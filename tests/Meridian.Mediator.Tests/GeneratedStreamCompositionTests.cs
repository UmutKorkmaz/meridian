using System.Runtime.CompilerServices;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Streaming;
using Meridian.Mediator.Wrappers;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

/// <summary>Strict cross-registry open stream behavior validation (D3 fix round 1).</summary>
[Collection("Pipeline")]
public class GeneratedStreamCompositionTests
{
    private static async Task<List<string>> RunAsync(IServiceProvider root, object request)
    {
        using var scope = root.CreateScope();
        await foreach (var _ in scope.ServiceProvider.GetRequiredService<IMediator>().CreateStream(request)) { }
        return scope.ServiceProvider.GetRequiredService<GeneratedStreamLog>().Events;
    }

    [Fact]
    public void Strict_Manual_Registry_Stream_Slot_Is_Not_Silently_Omitted_From_Open_Behavior_Validation()
    {
        var services = new ServiceCollection().AddScoped<GeneratedStreamLog>();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddMeridianMediator(c =>
        {
            c.RequireGeneratedDispatch = true;
            c.AddGeneratedRegistry<OpenRootRegistry>();
            c.AddGeneratedRegistry<ManualOtherStreamRegistry>();
            c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>));
        }));
        Assert.Contains(nameof(ManualOtherStreamRequest), error.Message);
        Assert.Contains(nameof(GeneratedOpenStreamBehavior<,>), error.Message);
    }

    [Fact]
    public void Strict_Manual_Only_Registry_With_Configured_Open_Behavior_Is_Rejected_Actionably()
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddMeridianMediator(c =>
        {
            c.RequireGeneratedDispatch = true;
            c.AddGeneratedRegistry<ManualOtherStreamRegistry>();
            c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>));
        }));
        // A manual-only registry has no generated finite pairs, so the older pairs guard rejects it before the per-slot
        // loop runs; the slot loop itself is covered by the generated-plus-manual test above.
        Assert.Contains("has no generated finite stream pairs", error.Message);
        Assert.Contains("declare", error.Message);
    }

    [Fact]
    public async Task Strict_Manual_Only_Registry_Without_Open_Behavior_Remains_Valid()
    {
        var services = new ServiceCollection().AddScoped<GeneratedStreamLog>();
        services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.AddGeneratedRegistry<ManualOtherStreamRegistry>(); });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(new[] { "manual-handler" }, await RunAsync(provider, new ManualOtherStreamRequest()));
    }

    [Fact]
    public async Task Strict_Generated_Plus_Manual_Registry_Without_Open_Behavior_Remains_Valid()
    {
        var services = new ServiceCollection().AddScoped<GeneratedStreamLog>();
        services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.AddGeneratedRegistry<OpenRootRegistry>(); c.AddGeneratedRegistry<ManualOtherStreamRegistry>(); });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(new[] { "manual-handler" }, await RunAsync(provider, new ManualOtherStreamRequest()));
        Assert.Equal(new[] { "open:enter", "second:enter", "enter", "finally", "second:finally", "open:finally" }, await RunAsync(provider, new GeneratedStreamRequest()));
    }

    [Fact]
    public async Task Strict_Manual_Registry_With_Finite_Inapplicable_Evidence_Is_Accepted()
    {
        var services = new ServiceCollection().AddScoped<GeneratedStreamLog>();
        services.AddMeridianMediator(c =>
        {
            c.RequireGeneratedDispatch = true;
            c.AddGeneratedRegistry<OpenRootRegistry>();
            c.AddGeneratedRegistry<ManualEvidenceStreamRegistry>();
            c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>));
        });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(new[] { "manual-handler" }, await RunAsync(provider, new ManualOtherStreamRequest()));
        Assert.Equal(new[] { "open:enter", "second:enter", "enter", "finally", "second:finally", "open:finally" }, await RunAsync(provider, new GeneratedStreamRequest()));
    }

    [Fact]
    public async Task Strict_Explicit_Closed_Factory_Roots_Configured_Open_Behavior_Once_Closed_Before_Open()
    {
        var services = new ServiceCollection().AddScoped<GeneratedStreamLog>();
        services.AddMeridianMediator(c =>
        {
            c.RequireGeneratedDispatch = true;
            c.AddGeneratedRegistry<OpenRootRegistry>();
            c.AddGeneratedRegistry<ClosedRootRegistry>();
            // Second definition is configured first; the explicit closed B root must still run in the closed phase.
            c.AddOpenStreamBehavior(typeof(GeneratedSecondOpenStreamBehavior<,>));
            c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>));
        });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(new[] { "open:enter", "second:enter", "closed-root", "second:finally", "open:finally" }, await RunAsync(provider, new ClosedRootStreamRequest()));
        Assert.Equal(new[] { "second:enter", "open:enter", "enter", "finally", "open:finally", "second:finally" }, await RunAsync(provider, new GeneratedStreamRequest()));
    }

    [Fact]
    public void Strict_Unrooted_Stream_Next_To_Explicit_Closed_Root_Is_Still_Rejected()
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddMeridianMediator(c =>
        {
            c.RequireGeneratedDispatch = true;
            c.AddGeneratedRegistry<OpenRootRegistry>();
            c.AddGeneratedRegistry<ClosedRootRegistry>();
            c.AddGeneratedRegistry<UnrootedStreamRegistry>();
            c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>));
        }));
        Assert.Contains(nameof(UnrootedStreamRequest), error.Message);
    }
}

public sealed class ManualOtherStreamRequest : IStreamRequest<int> { }
public sealed class ManualOtherStreamHandler(GeneratedStreamLog log) : IStreamRequestHandler<ManualOtherStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(ManualOtherStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    { log.Events.Add("manual-handler"); await Task.Yield(); yield return 1; }
}
public class ManualOtherStreamRegistry : IGeneratedMediatorRegistry
{
    public string RegistryId => GetType().Name;
    public IReadOnlyCollection<Type> RequestTypes => Array.Empty<Type>();
    public IReadOnlyCollection<Type> NotificationTypes => Array.Empty<Type>();
    public IReadOnlyCollection<Type> StreamTypes => new[] { typeof(ManualOtherStreamRequest) };
    public void RegisterServices(IServiceCollection services, ServiceLifetime handlerLifetime) =>
        services.Add(new ServiceDescriptor(typeof(IStreamRequestHandler<ManualOtherStreamRequest, int>), sp => new ManualOtherStreamHandler(sp.GetRequiredService<GeneratedStreamLog>()), handlerLifetime));
    public RequestHandlerBase? FindRequestHandler(Type requestType) => null;
    public NotificationHandlerWrapperBase? FindNotificationHandler(Type notificationType) => null;
    public StreamRequestHandlerBase? FindStreamHandler(Type requestType) =>
        requestType == typeof(ManualOtherStreamRequest) ? new StreamRequestHandlerWrapperImpl<ManualOtherStreamRequest, int>() : null;
}
/// <summary>Manual registry that also supplies finite evidence that the configured open behavior is inapplicable.</summary>
public sealed class ManualEvidenceStreamRegistry : ManualOtherStreamRegistry, IGeneratedMediatorBehaviorCapabilities
{
    public IReadOnlyList<GeneratedMediatorBehaviorCapability> BehaviorCapabilities { get; } = new[]
    {
        new GeneratedMediatorBehaviorCapability(typeof(ManualOtherStreamRequest), typeof(int), typeof(GeneratedOpenStreamBehavior<,>), GeneratedMediatorCategory.StreamBehavior, false)
    };
}

public sealed class ClosedRootStreamRequest : IStreamRequest<int> { }
public sealed class ClosedRootStreamHandler(GeneratedStreamLog log) : IStreamRequestHandler<ClosedRootStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(ClosedRootStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    { log.Events.Add("closed-root"); await Task.Yield(); yield return 1; }
}
public sealed class UnrootedStreamRequest : IStreamRequest<int> { }
public sealed class UnrootedStreamHandler : IStreamRequestHandler<UnrootedStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(UnrootedStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.Yield(); yield return 1; }
}
public sealed class GeneratedSecondOpenStreamBehavior<TRequest, TResponse>(GeneratedStreamLog log) : IStreamPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    { log.Events.Add("second:enter"); try { await foreach (var value in next().WithCancellation(cancellationToken)) yield return value; } finally { log.Events.Add("second:finally"); } }
}

[GenerateMediatorRegistry(typeof(GeneratedStreamHandler), StreamBehaviorTypes = new[] { typeof(GeneratedOpenStreamBehavior<,>), typeof(GeneratedSecondOpenStreamBehavior<,>) })]
public partial class OpenRootRegistry { }
[GenerateMediatorRegistry(typeof(ClosedRootStreamHandler), StreamBehaviorTypes = new[] { typeof(GeneratedOpenStreamBehavior<ClosedRootStreamRequest, int>), typeof(GeneratedSecondOpenStreamBehavior<,>) })]
public partial class ClosedRootRegistry { }
[GenerateMediatorRegistry(typeof(UnrootedStreamHandler))]
public partial class UnrootedStreamRegistry { }
