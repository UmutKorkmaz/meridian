using System.Diagnostics;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.Logging;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// One audit record produced by <see cref="AuditBehavior{TRequest,TResponse}"/>
/// for every dispatched request. Implementations of <see cref="IAuditSink"/>
/// receive these records and persist them to wherever the application
/// needs them (database, log aggregator, SIEM).
/// </summary>
/// <remarks>
/// Deliberately a record so adopters can pattern-match on it in their
/// sinks. Field types are deliberately conservative — strings and ints —
/// so the type can be serialised straight to JSON or a relational column
/// without an extra mapping step.
/// </remarks>
public sealed record AuditEntry(
    DateTimeOffset TimestampUtc,
    string CorrelationId,
    string RequestTypeName,
    long DurationMs,
    bool Success,
    string? FailureMessage,
    string? FailureType);

/// <summary>
/// Receives audit records produced by <see cref="AuditBehavior{TRequest,TResponse}"/>.
/// Register one implementation in DI; the behaviour will dispatch to it
/// for every request that flows through the mediator pipeline.
/// </summary>
/// <remarks>
/// Implementations MUST be safe to call concurrently. The behaviour
/// awaits the sink call so a slow sink will slow down requests — keep
/// implementations non-blocking and ideally fire-and-forget against an
/// in-process channel.
/// </remarks>
public interface IAuditSink
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IAuditSink"/> that emits each record as a structured
/// <see cref="ILogger"/> message. Suitable for development and for
/// production environments that already aggregate logs into Elastic /
/// Splunk / equivalent. Adopters who need a database or SIEM destination
/// register their own sink instead.
/// </summary>
public sealed class LoggerAuditSink : IAuditSink
{
    private readonly ILogger<LoggerAuditSink> _logger;

    public LoggerAuditSink(ILogger<LoggerAuditSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Success)
        {
            _logger.LogInformation(
                "MeridianAudit success {RequestType} corr={CorrelationId} duration={DurationMs}ms",
                entry.RequestTypeName, entry.CorrelationId, entry.DurationMs);
        }
        else
        {
            _logger.LogWarning(
                "MeridianAudit failure {RequestType} corr={CorrelationId} duration={DurationMs}ms type={FailureType} msg={FailureMessage}",
                entry.RequestTypeName, entry.CorrelationId, entry.DurationMs,
                entry.FailureType, entry.FailureMessage);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Pipeline behaviour that records an <see cref="AuditEntry"/> for every
/// request dispatched through the mediator. Captures correlation ID
/// (auto-creates one via <see cref="CorrelationContext"/> if absent),
/// request type name, wall-clock duration, and success / failure with
/// the exception's type + message preserved for triage.
/// </summary>
/// <remarks>
/// <para>
/// Order matters in the pipeline: register <see cref="AuditBehavior{TRequest,TResponse}"/>
/// AFTER <see cref="CorrelationIdBehavior{TRequest,TResponse}"/> so the
/// audit entry sees the established correlation ID, and BEFORE
/// <see cref="ValidationBehavior{TRequest,TResponse}"/> so failed
/// validations are themselves audited.
/// </para>
/// <para>
/// Exceptions are re-thrown unchanged after the audit record is written.
/// The behaviour deliberately does NOT swallow failures — the audit log
/// is supposed to be a record of what happened, not the place where bugs
/// go to die.
/// </para>
/// </remarks>
public sealed class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IAuditSink _sink;

    public AuditBehavior(IAuditSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.EnsureCorrelationId();
        var requestTypeName = typeof(TRequest).FullName ?? typeof(TRequest).Name;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await next().ConfigureAwait(false);
            sw.Stop();

            await _sink.RecordAsync(
                new AuditEntry(startedAt, correlationId, requestTypeName,
                    sw.ElapsedMilliseconds, Success: true,
                    FailureMessage: null, FailureType: null),
                cancellationToken).ConfigureAwait(false);

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();

            // Best-effort audit of the failure. We deliberately do not let a
            // sink failure mask the original exception — if the sink throws,
            // its exception is suppressed via AggregateException-style
            // bookkeeping below.
            try
            {
                await _sink.RecordAsync(
                    new AuditEntry(startedAt, correlationId, requestTypeName,
                        sw.ElapsedMilliseconds, Success: false,
                        FailureMessage: ex.Message, FailureType: ex.GetType().FullName),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception sinkEx)
            {
                throw new AggregateException(
                    "Request handler failed AND audit sink failed while recording the failure. " +
                    "The original handler exception is the inner exception of this AggregateException; " +
                    "the sink failure is the second.",
                    ex, sinkEx);
            }

            throw;
        }
    }
}
