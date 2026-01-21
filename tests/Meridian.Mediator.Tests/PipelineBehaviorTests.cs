using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record PipelineRequest(string Input) : IRequest<string>;

public class PipelineRequestHandler : IRequestHandler<PipelineRequest, string>
{
    public Task<string> Handle(PipelineRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Handled:{request.Input}");
    }
}

public class LoggingBehavior : IPipelineBehavior<PipelineRequest, string>
{
    public static List<string> Log { get; } = new();

    public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        Log.Add("Before");
        var response = await next();
        Log.Add($"After:{response}");
        return response;
    }
}

public class OuterBehavior : IPipelineBehavior<PipelineRequest, string>
{
    public static List<string> Log => LoggingBehavior.Log;

    public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        Log.Add("Outer-Before");
        var response = await next();
        Log.Add("Outer-After");
        return response;
    }
}

public class InnerBehavior : IPipelineBehavior<PipelineRequest, string>
{
    public static List<string> Log => LoggingBehavior.Log;

    public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        Log.Add("Inner-Before");
        var response = await next();
        Log.Add("Inner-After");
        return response;
    }
}

public class ShortCircuitBehavior : IPipelineBehavior<PipelineRequest, string>
{
    public Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        // Don't call next() - short-circuit
        return Task.FromResult("ShortCircuited");
    }
}

public class ResponseModifyBehavior : IPipelineBehavior<PipelineRequest, string>
{
    public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        var response = await next();
        return $"Modified({response})";
    }
}

// Open generic behavior for testing
public class OpenGenericBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public static int CallCount { get; set; }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return await next();
    }
}

#endregion

[Collection("Pipeline")]
public class PipelineBehaviorTests
{
    [Fact]
    public async Task SingleBehavior_WrapsHandler_PreAndPostLogic()
    {
        LoggingBehavior.Log.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(PipelineBehaviorTests).Assembly));
        // Re-register just the logging behavior (assembly scan picks up all behaviors,
        // so we build a focused container)
        var focused = new ServiceCollection();
        focused.AddMeridianMediator(cfg => { });
        focused.AddTransient<IRequestHandler<PipelineRequest, string>, PipelineRequestHandler>();
        focused.AddTransient<IPipelineBehavior<PipelineRequest, string>, LoggingBehavior>();
        var provider = focused.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new PipelineRequest("Test"));

        Assert.Equal("Handled:Test", result);
        Assert.Equal("Before", LoggingBehavior.Log[0]);
        Assert.Equal("After:Handled:Test", LoggingBehavior.Log[1]);
    }

    [Fact]
    public async Task MultipleBehaviors_ExecuteInRegistrationOrder()
    {
        LoggingBehavior.Log.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<PipelineRequest, string>, PipelineRequestHandler>();
        services.AddTransient<IPipelineBehavior<PipelineRequest, string>, OuterBehavior>();
        services.AddTransient<IPipelineBehavior<PipelineRequest, string>, InnerBehavior>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new PipelineRequest("X"));

        // Reverse() + Aggregate: first-registered (Outer) becomes outermost,
        // last-registered (Inner) becomes closest to handler
        Assert.Equal(4, LoggingBehavior.Log.Count);
        Assert.Equal("Outer-Before", LoggingBehavior.Log[0]);
        Assert.Equal("Inner-Before", LoggingBehavior.Log[1]);
        Assert.Equal("Inner-After", LoggingBehavior.Log[2]);
        Assert.Equal("Outer-After", LoggingBehavior.Log[3]);
    }

    [Fact]
    public async Task Behavior_CanShortCircuit_ByNotCallingNext()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<PipelineRequest, string>, PipelineRequestHandler>();
        services.AddTransient<IPipelineBehavior<PipelineRequest, string>, ShortCircuitBehavior>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new PipelineRequest("Ignored"));

        Assert.Equal("ShortCircuited", result);
    }

    [Fact]
    public async Task Behavior_CanModifyResponse_AfterHandler()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<PipelineRequest, string>, PipelineRequestHandler>();
        services.AddTransient<IPipelineBehavior<PipelineRequest, string>, ResponseModifyBehavior>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new PipelineRequest("Data"));

        Assert.Equal("Modified(Handled:Data)", result);
    }

    [Fact]
    public async Task OpenGenericBehavior_WorksWithAnyRequestType()
    {
        OpenGenericBehavior<PipelineRequest, string>.CallCount = 0;
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddOpenBehavior(typeof(OpenGenericBehavior<,>));
        });
        services.AddTransient<IRequestHandler<PipelineRequest, string>, PipelineRequestHandler>();
        services.AddTransient<IRequestHandler<Ping, string>, PingHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new PipelineRequest("A"));
        Assert.Equal(1, OpenGenericBehavior<PipelineRequest, string>.CallCount);

        // Also works with a different request type (Ping)
        OpenGenericBehavior<Ping, string>.CallCount = 0;
        await mediator.Send(new Ping("B"));
        Assert.Equal(1, OpenGenericBehavior<Ping, string>.CallCount);
    }
}
