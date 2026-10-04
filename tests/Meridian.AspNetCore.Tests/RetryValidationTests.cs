using System.Security.Claims;
using Meridian.AspNetCore.Infrastructure;
using Meridian.AspNetCore.Orders;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
namespace Meridian.AspNetCore.Tests;

[Trait("Provider", "Redis")]
public sealed class RetryValidationTests
{
    [Fact]
    public async Task Explicit_Test_Outer_Retry_Does_Not_Replay_PostCommit_Uncertainty()
    {
        await using var app = new ApiFixture(customize: services =>
        {
            services.AddScoped<IRequestHandler<RetryCreate, OrderDto>, RetryCreateHandler>();
            services.AddScoped<IPipelineBehavior<RetryCreate, OrderDto>, RetryBehavior<RetryCreate, OrderDto>>();
        });
        app.Signals.CompletionFailureKind = 1; using var client = app.Client(app.Token()); // Starts actual isolated host.
        await using var scope = app.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new("iss", LocalAuthSettings.Issuer), new("tenant", "A"), new(ClaimTypes.NameIdentifier, "1"), new("permission", "orders")], "local"))
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new RetryCreate(new CreateOrder("book", 2, "retry-test"))));
            Assert.Equal(1, app.Signals.RetryCalls); Assert.Equal(1, app.Signals.HandlerCalls); Assert.Equal((1, 1), await app.Rows());
        }
        finally { accessor.HttpContext = null; }
    }
    [Fact]
    public async Task Sample_Async_Validation_Observes_Cancellation()
    {
        var validator = new CreateOrderValidator(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validator.ValidateAsync(new CreateOrder("book", 1, "validation"), canceled.Token));
    }
}
public sealed record RetryCreate(CreateOrder Command) : IRequest<OrderDto>, IRetryableRequest { public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(1); }
public sealed class RetryCreateHandler(ISender sender, Signals signals) : IRequestHandler<RetryCreate, OrderDto>
{
    public Task<OrderDto> Handle(RetryCreate request, CancellationToken cancellationToken) { Interlocked.Increment(ref signals.RetryCalls); return sender.Send(request.Command, cancellationToken); }
}
