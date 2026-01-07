using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record NestedOuterRequest(string Value) : IRequest<string>;

public class NestedOuterHandler : IRequestHandler<NestedOuterRequest, string>
{
    private readonly ISender _sender;

    public NestedOuterHandler(ISender sender)
    {
        _sender = sender;
    }

    public async Task<string> Handle(NestedOuterRequest request, CancellationToken cancellationToken)
    {
        var innerResult = await _sender.Send(new Ping(request.Value), cancellationToken);
        return $"Outer({innerResult})";
    }
}

public record IntRequest(int Value) : IRequest<int>;

public class IntRequestHandler : IRequestHandler<IntRequest, int>
{
    public Task<int> Handle(IntRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request.Value * 3);
    }
}

#endregion

public class EdgeCaseTests
{
    private IMediator BuildMediator(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<Ping, string>, PingHandler>();
        services.AddTransient<IRequestHandler<VoidCommand, Unit>, VoidCommandHandler>();
        services.AddTransient<IRequestHandler<NestedOuterRequest, string>, NestedOuterHandler>();
        services.AddTransient<IRequestHandler<IntRequest, int>, IntRequestHandler>();
        services.AddTransient<IRequestHandler<DiRequest, int>, DiRequestHandler>();
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task NullRequest_ThrowsArgumentNullException_TypedOverload()
    {
        var mediator = BuildMediator();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Send<string>(null!));
    }

    [Fact]
    public async Task NullRequest_ThrowsArgumentNullException_ObjectOverload()
    {
        var mediator = BuildMediator();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Send((object)null!));
    }

    [Fact]
    public async Task NullRequest_ThrowsArgumentNullException_VoidOverload()
    {
        var mediator = BuildMediator();
        VoidCommand nullCmd = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Send(nullCmd));
    }

    [Fact]
    public async Task HandlerWithConstructorDependencies_ResolvedCorrectly()
    {
        var mediator = BuildMediator(services =>
        {
            services.AddTransient<IMultiplier, DoubleMultiplier>();
        });

        var result = await mediator.Send(new DiRequest(7));
        Assert.Equal(14, result);
    }

    [Fact]
    public async Task NestedSend_HandlerSendsAnotherRequest_Works()
    {
        var mediator = BuildMediator();

        var result = await mediator.Send(new NestedOuterRequest("nested"));
        Assert.Equal("Outer(Pong: nested)", result);
    }

    [Fact]
    public async Task ConcurrentSend_IsThreadSafe()
    {
        var mediator = BuildMediator();
        var tasks = Enumerable.Range(1, 100)
            .Select(i => mediator.Send(new Ping($"msg-{i}")));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(100, results.Length);
        for (int i = 1; i <= 100; i++)
        {
            Assert.Contains($"Pong: msg-{i}", results);
        }
    }

    [Fact]
    public void UnitType_Equality_Works()
    {
        Assert.Equal(Unit.Value, Unit.Value);
        Assert.True(Unit.Value == default);
        Assert.Equal("()", Unit.Value.ToString());
        Assert.Equal(0, Unit.Value.GetHashCode());
        Assert.True(Unit.Value.Equals(Unit.Value));
        Assert.False(Unit.Value.Equals("not a unit"));
    }

    [Fact]
    public async Task Send_MultipleRequestTypes_UseCorrectHandlers()
    {
        var mediator = BuildMediator();

        var stringResult = await mediator.Send(new Ping("hello"));
        var intResult = await mediator.Send(new IntRequest(4));

        Assert.Equal("Pong: hello", stringResult);
        Assert.Equal(12, intResult);
    }

    [Fact]
    public async Task Send_UnitReturn_FromVoidHandler()
    {
        var mediator = BuildMediator();

        // IRequest inherits from IRequest<Unit>
        // The void Send method returns Task, but internally it works with Unit
        VoidCommandHandler.WasHandled = false;
        await mediator.Send(new VoidCommand("unit-test"));
        Assert.True(VoidCommandHandler.WasHandled);
    }
}
