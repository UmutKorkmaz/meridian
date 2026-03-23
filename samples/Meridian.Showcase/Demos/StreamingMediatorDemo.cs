using System.Runtime.CompilerServices;
using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Showcase;

public static class StreamingMediatorDemo
{
    public static async Task RunAsync()
    {
        ShowcaseOutput.WriteHeader("Streaming");

        var services = new ServiceCollection();
        services.AddSingleton<IMediatorLogger, ConsoleMediatorLogger>();

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ShowcaseAssemblyMarker>();
            cfg.AddOpenStreamBehavior(typeof(TracingStreamBehavior<,>));
        });

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var items = new List<string>();
        await foreach (var item in mediator.CreateStream(new OrderUpdateStream("ORD-1001", 3)))
        {
            items.Add(item);
        }

        Console.WriteLine($"Stream => {string.Join(" | ", items)}");
        Console.WriteLine();
    }
}

public record OrderUpdateStream(string OrderId, int Count) : IStreamRequest<string>;

public sealed class OrderUpdateStreamHandler : IStreamRequestHandler<OrderUpdateStream, string>
{
    public async IAsyncEnumerable<string> Handle(
        OrderUpdateStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 1; index <= request.Count; index++)
        {
            yield return $"{request.OrderId}:update-{index}";
            await Task.Delay(10, cancellationToken);
        }
    }
}

public sealed class TracingStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IMediatorLogger _logger;

    public TracingStreamBehavior(IMediatorLogger logger)
    {
        _logger = logger;
    }

    public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        return TraceAsync(request, next(), cancellationToken);
    }

    private async IAsyncEnumerable<TResponse> TraceAsync(
        TRequest request,
        IAsyncEnumerable<TResponse> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Streaming {RequestName}", typeof(TRequest).Name);

        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            _logger.LogInformation("Stream item from {RequestName}", typeof(TRequest).Name);
            yield return item;
        }
    }
}
