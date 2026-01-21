using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures for Validation

public record HvValidatedRequest(string Value) : IRequest<string>;

public class HvValidatedRequestHandler : IRequestHandler<HvValidatedRequest, string>
{
    public Task<string> Handle(HvValidatedRequest request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);
}

public record HvOrphanRequest(int Id) : IRequest<int>;
// No handler registered for HvOrphanRequest — should fail validation

public record HvValidatedNotification(string Message) : INotification;

public class HvValidatedNotificationHandler : INotificationHandler<HvValidatedNotification>
{
    public Task Handle(HvValidatedNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public record HvOrphanNotification(string Message) : INotification;
// No handler for OrphanNotification — should produce a warning

#endregion

public class HandlerValidationTests
{
    [Fact]
    public void AssertHandlerRegistration_Succeeds_WhenAllHandlersRegistered()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<HvValidatedRequest, string>, HvValidatedRequestHandler>();
        services.AddTransient<INotificationHandler<HvValidatedNotification>, HvValidatedNotificationHandler>();
        var provider = services.BuildServiceProvider();

        // Should not throw — all handlers are properly registered
        provider.AssertHandlerRegistration(typeof(HvValidatedRequest), typeof(HvValidatedNotification));
    }

    [Fact]
    public void AssertHandlerRegistration_Throws_WhenRequestHasNoHandler()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        // Register handler for HvValidatedRequest but NOT HvOrphanRequest
        services.AddTransient<IRequestHandler<HvValidatedRequest, string>, HvValidatedRequestHandler>();
        var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.AssertHandlerRegistration(typeof(HvOrphanRequest)));

        Assert.Contains("HvOrphanRequest", ex.Message);
        Assert.Contains("no registered handler", ex.Message);
    }

    [Fact]
    public void AssertHandlerRegistration_IncludesWarning_WhenNotificationHasNoHandler()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        // Register request handler but no notification handlers for HvOrphanNotification
        // AND no handler for HvOrphanRequest — this causes the primary error
        services.AddTransient<IRequestHandler<HvValidatedRequest, string>, HvValidatedRequestHandler>();
        var provider = services.BuildServiceProvider();

        // HvOrphanRequest has no handler → error. HvOrphanNotification has no handler → warning.
        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.AssertHandlerRegistration(
                typeof(HvOrphanRequest), typeof(HvOrphanNotification)));

        Assert.Contains("HvOrphanRequest", ex.Message);
        Assert.Contains("HvOrphanNotification", ex.Message);
        Assert.Contains("warning", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
