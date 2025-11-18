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
        await using var transaction = await _transactionProvider.BeginAsync(cancellationToken);
        try
        {
            var response = await next();
            await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
