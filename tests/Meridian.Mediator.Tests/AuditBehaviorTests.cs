using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Mediator.Tests;

public class AuditBehaviorTests
{
    public sealed record AuditPing(int Value) : IRequest<AuditPong>;
    public sealed record AuditPong(int Value);

    public sealed class AuditPingHandler : IRequestHandler<AuditPing, AuditPong>
    {
        public Task<AuditPong> Handle(AuditPing r, CancellationToken ct) =>
            Task.FromResult(new AuditPong(r.Value + 1));
    }

    public sealed record FailingPing : IRequest<Unit>;

    public sealed class FailingPingHandler : IRequestHandler<FailingPing, Unit>
    {
        public Task<Unit> Handle(FailingPing r, CancellationToken ct) =>
            throw new InvalidOperationException("intentional");
    }

    private sealed class CapturingSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = new();
        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static IMediator BuildMediator(IAuditSink sink)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sink);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddMeridianMediator(c =>
        {
            c.RegisterServicesFromAssembly(typeof(AuditBehaviorTests).Assembly);
            c.AddAuditBehavior();
        });
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Successful_Request_Produces_One_Success_Audit_Entry()
    {
        var sink = new CapturingSink();
        var mediator = BuildMediator(sink);

        var response = await mediator.Send(new AuditPing(41));

        Assert.Equal(42, response.Value);
        Assert.Single(sink.Entries);
        Assert.True(sink.Entries[0].Success);
        Assert.Null(sink.Entries[0].FailureMessage);
        Assert.Contains(nameof(AuditPing), sink.Entries[0].RequestTypeName);
        Assert.False(string.IsNullOrEmpty(sink.Entries[0].CorrelationId));
        Assert.True(sink.Entries[0].DurationMs >= 0);
    }

    [Fact]
    public async Task Failing_Request_Produces_Failure_Audit_Entry_And_Rethrows()
    {
        var sink = new CapturingSink();
        var mediator = BuildMediator(sink);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Send(new FailingPing()));

        Assert.Equal("intentional", ex.Message);
        Assert.Single(sink.Entries);
        Assert.False(sink.Entries[0].Success);
        Assert.Equal("intentional", sink.Entries[0].FailureMessage);
        Assert.Contains("InvalidOperationException", sink.Entries[0].FailureType);
    }

    [Fact]
    public async Task Caller_Established_Correlation_Id_Flows_Through_Both_Requests()
    {
        var sink = new CapturingSink();
        var mediator = BuildMediator(sink);

        // AsyncLocal modifications made INSIDE an awaited Task do not
        // propagate back to the caller — that's by design. So the audit
        // behaviour will mint a fresh ID for each top-level Send unless
        // the caller establishes one first. This is the recommended
        // pattern for HTTP middleware: read the X-Correlation-Id header
        // at the start of the request, set CorrelationContext.CorrelationId,
        // and every nested Send shares it.
        var expected = "shared-corr-id-from-caller";
        CorrelationContext.CorrelationId = expected;
        try
        {
            await mediator.Send(new AuditPing(1));
            await mediator.Send(new AuditPing(2));
        }
        finally
        {
            CorrelationContext.CorrelationId = null;
        }

        Assert.Equal(2, sink.Entries.Count);
        Assert.Equal(expected, sink.Entries[0].CorrelationId);
        Assert.Equal(expected, sink.Entries[1].CorrelationId);
    }

    [Fact]
    public async Task Two_Top_Level_Requests_Without_A_Caller_Id_Each_Get_A_Fresh_Id()
    {
        var sink = new CapturingSink();
        var mediator = BuildMediator(sink);

        CorrelationContext.CorrelationId = null;  // ensure clean slate

        await mediator.Send(new AuditPing(1));
        await mediator.Send(new AuditPing(2));

        Assert.Equal(2, sink.Entries.Count);
        Assert.NotEqual(sink.Entries[0].CorrelationId, sink.Entries[1].CorrelationId);
    }

    [Fact]
    public async Task LoggerAuditSink_Does_Not_Throw_On_Success_Or_Failure()
    {
        // Use the default sink directly to verify it does not blow up
        // — a no-op contract is critical because we wrap user handlers.
        var sink = new LoggerAuditSink(NullLogger<LoggerAuditSink>.Instance);

        await sink.RecordAsync(
            new AuditEntry(DateTimeOffset.UtcNow, "corr", "Type", 1, true, null, null),
            CancellationToken.None);

        await sink.RecordAsync(
            new AuditEntry(DateTimeOffset.UtcNow, "corr", "Type", 1, false, "boom", "BoomException"),
            CancellationToken.None);
    }
}
