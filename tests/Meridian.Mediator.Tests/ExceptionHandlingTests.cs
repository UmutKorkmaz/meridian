using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record ExceptionRequest(bool ShouldThrow) : IRequest<string>;

public class ExceptionRequestHandler : IRequestHandler<ExceptionRequest, string>
{
    public Task<string> Handle(ExceptionRequest request, CancellationToken cancellationToken)
    {
        if (request.ShouldThrow)
            throw new InvalidOperationException("Handler failed");
        return Task.FromResult("Success");
    }
}

public class TestExceptionHandler : IRequestExceptionHandler<ExceptionRequest, string, InvalidOperationException>
{
    public static bool WasCalled { get; set; }

    public Task Handle(ExceptionRequest request, InvalidOperationException exception,
        RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        WasCalled = true;
        state.SetHandled("Recovered");
        return Task.CompletedTask;
    }
}

public class NonHandlingExceptionHandler : IRequestExceptionHandler<ExceptionRequest, string, InvalidOperationException>
{
    public static bool WasCalled { get; set; }

    public Task Handle(ExceptionRequest request, InvalidOperationException exception,
        RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        WasCalled = true;
        // Don't call state.SetHandled - let it propagate
        return Task.CompletedTask;
    }
}

public class TestExceptionAction : IRequestExceptionAction<ExceptionRequest, InvalidOperationException>
{
    public static bool WasExecuted { get; set; }
    public static string? ReceivedMessage { get; set; }

    public Task Execute(ExceptionRequest request, InvalidOperationException exception,
        CancellationToken cancellationToken)
    {
        WasExecuted = true;
        ReceivedMessage = exception.Message;
        return Task.CompletedTask;
    }
}

#endregion

public class ExceptionHandlingTests
{
    [Fact]
    public async Task ExceptionHandler_CatchesException_ProvidesAlternateResponse()
    {
        TestExceptionHandler.WasCalled = false;
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<ExceptionRequest, string>, ExceptionRequestHandler>();
        services.AddTransient<IRequestExceptionHandler<ExceptionRequest, string, InvalidOperationException>,
            TestExceptionHandler>();
        services.AddTransient<IPipelineBehavior<ExceptionRequest, string>,
            RequestExceptionProcessorBehavior<ExceptionRequest, string>>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ExceptionRequest(true));

        Assert.True(TestExceptionHandler.WasCalled);
        Assert.Equal("Recovered", result);
    }

    [Fact]
    public void RequestExceptionHandlerState_SetHandled_Works()
    {
        var state = new RequestExceptionHandlerState<string>();
        Assert.False(state.Handled);
        Assert.Null(state.Response);

        state.SetHandled("MyResponse");

        Assert.True(state.Handled);
        Assert.Equal("MyResponse", state.Response);
    }

    [Fact]
    public async Task ExceptionAction_ExecutesSideEffects_OnException()
    {
        TestExceptionAction.WasExecuted = false;
        TestExceptionAction.ReceivedMessage = null;
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<ExceptionRequest, string>, ExceptionRequestHandler>();
        services.AddTransient<IRequestExceptionAction<ExceptionRequest, InvalidOperationException>,
            TestExceptionAction>();
        services.AddTransient<IPipelineBehavior<ExceptionRequest, string>,
            RequestExceptionProcessorBehavior<ExceptionRequest, string>>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        // Exception actions don't handle the exception - it still propagates
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new ExceptionRequest(true)));

        Assert.True(TestExceptionAction.WasExecuted);
        Assert.Equal("Handler failed", TestExceptionAction.ReceivedMessage);
    }

    [Fact]
    public async Task UnhandledException_PropagatesNormally_WithoutExceptionBehavior()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<ExceptionRequest, string>, ExceptionRequestHandler>();
        // No exception behavior registered
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new ExceptionRequest(true)));
        Assert.Equal("Handler failed", ex.Message);
    }

    [Fact]
    public async Task ExceptionHandler_ThatDoesNotSetHandled_LetsExceptionPropagate()
    {
        NonHandlingExceptionHandler.WasCalled = false;
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<ExceptionRequest, string>, ExceptionRequestHandler>();
        services.AddTransient<IRequestExceptionHandler<ExceptionRequest, string, InvalidOperationException>,
            NonHandlingExceptionHandler>();
        services.AddTransient<IPipelineBehavior<ExceptionRequest, string>,
            RequestExceptionProcessorBehavior<ExceptionRequest, string>>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new ExceptionRequest(true)));

        Assert.True(NonHandlingExceptionHandler.WasCalled);
    }
}
