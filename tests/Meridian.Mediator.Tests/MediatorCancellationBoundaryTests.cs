using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

[Collection("Metrics")]
public class MediatorCancellationBoundaryTests
{
    [Fact]
    public async Task Publish_Caller_Cancellation_Preserves_The_Fault_Without_Error_Telemetry()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Meridian.Mediator",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        using var caller = new CancellationTokenSource();
        using var provider = Mx.BuildStandard();
        var selected = new OperationCanceledException("caller cancelled notification", caller.Token);
        var calls = 0;
        CancellationToken received = default;
        Activity? handlerActivity = null;
        provider.GetRequiredService<MxScript>().Note = (_, token) =>
        {
            calls++;
            received = token;
            handlerActivity = Activity.Current;
            caller.Cancel();
            return Task.FromException(selected);
        };

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IMediator>().Publish(new MxNote(), caller.Token));

        Assert.Same(selected, error);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(caller.Token, received);
        Assert.Equal(1, calls);
        Assert.NotNull(handlerActivity);
        var activity = Assert.Single(stopped);
        Assert.Same(handlerActivity, activity);
        Assert.Equal("Mediator.Publish MxNote", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key.StartsWith("exception.", StringComparison.Ordinal));
        Assert.Empty(activity.Events);
    }
}
