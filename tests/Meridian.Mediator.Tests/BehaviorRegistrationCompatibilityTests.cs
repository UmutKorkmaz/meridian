using System.Runtime.CompilerServices;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public sealed record ClosedApiRequest(string Value) : IRequest<string>;

public sealed class ClosedApiRequestHandler : IRequestHandler<ClosedApiRequest, string>
{
    public Task<string> Handle(ClosedApiRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"handled:{request.Value}");
    }
}

public sealed class ClosedApiBehavior : IPipelineBehavior<ClosedApiRequest, string>
{
    public static int InvocationCount { get; set; }

    public async Task<string> Handle(
        ClosedApiRequest request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        InvocationCount++;
        return await next().ConfigureAwait(false);
    }
}

public sealed record ClosedApiStream(int Count) : IStreamRequest<int>;

public sealed class ClosedApiStreamHandler : IStreamRequestHandler<ClosedApiStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        ClosedApiStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }
}

public sealed class ClosedApiStreamBehavior : IStreamPipelineBehavior<ClosedApiStream, int>
{
    public static int BeforeCount { get; set; }
    public static int AfterCount { get; set; }

    public async IAsyncEnumerable<int> Handle(
        ClosedApiStream request,
        StreamHandlerDelegate<int> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BeforeCount++;

        await foreach (var item in next().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }

        AfterCount++;
    }
}

[Collection("Pipeline")]
public class BehaviorRegistrationCompatibilityTests
{
    [Fact]
    public async Task AddClosedBehavior_Registers_Explicit_Service_Type()
    {
        ClosedApiBehavior.InvocationCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddClosedBehavior(typeof(IPipelineBehavior<ClosedApiRequest, string>), typeof(ClosedApiBehavior));
        });
        services.AddTransient<IRequestHandler<ClosedApiRequest, string>, ClosedApiRequestHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var result = await mediator.Send(new ClosedApiRequest("explicit"));

        Assert.Equal("handled:explicit", result);
        Assert.Equal(1, ClosedApiBehavior.InvocationCount);
    }

    [Fact]
    public async Task AddBehavior_Request_Type_Overload_Remains_Compatible()
    {
        ClosedApiBehavior.InvocationCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
#pragma warning disable CS0618
            cfg.AddBehavior(typeof(ClosedApiRequest), typeof(ClosedApiBehavior));
#pragma warning restore CS0618
        });
        services.AddTransient<IRequestHandler<ClosedApiRequest, string>, ClosedApiRequestHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var result = await mediator.Send(new ClosedApiRequest("request"));

        Assert.Equal("handled:request", result);
        Assert.Equal(1, ClosedApiBehavior.InvocationCount);
    }

    [Fact]
    public async Task AddBehavior_Service_Type_Overload_Remains_Compatible()
    {
        ClosedApiBehavior.InvocationCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
#pragma warning disable CS0618
            cfg.AddBehavior(typeof(IPipelineBehavior<ClosedApiRequest, string>), typeof(ClosedApiBehavior));
#pragma warning restore CS0618
        });
        services.AddTransient<IRequestHandler<ClosedApiRequest, string>, ClosedApiRequestHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var result = await mediator.Send(new ClosedApiRequest("service"));

        Assert.Equal("handled:service", result);
        Assert.Equal(1, ClosedApiBehavior.InvocationCount);
    }

    [Fact]
    public async Task AddClosedStreamBehavior_Registers_Explicit_Service_Type()
    {
        ClosedApiStreamBehavior.BeforeCount = 0;
        ClosedApiStreamBehavior.AfterCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddClosedStreamBehavior(typeof(IStreamPipelineBehavior<ClosedApiStream, int>), typeof(ClosedApiStreamBehavior));
        });
        services.AddTransient<IStreamRequestHandler<ClosedApiStream, int>, ClosedApiStreamHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var items = new List<int>();
        await foreach (var item in mediator.CreateStream(new ClosedApiStream(3)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2, 3 }, items);
        Assert.Equal(1, ClosedApiStreamBehavior.BeforeCount);
        Assert.Equal(1, ClosedApiStreamBehavior.AfterCount);
    }

    [Fact]
    public async Task AddStreamBehavior_Typed_Overload_Registers_Closed_Stream_Behavior()
    {
        ClosedApiStreamBehavior.BeforeCount = 0;
        ClosedApiStreamBehavior.AfterCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddStreamBehavior<ClosedApiStreamBehavior>();
        });
        services.AddTransient<IStreamRequestHandler<ClosedApiStream, int>, ClosedApiStreamHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var items = new List<int>();
        await foreach (var item in mediator.CreateStream(new ClosedApiStream(2)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2 }, items);
        Assert.Equal(1, ClosedApiStreamBehavior.BeforeCount);
        Assert.Equal(1, ClosedApiStreamBehavior.AfterCount);
    }

    [Fact]
    public async Task AddStreamBehavior_Request_Type_Overload_Remains_Compatible()
    {
        ClosedApiStreamBehavior.BeforeCount = 0;
        ClosedApiStreamBehavior.AfterCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
#pragma warning disable CS0618
            cfg.AddStreamBehavior(typeof(ClosedApiStream), typeof(ClosedApiStreamBehavior));
#pragma warning restore CS0618
        });
        services.AddTransient<IStreamRequestHandler<ClosedApiStream, int>, ClosedApiStreamHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var items = new List<int>();
        await foreach (var item in mediator.CreateStream(new ClosedApiStream(2)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2 }, items);
        Assert.Equal(1, ClosedApiStreamBehavior.BeforeCount);
        Assert.Equal(1, ClosedApiStreamBehavior.AfterCount);
    }

    [Fact]
    public async Task AddStreamBehavior_Service_Type_Overload_Remains_Compatible()
    {
        ClosedApiStreamBehavior.BeforeCount = 0;
        ClosedApiStreamBehavior.AfterCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
#pragma warning disable CS0618
            cfg.AddStreamBehavior(typeof(IStreamPipelineBehavior<ClosedApiStream, int>), typeof(ClosedApiStreamBehavior));
#pragma warning restore CS0618
        });
        services.AddTransient<IStreamRequestHandler<ClosedApiStream, int>, ClosedApiStreamHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var items = new List<int>();
        await foreach (var item in mediator.CreateStream(new ClosedApiStream(2)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2 }, items);
        Assert.Equal(1, ClosedApiStreamBehavior.BeforeCount);
        Assert.Equal(1, ClosedApiStreamBehavior.AfterCount);
    }
}
