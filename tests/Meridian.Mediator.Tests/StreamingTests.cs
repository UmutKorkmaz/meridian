using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record NumberStream(int Count) : IStreamRequest<int>;

public class NumberStreamHandler : IStreamRequestHandler<NumberStream, int>
{
    public async IAsyncEnumerable<int> Handle(NumberStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }
}

public record SlowStream : IStreamRequest<string>;

public class SlowStreamHandler : IStreamRequestHandler<SlowStream, string>
{
    public async IAsyncEnumerable<string> Handle(SlowStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return "first";
        await Task.Delay(5000, cancellationToken);
        yield return "second";
    }
}

#endregion

public class StreamingTests
{
    private Meridian.Mediator.Mediator BuildMediator()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IStreamRequestHandler<NumberStream, int>, NumberStreamHandler>();
        services.AddTransient<IStreamRequestHandler<SlowStream, string>, SlowStreamHandler>();
        var provider = services.BuildServiceProvider();
        return (Meridian.Mediator.Mediator)provider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task CreateStream_ReturnsCorrectItems()
    {
        var mediator = BuildMediator();
        var items = new List<int>();

        await foreach (var item in mediator.CreateStream(new NumberStream(3)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2, 3 }, items);
    }

    [Fact]
    public async Task CreateStream_YieldsMultipleItems()
    {
        var mediator = BuildMediator();
        var count = 0;

        await foreach (var _ in mediator.CreateStream(new NumberStream(5)))
        {
            count++;
        }

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task CreateStream_CancellationToken_CancelsIteration()
    {
        var mediator = BuildMediator();
        using var cts = new CancellationTokenSource();

        var items = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in mediator.CreateStream(new SlowStream(), cts.Token))
            {
                items.Add(item);
                cts.Cancel(); // Cancel after first item
            }
        });

        Assert.Single(items);
        Assert.Equal("first", items[0]);
    }

    [Fact]
    public async Task CreateStream_ObjectOverload_ReturnsItems()
    {
        var mediator = BuildMediator();
        var items = new List<object?>();

        await foreach (var item in mediator.CreateStream((object)new NumberStream(2)))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0]);
        Assert.Equal(2, items[1]);
    }

    [Fact]
    public void CreateStream_NullRequest_ThrowsArgumentNullException()
    {
        var mediator = BuildMediator();
        Assert.Throws<ArgumentNullException>(() => mediator.CreateStream<int>(null!));
    }
}
