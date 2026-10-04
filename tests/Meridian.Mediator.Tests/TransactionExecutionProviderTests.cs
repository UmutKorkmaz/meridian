using Meridian.Mediator.Behaviors;
namespace Meridian.Mediator.Tests;

public sealed class TransactionExecutionProviderTests
{
    [Fact]
    public async Task Rich_Provider_Owns_Lifecycle_And_Final_Failure_Is_Not_Retried()
    {
        var failure = new IOException("terminal");
        var provider = new ExecutionProvider(failure);
        var transaction = new TransactionBehavior<Command, int>(provider);
        var request = new Command();
        var calls = 0;
        Assert.Same(failure, await Record.ExceptionAsync(() => new RetryBehavior<Command, int>().Handle(request,
            () => transaction.Handle(request, () => { calls++; return Task.FromResult(1); }, default), default)));
        Assert.Equal(1, provider.Executions);
        Assert.Equal(1, calls);
        Assert.Equal(0, provider.Begins);
    }
    [Fact]
    public async Task Rich_Provider_Callback_Enforces_Request_Cancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var provider = new ExecutionProvider(null);
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TransactionBehavior<Command,int>(provider)
            .Handle(new(), () => { calls++; return Task.FromResult(1); }, cancellation.Token));
        Assert.Equal(0, calls);
        Assert.Equal(cancellation.Token, provider.Token);
    }
    private sealed class ExecutionProvider(Exception? failure) : ITransactionScopeProvider, ITransactionExecutionProvider
    {
        public int Begins; public int Executions; public CancellationToken Token;
        public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken) { Begins++; throw new InvalidOperationException("Legacy Begin must not be called"); }
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        { Executions++; Token = ct; var value = await action(ct); if (failure is not null) throw failure; return value; }
    }
    private sealed class Command : ITransactionalRequest, IRetryableRequest
    { public int MaxRetries => 2; public TimeSpan RetryDelay => TimeSpan.Zero; public bool ShouldRetry(Exception exception) => true; }
}
