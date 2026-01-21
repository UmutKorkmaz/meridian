using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record Ping(string Message) : IRequest<string>;

public class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Pong: {request.Message}");
    }
}

public record VoidCommand(string Data) : IRequest;

public class VoidCommandHandler : IRequestHandler<VoidCommand, Unit>
{
    public static bool WasHandled { get; set; }
    public static string? ReceivedData { get; set; }

    public Task<Unit> Handle(VoidCommand request, CancellationToken cancellationToken)
    {
        WasHandled = true;
        ReceivedData = request.Data;
        return Unit.Task;
    }
}

public record CancellableQuery(string Value) : IRequest<string>;

public class CancellableQueryHandler : IRequestHandler<CancellableQuery, string>
{
    public async Task<string> Handle(CancellableQuery request, CancellationToken cancellationToken)
    {
        await Task.Delay(5000, cancellationToken);
        return request.Value;
    }
}

public record UnregisteredRequest : IRequest<int>;

#endregion

public class RequestResponseTests
{
    private IMediator BuildMediator(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<Ping, string>, PingHandler>();
        services.AddTransient<IRequestHandler<VoidCommand, Unit>, VoidCommandHandler>();
        services.AddTransient<IRequestHandler<CancellableQuery, string>, CancellableQueryHandler>();
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Send_IRequest_WithResponse_ReturnsCorrectResponse()
    {
        var mediator = BuildMediator();
        var result = await mediator.Send(new Ping("Hello"));
        Assert.Equal("Pong: Hello", result);
    }

    [Fact]
    public async Task Send_IRequest_Void_CompletesSuccessfully()
    {
        VoidCommandHandler.WasHandled = false;
        VoidCommandHandler.ReceivedData = null;

        var mediator = BuildMediator();
        await mediator.Send(new VoidCommand("test-data"));

        Assert.True(VoidCommandHandler.WasHandled);
    }

    [Fact]
    public async Task Send_Handler_ReceivesCorrectRequestData()
    {
        VoidCommandHandler.WasHandled = false;
        VoidCommandHandler.ReceivedData = null;

        var mediator = BuildMediator();
        await mediator.Send(new VoidCommand("specific-value"));

        Assert.Equal("specific-value", VoidCommandHandler.ReceivedData);
    }

    [Fact]
    public async Task Send_CancellationToken_IsPropagatedToHandler()
    {
        var mediator = BuildMediator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mediator.Send(new CancellableQuery("value"), cts.Token));
    }

    [Fact]
    public async Task Send_UnregisteredRequestType_Throws()
    {
        var mediator = BuildMediator();

        // No handler registered for UnregisteredRequest
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new UnregisteredRequest()));
    }

    [Fact]
    public async Task Send_NullRequest_ThrowsArgumentNullException()
    {
        var mediator = BuildMediator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Send<string>(null!));
    }

    [Fact]
    public async Task Send_ObjectOverload_ReturnsCorrectResponse()
    {
        var mediator = BuildMediator();
        var result = await mediator.Send((object)new Ping("ObjectSend"));
        Assert.Equal("Pong: ObjectSend", result);
    }
}
