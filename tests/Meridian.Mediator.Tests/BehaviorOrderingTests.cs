using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures for Ordering

public record OrderTestRequest(string Input) : IRequest<string>;

public class OrderTestHandler : IRequestHandler<OrderTestRequest, string>
{
    public Task<string> Handle(OrderTestRequest request, CancellationToken cancellationToken)
        => Task.FromResult($"Handled:{request.Input}");
}

public class AlphaBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public static List<string> Log { get; } = new();

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        Log.Add("Alpha-Before");
        var response = await next();
        Log.Add("Alpha-After");
        return response;
    }
}

public class BravoBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        AlphaBehavior<TRequest, TResponse>.Log.Add("Bravo-Before");
        var response = await next();
        AlphaBehavior<TRequest, TResponse>.Log.Add("Bravo-After");
        return response;
    }
}

public class CharlieBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        AlphaBehavior<TRequest, TResponse>.Log.Add("Charlie-Before");
        var response = await next();
        AlphaBehavior<TRequest, TResponse>.Log.Add("Charlie-After");
        return response;
    }
}

#endregion

[Collection("BehaviorOrder")]
public class BehaviorOrderingTests
{
    private void ClearLog() => AlphaBehavior<OrderTestRequest, string>.Log.Clear();
    private List<string> Log => AlphaBehavior<OrderTestRequest, string>.Log;

    [Fact]
    public async Task Behaviors_With_Same_Order_Preserve_Registration_Sequence()
    {
        ClearLog();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddOpenBehavior(typeof(AlphaBehavior<,>), order: 0);
            cfg.AddOpenBehavior(typeof(BravoBehavior<,>), order: 0);
        });
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new OrderTestRequest("Test"));

        // Same order → registration sequence preserved: Alpha first, Bravo second
        Assert.Equal("Alpha-Before", Log[0]);
        Assert.Equal("Bravo-Before", Log[1]);
        Assert.Equal("Bravo-After", Log[2]);
        Assert.Equal("Alpha-After", Log[3]);
    }

    [Fact]
    public async Task Lower_Order_Runs_First_Regardless_Of_Registration_Sequence()
    {
        ClearLog();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            // Register Bravo first, but give it higher order
            cfg.AddOpenBehavior(typeof(BravoBehavior<,>), order: 10);
            cfg.AddOpenBehavior(typeof(AlphaBehavior<,>), order: 1);
        });
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new OrderTestRequest("Test"));

        // Alpha (order 1) should be outermost, Bravo (order 10) innermost
        Assert.Equal("Alpha-Before", Log[0]);
        Assert.Equal("Bravo-Before", Log[1]);
        Assert.Equal("Bravo-After", Log[2]);
        Assert.Equal("Alpha-After", Log[3]);
    }

    [Fact]
    public async Task Three_Behaviors_Ordered_Correctly()
    {
        ClearLog();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            // Register in reverse order but assign proper ordering
            cfg.AddOpenBehavior(typeof(CharlieBehavior<,>), order: 30);
            cfg.AddOpenBehavior(typeof(BravoBehavior<,>), order: 20);
            cfg.AddOpenBehavior(typeof(AlphaBehavior<,>), order: 10);
        });
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new OrderTestRequest("Test"));

        // Expected pipeline: Alpha → Bravo → Charlie → Handler
        Assert.Equal(6, Log.Count);
        Assert.Equal("Alpha-Before", Log[0]);
        Assert.Equal("Bravo-Before", Log[1]);
        Assert.Equal("Charlie-Before", Log[2]);
        Assert.Equal("Charlie-After", Log[3]);
        Assert.Equal("Bravo-After", Log[4]);
        Assert.Equal("Alpha-After", Log[5]);
    }

    [Fact]
    public async Task Negative_Order_Runs_Before_Default()
    {
        ClearLog();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddOpenBehavior(typeof(BravoBehavior<,>));         // default order = 0
            cfg.AddOpenBehavior(typeof(AlphaBehavior<,>), order: -10); // should run first
        });
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new OrderTestRequest("Test"));

        // Alpha (order -10) outermost, Bravo (order 0) innermost
        Assert.Equal("Alpha-Before", Log[0]);
        Assert.Equal("Bravo-Before", Log[1]);
        Assert.Equal("Bravo-After", Log[2]);
        Assert.Equal("Alpha-After", Log[3]);
    }

    [Fact]
    public async Task Default_Order_Is_Zero_Behaviors_Run_In_Registration_Order()
    {
        ClearLog();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            // All default order (0) — should run in registration order
            cfg.AddOpenBehavior(typeof(AlphaBehavior<,>));
            cfg.AddOpenBehavior(typeof(BravoBehavior<,>));
            cfg.AddOpenBehavior(typeof(CharlieBehavior<,>));
        });
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new OrderTestRequest("Test"));

        // All order=0 → registration order: Alpha, Bravo, Charlie
        Assert.Equal("Alpha-Before", Log[0]);
        Assert.Equal("Bravo-Before", Log[1]);
        Assert.Equal("Charlie-Before", Log[2]);
    }
}
