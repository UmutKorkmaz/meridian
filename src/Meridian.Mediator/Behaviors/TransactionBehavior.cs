using System.Runtime.ExceptionServices;
using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Marker interface for requests that should execute within a transaction.
/// </summary>
public interface ITransactionalRequest { }

/// <summary>
/// Abstract transaction scope provider. Users implement this for their DB provider.
/// </summary>
public interface ITransactionScopeProvider
{
    /// <summary>
    /// Begins a new transaction scope.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A transaction scope that can be committed or rolled back.</returns>
    Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents a transaction scope that can be committed or rolled back.
/// </summary>
public interface ITransactionScope : IAsyncDisposable
{
    /// <summary>
    /// Commits the transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Pipeline behavior that wraps requests marked with <see cref="ITransactionalRequest"/> in a transaction.
/// If the handler succeeds, the transaction is committed. If an exception is thrown, the transaction is rolled back.
/// </summary>
/// <typeparam name="TRequest">Request type (must implement <see cref="ITransactionalRequest"/>).</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ITransactionalRequest
{
    private readonly ITransactionScopeProvider _transactionProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransactionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="transactionProvider">The transaction scope provider.</param>
    public TransactionBehavior(ITransactionScopeProvider transactionProvider)
    {
        _transactionProvider = transactionProvider ?? throw new ArgumentNullException(nameof(transactionProvider));
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_transactionProvider is ITransactionExecutionProvider executionProvider)
        {
            try
            {
                return await executionProvider.ExecuteAsync(token =>
                {
                    token.ThrowIfCancellationRequested();
                    return next();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                BehaviorFailure.SuppressRetry(failure);
                throw;
            }
        }

        var transaction = await _transactionProvider.BeginAsync(cancellationToken).ConfigureAwait(false);
        TResponse response = default!;
        List<Exception>? failures = null;
        var handlerSucceeded = false;
        try
        {
            response = await next().ConfigureAwait(false);
            handlerSucceeded = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            failures = [primary];
            if (handlerSucceeded) BehaviorFailure.SuppressRetry(primary);
            using var cleanup = BehaviorFailure.CreateCleanupBudget();
            try
            {
                await transaction.RollbackAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                failures.Add(rollbackFailure);
            }
        }

        // DisposeAsync has no cancellation contract. Await it to observe its failure;
        // providers must bound their own disposal rather than leaving orphaned work.
        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposalFailure)
        {
            failures ??= [];
            failures.Add(disposalFailure);
            BehaviorFailure.SuppressRetry(disposalFailure);
        }

        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
        {
            var failure = new AggregateException("Request and transaction cleanup failed.", failures);
            BehaviorFailure.SuppressRetry(failure);
            throw failure;
        }
        return response;
    }
}
