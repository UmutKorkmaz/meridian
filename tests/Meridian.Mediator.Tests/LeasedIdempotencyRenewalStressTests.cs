#pragma warning disable CA1707 // Contract test names are deliberately descriptive.
namespace Meridian.Mediator.Tests;

/// <summary>
/// Concurrent stress for the leased-idempotency cleanup-deadline scenarios. Every iteration owns its own virtual clock,
/// store and gates, so many scenarios compete for the thread pool at once. That contention is what exposed the original
/// tie between an already-completed renewal and an expired cleanup budget: the pre-fix test fails here within the first
/// round on every target framework. The scenarios must now pass every iteration with their exact outcomes.
/// </summary>
public class LeasedIdempotencyRenewalStressTests
{
    private const int Rounds = 20;

    [Fact]
    public async Task Noncooperative_Renewal_Cleanup_Deadline_Scenarios_Are_Stable_Under_Thread_Pool_Contention()
    {
        var width = Math.Max(32, Environment.ProcessorCount * 8);
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, width * 2), io);
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var scenarios = Enumerable.Range(0, width).SelectMany(_ => new[]
                {
                    Task.Run(() => new LeasedIdempotencyBehaviorTests().Noncooperative_Renewal_Is_Joined_Even_After_Cleanup_Deadline()),
                    Task.Run(() => new LeasedIdempotencyBehaviorTests().Noncooperative_Healthy_Renewal_After_Cleanup_Budget_Faults_With_Only_The_Budget_Timeout()),
                    Task.Run(() => new LeasedIdempotencyBehaviorTests().Noncooperative_Renewal_With_A_Pending_Budget_Wait_Faults_With_The_Renewal_Deadline_And_Optionally_The_Budget()),
                    Task.Run(() => new LeasedIdempotencyBehaviorTests().Noncooperative_Provider_Keeps_Request_Alive_Until_It_Settles()),
                }).ToArray();
                await Task.WhenAll(scenarios);
            }
        }
        finally { ThreadPool.SetMinThreads(workers, io); }
    }
}
