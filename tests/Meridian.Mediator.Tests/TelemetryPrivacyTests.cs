using System.Diagnostics;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public class TelemetryPrivacyTests
{
    [Fact]
    public async Task Exception_Stacktrace_Is_Not_Recorded_By_Default()
    {
        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IRequestHandler<FailingActivityProbeRequest, int>, FailingActivityProbeRequestHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FailingActivityProbeRequest()));

        var stopped = collector.SingleStopped("Mediator.Send FailingActivityProbeRequest");
        Assert.Equal("request boom", stopped.GetTagItem("exception.message"));
        Assert.Null(stopped.GetTagItem("exception.stacktrace"));
    }

    [Fact]
    public async Task Telemetry_Options_Can_Opt_In_Stacktrace_And_Opt_Out_Message()
    {
        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddSingleton(new MediatorTelemetryOptions
        {
            RecordExceptionMessage = false,
            RecordExceptionStackTrace = true
        });
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IRequestHandler<FailingActivityProbeRequest, int>, FailingActivityProbeRequestHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FailingActivityProbeRequest()));

        var stopped = collector.SingleStopped("Mediator.Send FailingActivityProbeRequest");
        Assert.Equal(ActivityStatusCode.Error, stopped.Status);
        Assert.Null(stopped.StatusDescription);
        Assert.Null(stopped.GetTagItem("exception.message"));
        Assert.Contains("request boom", stopped.GetTagItem("exception.stacktrace") as string);
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly object _sync = new();

        public List<Activity> Stopped { get; } = [];

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "Meridian.Mediator",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_sync)
                    {
                        Stopped.Add(activity);
                    }
                }
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
        }

        public Activity SingleStopped(string operationName)
        {
            lock (_sync)
            {
                return Assert.Single(Stopped.Where(activity => activity.OperationName == operationName));
            }
        }
    }
}
