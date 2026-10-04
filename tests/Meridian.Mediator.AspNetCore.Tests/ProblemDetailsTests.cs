using System.Security.Claims;
using System.Text.Json;
using Meridian.Mediator.AspNetCore;
using Meridian.Mediator.Behaviors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.AspNetCore.Tests;

public sealed class ProblemDetailsTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [new ValidationException([new("secret-property", "secret-value")]), false, 400, "validation_failed"];
        yield return [new UnauthorizedException("secret-value"), false, 401, "authentication_required"];
        yield return [new UnauthorizedException("secret-value"), true, 403, "authorization_denied"];
        yield return [new MissingAuthorizationHandlerException(typeof(ProblemDetailsTests)), false, 500, "internal_error"];
        yield return [new MissingAuthorizationHandlerException(typeof(ProblemDetailsTests)), true, 500, "internal_error"];
        yield return [new IdempotencyInProgressException(typeof(ProblemDetailsTests)), true, 409, "idempotency_in_progress"];
        yield return [new IdempotencyOutcomeUnknownException(), true, 409, "idempotency_outcome_unknown"];
        yield return [new AggregateException(new ValidationException([]), new IOException("secret-value")), true, 500, "internal_error"];
        yield return [new AggregateException(new IdempotencyOutcomeUnknownException()), true, 500, "internal_error"];
        yield return [new JsonException("secret-value"), true, 500, "internal_error"];
        yield return [new IOException("secret-value"), true, 500, "internal_error"];
        yield return [new OperationCanceledException("secret-value"), true, 500, "internal_error"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Stable_Status_Code_And_Redaction(Exception error, bool authenticated, int status, string code)
    {
        await using var provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var context = Context(provider);
        context.TraceIdentifier = "safe-id";
        if (authenticated) context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, "user")], "test"));
        Assert.True(await provider.GetRequiredService<MeridianExceptionHandler>().TryHandleAsync(context, error, default));
        Assert.Equal(status, context.Response.StatusCode);
        var text = Body(context);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(context.Response.Headers["X-Correlation-ID"].ToString(), json.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("safe-id", json.RootElement.GetProperty("correlationId").GetString());
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.DoesNotContain("secret", text); Assert.DoesNotContain(nameof(ProblemDetailsTests), text); Assert.DoesNotContain("stack", text);
        Assert.False(json.RootElement.TryGetProperty("detail", out _) && code != "idempotency_outcome_unknown");
    }

    [Fact]
    public void Registration_ReturnsServices_And_IsIdempotent()
    {
        var services = Services(); var count = services.Count;
        Assert.Same(services, services.AddMeridianProblemDetails()); Assert.Equal(count, services.Count);
        Assert.Single(services.Where(x => x.ServiceType == typeof(IExceptionHandler)));
        Assert.Equal(ServiceLifetime.Singleton, services.Single(x => x.ServiceType == typeof(IExceptionHandler)).Lifetime);
        Assert.Throws<ArgumentNullException>(() => MeridianHttpExtensions.AddMeridianProblemDetails(null!));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("image/png")]
    public async Task Unsupported_Accept_Uses_Safe_Json_Fallback(string accept)
    {
        await using var provider = Services().BuildServiceProvider(); var context = Context(provider); context.Request.Headers.Accept = accept;
        await provider.GetRequiredService<MeridianExceptionHandler>().TryHandleAsync(context, new IOException("secret-value"), default);
        Assert.Equal(500, context.Response.StatusCode); Assert.Contains("internal_error", Body(context)); Assert.DoesNotContain("secret", Body(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_False_Or_Exception_Uses_Safe_Fallback(bool throws)
    {
        var services = Services(); var writer = new BrokenWriter(throws); services.AddSingleton<IProblemDetailsService>(writer);
        await using var provider = services.BuildServiceProvider(); var context = Context(provider);
        await provider.GetRequiredService<MeridianExceptionHandler>().TryHandleAsync(context, new IOException("secret-value"), default);
        Assert.Contains("internal_error", Body(context)); Assert.DoesNotContain("secret", Body(context)); Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public async Task Request_Abort_Does_Not_Write_500()
    {
        await using var provider = Services().BuildServiceProvider(); var context = Context(provider); context.RequestAborted = new(true);
        await provider.GetRequiredService<MeridianExceptionHandler>().TryHandleAsync(context, new OperationCanceledException(context.RequestAborted), context.RequestAborted);
        Assert.Equal("", Body(context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad,id")]
    [InlineData("bad\r\nid")]
    [InlineData("unicode-ş")]
    public async Task Unsafe_Request_Identifier_Is_Replaced(string id)
    {
        await using var provider = Services().BuildServiceProvider(); var context = Context(provider); context.TraceIdentifier = id;
        await provider.GetRequiredService<MeridianExceptionHandler>().TryHandleAsync(context, new Exception("secret-value"), default);
        using var json = JsonDocument.Parse(Body(context)); var safe = json.RootElement.GetProperty("correlationId").GetString();
        Assert.Equal(32, safe!.Length); Assert.Equal(context.Response.Headers["X-Correlation-ID"].ToString(), safe);
    }

    [Fact]
    public async Task Started_Response_Is_Aborted_Without_Rewriting()
    {
        await using var provider = Services().BuildServiceProvider(); var context = Context(provider);
        var feature = new StartedFeature(); context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(feature);
        await provider.GetRequiredService<MeridianExceptionHandler>().TryHandleAsync(context, new IOException("secret"), default);
        Assert.Equal(200, feature.StatusCode); Assert.Equal(0, feature.Body.Length);
    }
    [Fact]
    public void Application_Handlers_And_Writers_Are_Preserved()
    {
        var services = new ServiceCollection(); var writer = new BrokenWriter(false);
        services.AddSingleton<IExceptionHandler, ApplicationHandler>(); services.AddSingleton<IProblemDetailsService>(writer);
        services.AddMeridianProblemDetails(); services.AddMeridianProblemDetails();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Same(writer, provider.GetRequiredService<IProblemDetailsService>());
        var handlers = provider.GetServices<IExceptionHandler>().ToArray(); Assert.Equal(2, handlers.Length); Assert.IsType<ApplicationHandler>(handlers[0]); Assert.IsType<MeridianExceptionHandler>(handlers[1]);
    }
    [Fact]
    public async Task SingletonHandler_DoesNotCaptureScope()
    {
        var services = Services(); services.AddScoped<IProblemDetailsService, ScopedWriter>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var handler = provider.GetRequiredService<MeridianExceptionHandler>();
        await using var first = provider.CreateAsyncScope(); await using var second = provider.CreateAsyncScope();
        var firstWriter = first.ServiceProvider.GetRequiredService<IProblemDetailsService>();
        var secondWriter = second.ServiceProvider.GetRequiredService<IProblemDetailsService>();
        Assert.NotSame(firstWriter, secondWriter);
        await handler.TryHandleAsync(Context(first.ServiceProvider), new IOException("secret"), default);
        await handler.TryHandleAsync(Context(second.ServiceProvider), new IOException("secret"), default);
        Assert.Equal(1, ((ScopedWriter)firstWriter).Calls); Assert.Equal(1, ((ScopedWriter)secondWriter).Calls);
    }
    private sealed class ScopedWriter : IProblemDetailsService
    {
        public int Calls { get; private set; }
        public ValueTask WriteAsync(ProblemDetailsContext context) => throw new NotSupportedException();
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) { Calls++; return ValueTask.FromResult(false); }
    }
    private sealed class ApplicationHandler : IExceptionHandler
    { public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) => ValueTask.FromResult(false); }
    private sealed class StartedFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddMeridianProblemDetails();
        services.AddSingleton(sp => sp.GetServices<IExceptionHandler>().OfType<MeridianExceptionHandler>().Single()); return services;
    }
    private static DefaultHttpContext Context(IServiceProvider provider) => new() { RequestServices = provider, Response = { Body = new MemoryStream() } };
    private static string Body(HttpContext context) => System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    private sealed class BrokenWriter(bool throws) : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => throw new IOException("secret-writer");
        public int Calls { get; private set; }
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) { Calls++; context.ProblemDetails.Type = "secret-writer-mutation"; context.ProblemDetails.Extensions["secret"] = "secret-writer"; return throws ? throw new IOException("secret-writer") : ValueTask.FromResult(false); }
    }
}
