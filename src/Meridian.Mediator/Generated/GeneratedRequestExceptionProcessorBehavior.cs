using System.Reflection;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Generated;

/// <summary>A finite exception contract adapter; instances hold metadata only.</summary>
public abstract class GeneratedExceptionHandlerInvoker<TRequest, TResponse> where TRequest : notnull
{
    /// <summary>Declared exception inheritance depth.</summary>
    public abstract int Depth { get; }
    /// <summary>Tests the declared exception contract.</summary>
    public abstract bool Matches(Exception exception);
    /// <summary>Resolves and invokes the declared contract from the current provider.</summary>
    public abstract Task<(bool Handled, TResponse? Response)> Invoke(IServiceProvider provider, TRequest request, Exception exception, CancellationToken token);
}

/// <summary>A closed typed handler adapter; never uses reflection invocation.</summary>
public sealed class GeneratedExceptionHandlerInvoker<TRequest, TResponse, TException>(int depth) : GeneratedExceptionHandlerInvoker<TRequest, TResponse>
    where TRequest : notnull where TException : Exception
{
    /// <inheritdoc/>
    public override int Depth => depth;
    /// <inheritdoc/>
    public override bool Matches(Exception exception) => exception is TException;
    /// <inheritdoc/>
    public override async Task<(bool Handled, TResponse? Response)> Invoke(IServiceProvider provider, TRequest request, Exception exception, CancellationToken token)
    {
        foreach (var handler in provider.GetServices<IRequestExceptionHandler<TRequest, TResponse, TException>>())
        {
            if (handler == null) continue;
            var state = new RequestExceptionHandlerState<TResponse>();
            Task task;
            // Legacy MethodInfo.Invoke wraps only the synchronous call, not the awaited task.
            try { task = handler.Handle(request, (TException)exception, state, token); }
            catch (Exception error) { throw new TargetInvocationException(error); }
            await task.ConfigureAwait(false);
            if (state.Handled) return (true, state.Response);
        }
        return (false, default);
    }
}

/// <summary>A finite action contract adapter.</summary>
public abstract class GeneratedExceptionActionInvoker<TRequest> where TRequest : notnull
{
    /// <summary>Declared exception inheritance depth.</summary>
    public abstract int Depth { get; }
    /// <summary>Tests the declared exception contract.</summary>
    public abstract bool Matches(Exception exception);
    /// <summary>Resolves and invokes the declared action contract from the current provider.</summary>
    public abstract Task Invoke(IServiceProvider provider, TRequest request, Exception exception, CancellationToken token);
}

/// <summary>A closed typed action adapter preserving synchronous legacy invocation failures.</summary>
public sealed class GeneratedExceptionActionInvoker<TRequest, TException>(int depth) : GeneratedExceptionActionInvoker<TRequest>
    where TRequest : notnull where TException : Exception
{
    /// <inheritdoc/>
    public override int Depth => depth;
    /// <inheritdoc/>
    public override bool Matches(Exception exception) => exception is TException;
    /// <inheritdoc/>
    public override async Task Invoke(IServiceProvider provider, TRequest request, Exception exception, CancellationToken token)
    {
        foreach (var action in provider.GetServices<IRequestExceptionAction<TRequest, TException>>())
        {
            if (action == null) continue;
            Task task;
            try { task = action.Execute(request, (TException)exception, token); }
            catch (Exception error) { throw new TargetInvocationException(error); }
            try { await task.ConfigureAwait(false); }
            catch { /* Legacy actions ignore only awaited failures. */ }
        }
    }
}

/// <summary>Runs only finite generated exception contracts, derived before base, against the current scope.</summary>
public sealed class GeneratedRequestExceptionProcessorBehavior<TRequest, TResponse>(IServiceProvider provider) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try { return await next().ConfigureAwait(false); }
        catch (Exception exception)
        {
            foreach (var invoker in provider.GetServices<GeneratedExceptionHandlerInvoker<TRequest, TResponse>>().OrderByDescending(i => i.Depth))
            {
                if (!invoker.Matches(exception)) continue;
                var result = await invoker.Invoke(provider, request, exception, cancellationToken).ConfigureAwait(false);
                if (result.Handled) return result.Response!;
            }
            foreach (var invoker in provider.GetServices<GeneratedExceptionActionInvoker<TRequest>>().OrderByDescending(i => i.Depth))
                if (invoker.Matches(exception)) await invoker.Invoke(provider, request, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
