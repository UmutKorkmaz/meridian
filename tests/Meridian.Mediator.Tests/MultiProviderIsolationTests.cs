using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public class MultiProviderIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_Request_Type_In_Two_Providers_Uses_Own_Handler(bool objectSend)
    {
        using var providerA = BuildProvider<ProviderAHandler>("provider-A");
        using var providerB = BuildProvider<ProviderBHandler>("provider-B");
        for (var round = 0; round < 2; round++)
        {
            using var scopeA = providerA.CreateScope();
            using var scopeB = providerB.CreateScope();
            var barrier = new DispatchBarrier();
            var request = new IsolationRequest(barrier);
            var mediatorA = scopeA.ServiceProvider.GetRequiredService<ISender>();
            var mediatorB = scopeB.ServiceProvider.GetRequiredService<ISender>();
            var sendA = objectSend ? mediatorA.Send((object)request) : Box(mediatorA.Send(request));
            var sendB = objectSend ? mediatorB.Send((object)request) : Box(mediatorB.Send(request));
            await barrier.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            barrier.Release.TrySetResult();

            Assert.Equal("provider-A", await sendA);
            Assert.Equal("provider-B", await sendB);
            var stateA = scopeA.ServiceProvider.GetRequiredService<ScopeState>();
            var stateB = scopeB.ServiceProvider.GetRequiredService<ScopeState>();
            Assert.NotSame(stateA, stateB);
            Assert.Equal(new[] { "behavior-before", "handler-A", "behavior-after" }, stateA.Events);
            Assert.Equal(new[] { "behavior-before", "handler-B", "behavior-after" }, stateB.Events);
        }
    }

    private static async Task<object?> Box(Task<string> response) => await response;

    private static ServiceProvider BuildProvider<THandler>(string name)
        where THandler : class, IRequestHandler<IsolationRequest, string>
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration => { });
        services.AddSingleton(new ProviderName(name));
        services.AddScoped<ScopeState>();
        services.AddScoped<IRequestHandler<IsolationRequest, string>, THandler>();
        services.AddScoped<IPipelineBehavior<IsolationRequest, string>, IsolationBehavior>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed record ProviderName(string Value);
    public sealed class ScopeState
    {
        public List<string> Events { get; } = new();
    }
    public sealed class DispatchBarrier
    {
        private int _entered;
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Enter(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == 2)
                BothEntered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
    public sealed record IsolationRequest(DispatchBarrier Barrier) : IRequest<string>;
    public sealed class ProviderAHandler(ProviderName name, ScopeState state) : IRequestHandler<IsolationRequest, string>
    {
        public async Task<string> Handle(IsolationRequest request, CancellationToken cancellationToken)
        {
            state.Events.Add("handler-A");
            await request.Barrier.Enter(cancellationToken);
            return name.Value;
        }
    }
    public sealed class ProviderBHandler(ProviderName name, ScopeState state) : IRequestHandler<IsolationRequest, string>
    {
        public async Task<string> Handle(IsolationRequest request, CancellationToken cancellationToken)
        {
            state.Events.Add("handler-B");
            await request.Barrier.Enter(cancellationToken);
            return name.Value;
        }
    }
    public sealed class IsolationBehavior(ScopeState state) : IPipelineBehavior<IsolationRequest, string>
    {
        public async Task<string> Handle(IsolationRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            state.Events.Add("behavior-before");
            var response = await next();
            state.Events.Add("behavior-after");
            return response;
        }
    }
}
