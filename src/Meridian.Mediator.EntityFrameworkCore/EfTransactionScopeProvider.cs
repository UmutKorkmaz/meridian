using System.Data;
using System.Runtime.ExceptionServices;
using Meridian.Mediator.Behaviors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Meridian.Mediator.EntityFrameworkCore;

/// <summary>Scoped, sequential EF transaction execution with explicit ownership and handler-once retry policy.</summary>
/// <typeparam name="TContext">The scoped context used by the handler.</typeparam>
public sealed class EfTransactionScopeProvider<TContext> : ITransactionScopeProvider, ITransactionExecutionProvider where TContext : DbContext
{
    private readonly TContext _context;
    private readonly bool _useStrategy;
    private readonly bool _joinWithoutSavepoint;
    private Frame? _frame;
    private Exception? _poison;

    /// <summary>Creates a provider for a scoped context. Parallel use of that context is unsupported.</summary>
    /// <param name="context">The handler's scoped context.</param>
    /// <param name="options">Explicit transaction opt-ins.</param>
    public EfTransactionScopeProvider(TContext context, EfTransactionOptions? options = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _useStrategy = options?.UseExecutionStrategy ?? false;
        _joinWithoutSavepoint = options?.JoinWithoutSavepoint ?? false;
    }

    private void CheckAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_poison is not null) throw new InvalidOperationException("Discard this scoped context after an uncertain transaction outcome.", _poison);
        if (System.Transactions.Transaction.Current is not null || (_context.GetService<IDbContextTransactionManager>() as ITransactionEnlistmentManager)?.EnlistedTransaction is not null)
            throw new InvalidOperationException("System.Transactions ambient or enlisted transactions are unsupported; use Database.CurrentTransaction.");
        if (ExecutionStrategy.Current is not null && (_frame is null || !ReferenceEquals(ExecutionStrategy.Current, _frame.Strategy)))
            throw new InvalidOperationException("An external execution strategy cannot own Meridian transaction execution.");
    }

    /// <inheritdoc/>
    public async Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
    {
        CheckAvailable(cancellationToken);
        var transaction = _context.Database.CurrentTransaction;
        if (transaction is null)
            return new EfTransactionScope(await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), true, null, Poison);
        if (!transaction.SupportsSavepoints)
        {
            if (!_joinWithoutSavepoint) throw new InvalidOperationException("The current transaction does not support savepoints; explicit JoinWithoutSavepoint is required.");
            return new EfTransactionScope(transaction, false, null, Poison);
        }
        var name = "meridian_" + Guid.NewGuid().ToString("N");
        await transaction.CreateSavepointAsync(name, cancellationToken).ConfigureAwait(false);
        return new EfTransactionScope(transaction, false, name, Poison);
    }

    /// <inheritdoc/>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        CheckAvailable(ct);
        if (_frame is not null) return (await AttemptAsync(action, ct).ConfigureAwait(false)).GetResult();
        var strategy = _context.Database.CreateExecutionStrategy();
        if (!_useStrategy && strategy.RetriesOnFailure)
            throw new InvalidOperationException("The context has a retrying strategy; explicitly enable UseExecutionStrategy for handler-once transaction execution.");
        if (_context.Database.CurrentTransaction is not null && strategy.RetriesOnFailure)
            throw new InvalidOperationException("An existing caller transaction cannot enter a new retrying EF execution strategy.");
        var frame = new Frame();
        _frame = frame;
        try
        {
            if (!_useStrategy) return (await AttemptAsync(action, ct).ConfigureAwait(false)).GetResult();
            Outcome<T>? terminal = null;
            var outcome = await strategy.ExecuteAsync(async token =>
            {
                // A successful EF-level result is terminal even if it carries a transaction error.
                if (terminal is not null) return terminal;
                frame.Strategy = ExecutionStrategy.Current;
                var opened = false;
                if (_context.Database.CurrentTransaction is null && _context.Database.GetDbConnection().State == ConnectionState.Closed)
                {
                    try { await _context.Database.OpenConnectionAsync(token).ConfigureAwait(false); opened = true; }
                    catch (Exception error) when (error is not OperationCanceledException
                        && _context.Database.CurrentTransaction is null && _context.Database.GetDbConnection().State == ConnectionState.Closed)
                    { frame.AcquisitionFailure = error; throw; }
                    catch (Exception error) { _poison = error; return terminal = new(default!, [error]); }
                }
                terminal = await AttemptAsync(action, token).ConfigureAwait(false);
                if (opened)
                {
                    try { await _context.Database.CloseConnectionAsync().ConfigureAwait(false); }
                    catch (Exception error) { terminal.Failures.Add(error); Poison(error); }
                }
                return terminal;
            }, ct).ConfigureAwait(false);
            return outcome.GetResult();
        }
        catch (RetryLimitExceededException error) when (error.InnerException is not null && ReferenceEquals(error.InnerException, frame.AcquisitionFailure))
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        finally { _frame = null; }
    }

    private async Task<Outcome<T>> AttemptAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        EfTransactionScope? scope = null;
        var failures = new List<Exception>();
        T value = default!;
        var commitStarted = false;
        try
        {
            scope = (EfTransactionScope)await BeginAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            value = await action(ct).ConfigureAwait(false);
            if (_frame?.Failure is not null) ExceptionDispatchInfo.Capture(_frame.Failure).Throw();
            commitStarted = true;
            await scope.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (_frame?.Failure is { } childFailure && !ReferenceEquals(childFailure, error)) failures.Add(childFailure);
            failures.Add(error);
            if (commitStarted || scope is null || !scope.HasRollbackBoundary) Poison(error);
            if (scope is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await scope.RollbackAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception rollback) { failures.Add(rollback); Poison(rollback); }
            }
        }
        if (scope is not null)
        {
            // No cancellation parameter exists for disposal: await and observe actual completion.
            try { await scope.DisposeAsync().ConfigureAwait(false); }
            catch (Exception disposal) { failures.Add(disposal); Poison(disposal); }
        }
        var result = new Outcome<T>(value, failures);
        if (_poison is not null && failures.Count > 0) _frame!.Failure = result.Error;
        return result;
    }

    private void Poison(Exception error)
    {
        _poison ??= error;
        if (_frame is not null) _frame.Failure ??= error;
    }

    private sealed class Frame
    {
        internal IExecutionStrategy? Strategy;
        internal Exception? Failure;
        internal Exception? AcquisitionFailure;
    }

    private sealed class Outcome<T>(T value, List<Exception> failures)
    {
        internal List<Exception> Failures { get; } = failures;
        private Exception? _error;
        private int _errorCount = -1;
        internal Exception? Error
        {
            get
            {
                if (_errorCount != Failures.Count)
                {
                    _error = Failures.Count switch { 0 => null, 1 => Failures[0], _ => new AggregateException("Transaction execution and cleanup failed.", Failures) };
                    _errorCount = Failures.Count;
                }
                return _error;
            }
        }
        internal T GetResult() { if (Error is { } error) ExceptionDispatchInfo.Capture(error).Throw(); return value; }
    }
}
