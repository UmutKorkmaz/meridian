using Meridian.Mediator.Behaviors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.AspNetCore;

/// <summary>Final redacting exception handler. Register after intentional application exception handlers.</summary>
public sealed class MeridianExceptionHandler : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        if (httpContext.RequestAborted.IsCancellationRequested || httpContext.Response.HasStarted)
        {
            httpContext.Abort();
            return true;
        }

        var (status, code, title) = Classify(exception, httpContext.User.Identities.Any(x => x.IsAuthenticated));
        var id = SafeIdentifier(httpContext.TraceIdentifier) ? httpContext.TraceIdentifier : Guid.NewGuid().ToString("N");
        httpContext.TraceIdentifier = id;
        var problem = new ProblemDetails { Status = status, Title = title, Type = "urn:meridian:problem:" + code };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = id;
        if (code == "idempotency_outcome_unknown") problem.Detail = "The operation requires reconciliation before any retry.";
        SetHeaders(httpContext, status, id);
        try
        {
            var writer = httpContext.RequestServices.GetService<IProblemDetailsService>();
            if (writer is not null && await writer.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem }))
                return true;
        }
        catch (Exception) when (!httpContext.RequestAborted.IsCancellationRequested && !httpContext.Response.HasStarted)
        {
            // A failed application writer must not escape into a development error page.
        }
        catch (Exception)
        {
            httpContext.Abort();
            return true;
        }

        if (httpContext.Response.HasStarted) { httpContext.Abort(); return true; }
        try
        {
            // Discard custom writer mutations; the fallback contains only the fixed public contract.
            httpContext.Response.Clear();
            SetHeaders(httpContext, status, id);
            await httpContext.Response.WriteAsJsonAsync(new PublicProblem("urn:meridian:problem:" + code, title, status, code, id,
                code == "idempotency_outcome_unknown" ? "The operation requires reconciliation before any retry." : null),
                options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        }
        catch (Exception) { httpContext.Abort(); }
        return true;
    }

    private static void SetHeaders(HttpContext context, int status, string id)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Correlation-ID"] = id;
    }
    private static bool SafeIdentifier(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static (int Status, string Code, string Title) Classify(Exception error, bool authenticated) => error switch
    {
        MissingAuthorizationHandlerException => (500, "internal_error", "An internal error occurred."),
        ValidationException => (400, "validation_failed", "Request validation failed."),
        UnauthorizedException when !authenticated => (401, "authentication_required", "Authentication is required."),
        UnauthorizedException => (403, "authorization_denied", "Access is denied."),
        IdempotencyInProgressException => (409, "idempotency_in_progress", "The operation is in progress."),
        IdempotencyOutcomeUnknownException => (409, "idempotency_outcome_unknown", "The operation outcome is unknown."),
        _ => (500, "internal_error", "An internal error occurred.")
    };
    private sealed record PublicProblem(string Type, string Title, int Status, string Code, string CorrelationId,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Detail);
}
