using System.Runtime.CompilerServices;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public class StreamLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_Stream_Disposes_After_Enumeration(bool objectStream)
    {
        using var provider = BuildProvider();
        var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<StreamState>();
        try
        {
            var sender = scope.ServiceProvider.GetRequiredService<IStreamSender>();
            await foreach (var item in Create(sender, new LifetimeRequest(), objectStream))
            {
                Assert.Equal(1, item);
                Assert.False(state.HandlerDisposed);
                Assert.False(state.OuterDisposed);
                Assert.False(state.InnerDisposed);
                break;
            }

            Assert.Equal(1, state.ResourcesReleased);
            Assert.Equal(new[] { "outer-before", "inner-before", "handler-before", "handler-finally",
                "inner-finally", "outer-finally" }, state.Events);
            Assert.False(state.HandlerDisposed);
            Assert.False(state.OuterDisposed);
            Assert.False(state.InnerDisposed);

            // The caller still owns the scope and can reuse its scoped handler.
            var items = new List<object?>();
            await foreach (var item in Create(sender, new LifetimeRequest(), objectStream))
                items.Add(item);
            Assert.Equal(new object?[] { 1, 2 }, items);
            Assert.Equal(2, state.ResourcesReleased);
            Assert.False(state.HandlerDisposed);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.True(state.HandlerDisposed);
        Assert.True(state.OuterDisposed);
        Assert.True(state.InnerDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_Stream_Async_Failure_Releases_Resources_And_Preserves_Original(bool objectStream)
    {
        using var provider = BuildProvider();
        var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<StreamState>();
        var failure = new InvalidOperationException("stream failure");
        var items = new List<object?>();
        try
        {
            var sender = scope.ServiceProvider.GetRequiredService<IStreamSender>();
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var item in Create(sender, new LifetimeRequest(failure), objectStream))
                    items.Add(item);
            });
            Assert.Same(failure, observed);
            Assert.Equal(new object?[] { 1 }, items);
            Assert.Equal(1, state.ResourcesReleased);
            Assert.Equal("outer-finally", state.Events[^1]);
            Assert.False(state.HandlerDisposed);
        }
        finally
        {
            scope.Dispose();
        }
        Assert.True(state.HandlerDisposed);
        Assert.True(state.OuterDisposed);
        Assert.True(state.InnerDisposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scoped_Stream_Cancellation_Before_Or_After_First_Yield_Releases_Resources(bool objectStream, bool afterYield)
    {
        using var provider = BuildProvider();
        var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<StreamState>();
        using var cancellation = new CancellationTokenSource();
        var items = new List<object?>();
        if (!afterYield)
            cancellation.Cancel();
        try
        {
            var sender = scope.ServiceProvider.GetRequiredService<IStreamSender>();
            var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var item in Create(sender, new LifetimeRequest(), objectStream, cancellation.Token))
                {
                    items.Add(item);
                    cancellation.Cancel();
                }
            });
            Assert.Equal(cancellation.Token, observed.CancellationToken);
            Assert.Equal(afterYield ? new object?[] { 1 } : Array.Empty<object?>(), items);
            Assert.Equal(1, state.ResourcesReleased);
            Assert.Equal("outer-finally", state.Events[^1]);
            Assert.False(state.HandlerDisposed);
        }
        finally
        {
            scope.Dispose();
        }
        Assert.True(state.HandlerDisposed);
        Assert.True(state.OuterDisposed);
        Assert.True(state.InnerDisposed);
    }

    private static async IAsyncEnumerable<object?> Create(IStreamSender sender, LifetimeRequest request,
        bool objectStream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (objectStream)
        {
            await foreach (var item in sender.CreateStream((object)request, cancellationToken))
                yield return item;
        }
        else
        {
            await foreach (var item in sender.CreateStream(request, cancellationToken))
                yield return item;
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration => { });
        services.AddScoped<StreamState>();
        services.AddScoped<IStreamRequestHandler<LifetimeRequest, int>, ScopedStreamHandler>();
        services.AddScoped<IStreamPipelineBehavior<LifetimeRequest, int>, OuterStreamBehavior>();
        services.AddScoped<IStreamPipelineBehavior<LifetimeRequest, int>, InnerStreamBehavior>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed record LifetimeRequest(Exception? Failure = null) : IStreamRequest<int>;
    public sealed class StreamState
    {
        public List<string> Events { get; } = new();
        public int ResourcesReleased { get; set; }
        public bool HandlerDisposed { get; set; }
        public bool OuterDisposed { get; set; }
        public bool InnerDisposed { get; set; }
    }
    public sealed class ScopedStreamHandler(StreamState state) : IStreamRequestHandler<LifetimeRequest, int>, IDisposable
    {
        public async IAsyncEnumerable<int> Handle(LifetimeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(state.HandlerDisposed, this);
            state.Events.Add("handler-before");
            using var resource = new EnumerationResource(state);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return 1;
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Failure is not null)
                    throw request.Failure;
                yield return 2;
            }
            finally
            {
                state.Events.Add("handler-finally");
            }
        }
        public void Dispose() => state.HandlerDisposed = true;
    }
    private sealed class EnumerationResource(StreamState state) : IDisposable
    {
        public void Dispose() => state.ResourcesReleased++;
    }
    public sealed class OuterStreamBehavior(StreamState state) : IStreamPipelineBehavior<LifetimeRequest, int>, IDisposable
    {
        public async IAsyncEnumerable<int> Handle(LifetimeRequest request, StreamHandlerDelegate<int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(state.OuterDisposed, this);
            state.Events.Add("outer-before");
            try
            {
                await foreach (var item in next().WithCancellation(cancellationToken))
                    yield return item;
            }
            finally { state.Events.Add("outer-finally"); }
        }
        public void Dispose() => state.OuterDisposed = true;
    }
    public sealed class InnerStreamBehavior(StreamState state) : IStreamPipelineBehavior<LifetimeRequest, int>, IDisposable
    {
        public async IAsyncEnumerable<int> Handle(LifetimeRequest request, StreamHandlerDelegate<int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(state.InnerDisposed, this);
            state.Events.Add("inner-before");
            try
            {
                await foreach (var item in next().WithCancellation(cancellationToken))
                    yield return item;
            }
            finally { state.Events.Add("inner-finally"); }
        }
        public void Dispose() => state.InnerDisposed = true;
    }
}
