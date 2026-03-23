using System.Collections;
using System.Reflection;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Publishing;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace Meridian.Mediator.Tests;

#region Fixtures

public record CoverageNotification(string Value) : INotification;

public sealed class CoveragePublisherDependency
{
    public string Name => "type-based-publisher";
}

public sealed class TypeConfiguredPublisher : INotificationPublisher
{
    public static int CallCount;
    public static string? DependencyName;

    private readonly CoveragePublisherDependency _dependency;

    public TypeConfiguredPublisher(CoveragePublisherDependency dependency)
    {
        _dependency = dependency;
    }

    public Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        CallCount++;
        DependencyName = _dependency.Name;
        return Task.CompletedTask;
    }
}

public record ResilientCoverageNotification(string Value) : INotification;

public class ResilientSuccessHandler : INotificationHandler<ResilientCoverageNotification>
{
    public static bool WasExecuted { get; set; }

    public async Task Handle(ResilientCoverageNotification notification, CancellationToken cancellationToken)
    {
        WasExecuted = true;
        await Task.Delay(10, cancellationToken);
    }
}

public class ResilientFailureHandler : INotificationHandler<ResilientCoverageNotification>
{
    public static bool WasExecuted { get; set; }

    public async Task Handle(ResilientCoverageNotification notification, CancellationToken cancellationToken)
    {
        WasExecuted = true;
        await Task.Yield();
        throw new InvalidOperationException("Resilient handler failed");
    }
}

public record ScannedCoverageNotification(string Value) : INotification;

public class ScannedCoverageHandlerOne : INotificationHandler<ScannedCoverageNotification>
{
    public static bool WasExecuted { get; set; }

    public Task Handle(ScannedCoverageNotification notification, CancellationToken cancellationToken)
    {
        WasExecuted = true;
        return Task.CompletedTask;
    }
}

public class ScannedCoverageHandlerTwo : INotificationHandler<ScannedCoverageNotification>
{
    public static bool WasExecuted { get; set; }

    public Task Handle(ScannedCoverageNotification notification, CancellationToken cancellationToken)
    {
        WasExecuted = true;
        return Task.CompletedTask;
    }
}

public record OpenStreamCoverageRequest(int Count) : IStreamRequest<int>;

public class OpenStreamCoverageHandler : IStreamRequestHandler<OpenStreamCoverageRequest, int>
{
    public async IAsyncEnumerable<int> Handle(OpenStreamCoverageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }
}

public class TrackingOpenStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public static int BeforeCount { get; set; }
    public static int AfterCount { get; set; }

    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BeforeCount++;

        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            yield return item;
        }

        AfterCount++;
    }
}

#endregion

public class AdditionalCoverageTests
{
    [Fact]
    public void BuiltInRegistrationHelpers_AddExpectedOpenBehaviors()
    {
        var config = new MeridianMediatorConfiguration();

        config.AddValidationBehavior(10)
            .AddLoggingBehavior(11)
            .AddTransactionBehavior(12)
            .AddCachingBehavior(13)
            .AddRetryBehavior(14)
            .AddAuthorizationBehavior(15)
            .AddCorrelationIdBehavior(16)
            .AddIdempotencyBehavior(17);

        var openBehaviors = GetOpenBehaviors(config);

        Assert.Equal(9, openBehaviors.Count);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(ValidationBehavior<,>) && b.Order == 10);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(LoggingBehavior<,>) && b.Order == 11);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(TransactionBehavior<,>) && b.Order == 12);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(CachingBehavior<,>) && b.Order == 13);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(CacheInvalidationBehavior<,>) && b.Order == 13);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(RetryBehavior<,>) && b.Order == 14);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(AuthorizationBehavior<,>) && b.Order == 15);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(CorrelationIdBehavior<,>) && b.Order == 16);
        Assert.Contains(openBehaviors, b => b.BehaviorType == typeof(IdempotencyBehavior<,>) && b.Order == 17);
    }

    [Fact]
    public async Task NotificationPublisherType_ResolvesConfiguredPublisherTypeThroughDi()
    {
        TypeConfiguredPublisher.CallCount = 0;
        TypeConfiguredPublisher.DependencyName = null;

        var services = new ServiceCollection();
        services.AddSingleton(new CoveragePublisherDependency());
        services.AddMeridianMediator(cfg =>
        {
            cfg.NotificationPublisherType = typeof(TypeConfiguredPublisher);
        });

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Publish(new CoverageNotification("publisher-type"));

        Assert.Equal(1, TypeConfiguredPublisher.CallCount);
        Assert.Equal("type-based-publisher", TypeConfiguredPublisher.DependencyName);
    }

    [Fact]
    public async Task ResilientTaskWhenAllPublisher_AllowsSuccessfulHandlersToComplete()
    {
        ResilientSuccessHandler.WasExecuted = false;
        ResilientFailureHandler.WasExecuted = false;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.NotificationPublisher = new ResilientTaskWhenAllPublisher();
        });
        services.AddTransient<INotificationHandler<ResilientCoverageNotification>, ResilientSuccessHandler>();
        services.AddTransient<INotificationHandler<ResilientCoverageNotification>, ResilientFailureHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => mediator.Publish(new ResilientCoverageNotification("resilient")));

        Assert.True(ResilientSuccessHandler.WasExecuted);
        Assert.True(ResilientFailureHandler.WasExecuted);
        Assert.Single(ex.InnerExceptions);
        Assert.IsType<InvalidOperationException>(ex.InnerExceptions[0]);
    }

    [Fact]
    public async Task AssemblyScanning_Registers_All_NotificationHandlers_For_A_Notification()
    {
        ScannedCoverageHandlerOne.WasExecuted = false;
        ScannedCoverageHandlerTwo.WasExecuted = false;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<AdditionalCoverageTests>();
        });

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Publish(new ScannedCoverageNotification("scan-all"));

        Assert.True(ScannedCoverageHandlerOne.WasExecuted);
        Assert.True(ScannedCoverageHandlerTwo.WasExecuted);
    }

    [Fact]
    public async Task AddOpenStreamBehavior_ExecutesAroundStreamHandler()
    {
        TrackingOpenStreamBehavior<OpenStreamCoverageRequest, int>.BeforeCount = 0;
        TrackingOpenStreamBehavior<OpenStreamCoverageRequest, int>.AfterCount = 0;

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddOpenStreamBehavior(typeof(TrackingOpenStreamBehavior<,>));
        });
        services.AddTransient<IStreamRequestHandler<OpenStreamCoverageRequest, int>, OpenStreamCoverageHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var items = new List<int>();
        await foreach (var item in mediator.CreateStream(new OpenStreamCoverageRequest(4)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2, 3, 4 }, items);
        Assert.Equal(1, TrackingOpenStreamBehavior<OpenStreamCoverageRequest, int>.BeforeCount);
        Assert.Equal(1, TrackingOpenStreamBehavior<OpenStreamCoverageRequest, int>.AfterCount);
    }

    private static List<(Type BehaviorType, int Order)> GetOpenBehaviors(MeridianMediatorConfiguration config)
    {
        var property = typeof(MeridianMediatorConfiguration).GetProperty(
            "OpenBehaviors",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(property);

        var value = property!.GetValue(config);
        Assert.NotNull(value);

        return ((IEnumerable)value!)
            .Cast<object>()
            .Select(item =>
            {
                var tuple = ((Type, int))item;
                return (BehaviorType: tuple.Item1, Order: tuple.Item2);
            })
            .ToList();
    }
}
