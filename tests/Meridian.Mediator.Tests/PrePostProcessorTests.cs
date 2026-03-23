using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record ProcessorRequest(string Value) : IRequest<string>;

public class ProcessorRequestHandler : IRequestHandler<ProcessorRequest, string>
{
    public static List<string> ExecutionLog { get; } = new();

    public Task<string> Handle(ProcessorRequest request, CancellationToken cancellationToken)
    {
        ExecutionLog.Add("Handler");
        return Task.FromResult($"Result:{request.Value}");
    }
}

public class TestPreProcessor : IRequestPreProcessor<ProcessorRequest>
{
    public static List<string> ExecutionLog => ProcessorRequestHandler.ExecutionLog;

    public Task Process(ProcessorRequest request, CancellationToken cancellationToken)
    {
        ExecutionLog.Add("PreProcessor");
        return Task.CompletedTask;
    }
}

public class TestPreProcessor2 : IRequestPreProcessor<ProcessorRequest>
{
    public static List<string> ExecutionLog => ProcessorRequestHandler.ExecutionLog;

    public Task Process(ProcessorRequest request, CancellationToken cancellationToken)
    {
        ExecutionLog.Add("PreProcessor2");
        return Task.CompletedTask;
    }
}

public class TestPostProcessor : IRequestPostProcessor<ProcessorRequest, string>
{
    public static List<string> ExecutionLog => ProcessorRequestHandler.ExecutionLog;

    public Task Process(ProcessorRequest request, string response, CancellationToken cancellationToken)
    {
        ExecutionLog.Add($"PostProcessor:{response}");
        return Task.CompletedTask;
    }
}

#endregion

public class PrePostProcessorTests
{
    [Fact]
    public async Task PreProcessor_ExecutesBeforeHandler()
    {
        ProcessorRequestHandler.ExecutionLog.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddRequestPreProcessor(typeof(TestPreProcessor));
        });
        services.AddTransient<IRequestHandler<ProcessorRequest, string>, ProcessorRequestHandler>();
        services.AddTransient<IRequestPreProcessor<ProcessorRequest>, TestPreProcessor>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new ProcessorRequest("X"));

        Assert.True(ProcessorRequestHandler.ExecutionLog.IndexOf("PreProcessor") <
                     ProcessorRequestHandler.ExecutionLog.IndexOf("Handler"));
    }

    [Fact]
    public async Task PostProcessor_ExecutesAfterHandler()
    {
        ProcessorRequestHandler.ExecutionLog.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddRequestPostProcessor(typeof(TestPostProcessor));
        });
        services.AddTransient<IRequestHandler<ProcessorRequest, string>, ProcessorRequestHandler>();
        services.AddTransient<IRequestPostProcessor<ProcessorRequest, string>, TestPostProcessor>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new ProcessorRequest("Y"));

        Assert.True(ProcessorRequestHandler.ExecutionLog.IndexOf("Handler") <
                     ProcessorRequestHandler.ExecutionLog.IndexOf("PostProcessor:Result:Y"));
    }

    [Fact]
    public async Task MultiplePreProcessors_AllExecute()
    {
        ProcessorRequestHandler.ExecutionLog.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddRequestPreProcessor(typeof(TestPreProcessor));
        });
        services.AddTransient<IRequestHandler<ProcessorRequest, string>, ProcessorRequestHandler>();
        services.AddTransient<IRequestPreProcessor<ProcessorRequest>, TestPreProcessor>();
        services.AddTransient<IRequestPreProcessor<ProcessorRequest>, TestPreProcessor2>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new ProcessorRequest("Z"));

        Assert.Contains("PreProcessor", ProcessorRequestHandler.ExecutionLog);
        Assert.Contains("PreProcessor2", ProcessorRequestHandler.ExecutionLog);
        Assert.Contains("Handler", ProcessorRequestHandler.ExecutionLog);
    }

    [Fact]
    public async Task ExecutionOrder_PreProcessors_Then_Handler_Then_PostProcessors()
    {
        ProcessorRequestHandler.ExecutionLog.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddRequestPreProcessor(typeof(TestPreProcessor));
            cfg.AddRequestPostProcessor(typeof(TestPostProcessor));
        });
        services.AddTransient<IRequestHandler<ProcessorRequest, string>, ProcessorRequestHandler>();
        services.AddTransient<IRequestPreProcessor<ProcessorRequest>, TestPreProcessor>();
        services.AddTransient<IRequestPostProcessor<ProcessorRequest, string>, TestPostProcessor>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new ProcessorRequest("Full"));

        var preIdx = ProcessorRequestHandler.ExecutionLog.IndexOf("PreProcessor");
        var handlerIdx = ProcessorRequestHandler.ExecutionLog.IndexOf("Handler");
        var postIdx = ProcessorRequestHandler.ExecutionLog.IndexOf("PostProcessor:Result:Full");

        Assert.True(preIdx < handlerIdx, "PreProcessor should execute before Handler");
        Assert.True(handlerIdx < postIdx, "Handler should execute before PostProcessor");
    }
}
