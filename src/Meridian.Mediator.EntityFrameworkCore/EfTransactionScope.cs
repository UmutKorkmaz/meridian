using Meridian.Mediator.Behaviors;
using Microsoft.EntityFrameworkCore.Storage;

namespace Meridian.Mediator.EntityFrameworkCore;

/// <summary>An owned transaction or one uniquely named savepoint in a caller-owned transaction.</summary>
public sealed class EfTransactionScope : ITransactionScope
{
    private readonly IDbContextTransaction _transaction;
    private readonly bool _ownsTransaction;
    private readonly string? _savepoint;
    private readonly Action<Exception> _poison;
    private bool _completed;
    private bool _disposed;

    internal EfTransactionScope(IDbContextTransaction transaction, bool ownsTransaction, string? savepoint, Action<Exception> poison)
    { _transaction = transaction; _ownsTransaction = ownsTransaction; _savepoint = savepoint; _poison = poison; }

    internal bool HasRollbackBoundary => _ownsTransaction || _savepoint is not null;

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Transaction scope is already completed.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_ownsTransaction) await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            else if (_savepoint is not null) await _transaction.ReleaseSavepointAsync(_savepoint, cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        catch (Exception error) { _poison(error); throw; }
    }

    /// <inheritdoc/>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) return;
        try
        {
            if (_ownsTransaction) await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            else if (_savepoint is not null)
            {
                await _transaction.RollbackToSavepointAsync(_savepoint, cancellationToken).ConfigureAwait(false);
                await _transaction.ReleaseSavepointAsync(_savepoint, cancellationToken).ConfigureAwait(false);
            }
            _completed = true;
        }
        catch (Exception error) { _poison(error); throw; }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_ownsTransaction) await _transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { _poison(error); throw; }
    }
}
