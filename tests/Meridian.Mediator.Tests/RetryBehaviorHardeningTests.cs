using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Tests;

[CollectionDefinition("RetryPolicy", DisableParallelization = true)]
public sealed class RetryPolicyCollectionDefinition;

[Collection("RetryPolicy")]
public class RetryBehaviorHardeningTests
{
    [Fact]
    public async Task MaxRetries_Are_Clamped_To_Global_Cap()
    {
        var originalCap = RetryPolicy.MaxRetriesCap;
        var originalBackoff = RetryPolicy.MaxBackoff;
        var originalJitter = RetryPolicy.JitterProvider;

        try
        {
            RetryPolicy.MaxRetriesCap = 10;
            RetryPolicy.MaxBackoff = TimeSpan.Zero;
            RetryPolicy.JitterProvider = static () => 0;

            var behavior = new RetryBehavior<AlwaysRetryRequest, string>();
            var request = new AlwaysRetryRequest { MaxRetries = int.MaxValue, RetryDelay = TimeSpan.Zero };
            var attempts = 0;

            await Assert.ThrowsAsync<TransientException>(() => behavior.Handle(
                request,
                () =>
                {
                    attempts++;
                    throw new TransientException($"failure #{attempts}");
                },
                CancellationToken.None));

            Assert.Equal(11, attempts);
        }
        finally
        {
            RetryPolicy.MaxRetriesCap = originalCap;
            RetryPolicy.MaxBackoff = originalBackoff;
            RetryPolicy.JitterProvider = originalJitter;
        }
    }

    [Fact]
    public async Task OperationCanceledException_Is_Not_Retried()
    {
        var behavior = new RetryBehavior<AlwaysRetryRequest, string>();
        var request = new AlwaysRetryRequest { MaxRetries = 5, RetryDelay = TimeSpan.Zero };
        var attempts = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => behavior.Handle(
            request,
            () =>
            {
                attempts++;
                throw new OperationCanceledException();
            },
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ValidationException_Is_Not_Retried()
    {
        var behavior = new RetryBehavior<AlwaysRetryRequest, string>();
        var request = new AlwaysRetryRequest { MaxRetries = 5, RetryDelay = TimeSpan.Zero };
        var attempts = 0;

        await Assert.ThrowsAsync<ValidationException>(() => behavior.Handle(
            request,
            () =>
            {
                attempts++;
                throw new ValidationException([new ValidationError("Name", "required")]);
            },
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Delay_Saturates_Without_Overflow()
    {
        var originalCap = RetryPolicy.MaxRetriesCap;
        var originalBackoff = RetryPolicy.MaxBackoff;
        var originalJitter = RetryPolicy.JitterProvider;

        try
        {
            RetryPolicy.MaxRetriesCap = 2;
            RetryPolicy.MaxBackoff = TimeSpan.FromMilliseconds(1);
            RetryPolicy.JitterProvider = static () => 0;

            var behavior = new RetryBehavior<AlwaysRetryRequest, string>();
            var request = new AlwaysRetryRequest
            {
                MaxRetries = 2,
                RetryDelay = TimeSpan.MaxValue
            };
            var attempts = 0;

            await Assert.ThrowsAsync<TransientException>(() => behavior.Handle(
                request,
                () =>
                {
                    attempts++;
                    throw new TransientException("still transient");
                },
                CancellationToken.None));

            Assert.Equal(3, attempts);
        }
        finally
        {
            RetryPolicy.MaxRetriesCap = originalCap;
            RetryPolicy.MaxBackoff = originalBackoff;
            RetryPolicy.JitterProvider = originalJitter;
        }
    }

    private sealed record AlwaysRetryRequest : IRequest<string>, IRetryableRequest
    {
        public int MaxRetries { get; init; }
        public TimeSpan RetryDelay { get; init; }
        public bool ShouldRetry(Exception exception) => true;
    }
}
