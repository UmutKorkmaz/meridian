using System.Runtime.ExceptionServices;
using System.Text.Json;
using Meridian.AspNetCore.Orders;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.AspNetCore.Infrastructure;

public sealed class OrderCorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var preceding = CorrelationContext.CorrelationId;
        var id = Guid.NewGuid().ToString("N");
        context.TraceIdentifier = id; CorrelationContext.CorrelationId = id;
        context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-ID"] = id; if (!context.Response.Headers.ContainsKey("Cache-Control")) context.Response.Headers.CacheControl = "private, no-store"; return Task.CompletedTask; });
        try { await next(context); }
        finally { CorrelationContext.CorrelationId = preceding; }
    }
}
public static class OrderHttp
{
    public static Task WriteProblem(HttpContext context, int status)
    {
        var (code, title) = status switch
        {
            400 => ("invalid_request", "The request is invalid."),
            401 => ("authentication_required", "Authentication is required."),
            403 => ("authorization_denied", "Access is denied."),
            404 => ("not_found", "The resource was not found."),
            _ => ("internal_error", "An internal error occurred.")
        };
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { type = "urn:meridian:problem:" + code, title, status, code, correlationId = context.TraceIdentifier },
            options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var orders = app.MapGroup("/orders").RequireAuthorization();
        orders.MapPost("", async (HttpContext context, CreateOrderBody body, ISender sender) =>
        {
            var keys = context.Request.Headers["Idempotency-Key"];
            if (keys.Count != 1 || keys[0] is not { Length: > 0 and <= 128 } key || !key.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'))
                return Results.StatusCode(400);
            var dto = await sender.Send(new CreateOrder(body.Product, body.Quantity, key), context.RequestAborted);
            return Results.Created("/orders?orderId=" + dto.Id.ToString("D"), dto);
        }).Produces<OrderDto>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(500)
          .WithDescription("409 codes: idempotency_in_progress or idempotency_outcome_unknown. A key identifies the original immutable submission, including replay after changed input.");
        orders.MapGet("", async (Guid? orderId, ISender sender, CancellationToken token) =>
        {
            if (orderId is null) return Results.Ok(await sender.Send(new ListOrders(), token));
            var dto = await sender.Send(new GetOrder(orderId.Value), token);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).Produces<OrderDto[]>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(500)
          .WithDescription("Without orderId: 200 returns an OrderDto array. With orderId: 200 returns one OrderDto object, or 404 when it is not visible to the caller.")
          .AddOpenApiOperationTransformer(DescribeListOrSingle);
        orders.MapGet("/events", Stream).Produces<OrderEventDto>(200, "application/x-ndjson")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(500)
            .WithDescription("One committed scoped OrderEventDto JSON object per newline. Cancellation disposes enumeration. Post-start failures abort the transport; clients must detect truncation. Status errors apply before streaming starts.");
    }
    // GET /orders keeps one route with two 200 shapes; describe both instead of only the array.
    private static Task DescribeListOrSingle(Microsoft.OpenApi.OpenApiOperation operation, Microsoft.AspNetCore.OpenApi.OpenApiOperationTransformerContext context, CancellationToken token)
    {
        if (operation.Responses?.TryGetValue("200", out var ok) == true && ok.Content?.TryGetValue("application/json", out var media) == true
            && media.Schema is { Items: { } item } list)
            media.Schema = new Microsoft.OpenApi.OpenApiSchema { OneOf = [list, item] };
        return Task.CompletedTask;
    }
    private static async Task Stream(HttpContext context, IStreamSender sender, AuthenticatedOrderScope scope, long? after)
    {
        if (after < 0) { context.Response.StatusCode = 400; return; }
        scope.Authorize(); // Normal request behaviors do not run for stream requests.
        var enumerator = sender.CreateStream(new WatchOrders(after ?? 0), context.RequestAborted).GetAsyncEnumerator(context.RequestAborted);
        List<Exception> failures = [];
        try
        {
            var available = await enumerator.MoveNextAsync(); // Initial errors still permit ProblemDetails.
            context.Response.ContentType = "application/x-ndjson";
            while (available)
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(enumerator.Current, OrdersJsonContext.Default.OrderEventDto) + "\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                available = await enumerator.MoveNextAsync();
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { await enumerator.DisposeAsync(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 0) return;
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Meridian.Orders.Stream")
                .LogWarning("Stream failed after start or disconnect. Code=stream_failure CorrelationId={CorrelationId} Failures={Count}", context.TraceIdentifier, failures.Count);
            context.Abort(); return;
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Stream operation and cleanup failed.", failures);
    }
}
