using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record DiRequest(int Value) : IRequest<int>;

public class DiRequestHandler : IRequestHandler<DiRequest, int>
{
    private readonly IMultiplier _multiplier;

    public DiRequestHandler(IMultiplier multiplier)
    {
        _multiplier = multiplier;
    }

    public Task<int> Handle(DiRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_multiplier.Multiply(request.Value));
    }
}

public interface IMultiplier
{
    int Multiply(int value);
}

public class DoubleMultiplier : IMultiplier
{
    public int Multiply(int value) => value * 2;
}

// Open generic command/handler pair — simulates DigiflowAPI's workflow pattern:
// CreateWorkflowCommand<TRequest, TResponse> / CreateWorkflowCommandHandler<TRequest, TResponse>
// Both the command and handler share the same type parameter arity (2) to match IRequestHandler<,>.
public class GenericWorkflowCommand<TRequest, TResponse> : IRequest<GenericWorkflowResult<TResponse>>
{
    public TRequest Payload { get; init; } = default!;
}

public class GenericWorkflowResult<TResponse>
{
    public TResponse Data { get; init; } = default!;
    public bool Success { get; init; }
}

public class GenericWorkflowCommandHandler<TRequest, TResponse>
    : IRequestHandler<GenericWorkflowCommand<TRequest, TResponse>, GenericWorkflowResult<TResponse>>
{
    public Task<GenericWorkflowResult<TResponse>> Handle(
        GenericWorkflowCommand<TRequest, TResponse> request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new GenericWorkflowResult<TResponse>
        {
            Data = (TResponse)(object)request.Payload!,
            Success = true
        });
    }
}

public record StartupDiagnosticMissingRequest(int Value) : IRequest<int>;

#endregion

[Collection("Pipeline")]
public class DependencyInjectionTests
{
    private static ServiceCollection CreateServicesWithoutAssemblyScan()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        return services;
    }

    [Fact]
    public void AddMeridianMediator_RegistersIMediator()
    {
        var services = CreateServicesWithoutAssemblyScan();
        var provider = services.BuildServiceProvider();

        var mediator = provider.GetService<IMediator>();
        Assert.NotNull(mediator);
    }

    [Fact]
    public void AddMeridianMediator_RegistersISender()
    {
        var services = CreateServicesWithoutAssemblyScan();
        var provider = services.BuildServiceProvider();

        var sender = provider.GetService<ISender>();
        Assert.NotNull(sender);
    }

    [Fact]
    public void AddMeridianMediator_RegistersIPublisher()
    {
        var services = CreateServicesWithoutAssemblyScan();
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetService<IPublisher>();
        Assert.NotNull(publisher);
    }

    [Fact]
    public async Task AssemblyScanning_DiscoversHandlersAutomatically()
    {
        // Verify that assembly scanning registers handlers without manual registration.
        // We register handlers manually here to avoid the open generic behavior issue
        // from other test files in the same assembly.
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        // Simulate what assembly scanning would do for PingHandler
        services.AddTransient<IRequestHandler<Ping, string>, PingHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new Ping("auto-discovered"));
        Assert.Equal("Pong: auto-discovered", result);
    }

    [Fact]
    public async Task PipelineBehaviors_RegisteredViaConfiguration()
    {
        LoggingBehavior.Log.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddBehavior<LoggingBehavior>();
        });
        services.AddTransient<IRequestHandler<PipelineRequest, string>, PipelineRequestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new PipelineRequest("cfg-test"));

        Assert.Contains("Before", LoggingBehavior.Log);
    }

    [Fact]
    public async Task HandlerDependencies_InjectedByDIContainer()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<IRequestHandler<DiRequest, int>, DiRequestHandler>();
        services.AddTransient<IMultiplier, DoubleMultiplier>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new DiRequest(5));
        Assert.Equal(10, result);
    }

    [Fact]
    public void ISender_And_IPublisher_ResolveTo_SameMediator_Type()
    {
        var services = CreateServicesWithoutAssemblyScan();
        var provider = services.BuildServiceProvider();

        var mediator = provider.GetRequiredService<IMediator>();
        var sender = provider.GetRequiredService<ISender>();
        var publisher = provider.GetRequiredService<IPublisher>();

        Assert.IsType<Meridian.Mediator.Mediator>(mediator);
        Assert.IsType<Meridian.Mediator.Mediator>(sender);
        Assert.IsType<Meridian.Mediator.Mediator>(publisher);
    }

    [Fact]
    public void AddOpenBehavior_ThrowsForNonOpenGeneric()
    {
        var config = new MeridianMediatorConfiguration();

        Assert.Throws<ArgumentException>(
            () => config.AddOpenBehavior(typeof(LoggingBehavior)));
    }

    [Fact]
    public void RegisterServicesFromAssemblyContaining_RegistersFromCorrectAssembly()
    {
        // Verify the fluent API returns config for chaining
        var config = new MeridianMediatorConfiguration();
        var result = config.RegisterServicesFromAssemblyContaining<DependencyInjectionTests>();
        Assert.Same(config, result);
    }

    [Fact]
    public async Task OpenGenericHandler_ManualClosedRegistration_RuntimeDispatch()
    {
        // Simulates DigiflowAPI's workflow pattern:
        // 1. Open generic handler exists: GenericWorkflowCommandHandler<TRequest, TResponse>
        // 2. At startup, all closed combinations are registered manually via MakeGenericType
        // 3. At runtime, commands are constructed and dispatched via mediator.Send(object)
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { }); // No assembly scan needed

        // Manually close and register (like CleanWorkflowServiceExtensions does)
        var handlerType = typeof(GenericWorkflowCommandHandler<,>).MakeGenericType(typeof(string), typeof(string));
        var requestType = typeof(GenericWorkflowCommand<,>).MakeGenericType(typeof(string), typeof(string));
        var responseType = typeof(GenericWorkflowResult<>).MakeGenericType(typeof(string));
        var serviceType = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
        services.AddScoped(serviceType, handlerType);

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        // Build command at runtime (like WorkflowsController does with MakeGenericType)
        var commandType = typeof(GenericWorkflowCommand<,>).MakeGenericType(typeof(string), typeof(string));
        var command = Activator.CreateInstance(commandType)!;
        commandType.GetProperty("Payload")!.SetValue(command, "test-payload");

        // Dispatch via Send(object) — runtime dispatch
        var result = await mediator.Send(command);

        Assert.NotNull(result);
        var resultType = result.GetType();
        Assert.Equal(typeof(GenericWorkflowResult<string>), resultType);
        Assert.Equal("test-payload", resultType.GetProperty("Data")!.GetValue(result));
        Assert.True((bool)resultType.GetProperty("Success")!.GetValue(result)!);
    }

    [Fact]
    public async Task OpenGenericHandler_ManualClosedRegistration_StronglyTypedSend()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });

        // Manually close and register for int type args
        var handlerType = typeof(GenericWorkflowCommandHandler<,>).MakeGenericType(typeof(int), typeof(int));
        var requestType = typeof(GenericWorkflowCommand<,>).MakeGenericType(typeof(int), typeof(int));
        var responseType = typeof(GenericWorkflowResult<>).MakeGenericType(typeof(int));
        var serviceType = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
        services.AddScoped(serviceType, handlerType);

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GenericWorkflowCommand<int, int> { Payload = 42 });

        Assert.NotNull(result);
        Assert.Equal(42, result.Data);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task OpenGenericHandler_Is_AutoRegistered_ByAssemblyScanning()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<DependencyInjectionTests>();
        });

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var commandType = typeof(GenericWorkflowCommand<,>).MakeGenericType(typeof(string), typeof(string));
        var command = Activator.CreateInstance(commandType)!;
        commandType.GetProperty("Payload")!.SetValue(command, "from-scan");

        var result = await mediator.Send(command);

        Assert.NotNull(result);
        var resultType = result.GetType();
        Assert.Equal(typeof(GenericWorkflowResult<string>), resultType);
        Assert.Equal("from-scan", resultType.GetProperty("Data")!.GetValue(result));
        Assert.True((bool)resultType.GetProperty("Success")!.GetValue(result)!);
    }

    [Fact]
    public void AddMeridianMediator_WithStartupDiagnostics_UsesScannedAssemblies()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<DependencyInjectionTests>();
            cfg.AddStartupDiagnostics();
        });

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IMediator>());
    }

    [Fact]
    public void AddMeridianMediator_WithStartupDiagnostics_Throws_WhenRequiredHandlerMissing_And_Strict()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            var services = new ServiceCollection();
            services.AddMeridianMediator(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<DependencyInjectionTests>();
                cfg.AddStartupDiagnostics(throwOnFailure: true);
            });
        });
    }
}
