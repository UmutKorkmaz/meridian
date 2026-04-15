using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Tests;

public class StandardPipelineExtensionsTests
{
    public sealed record StdPing(int Value) : IRequest<StdPong>;
    public sealed record StdPong(int Value);

    public sealed class StdPingHandler : IRequestHandler<StdPing, StdPong>
    {
        public Task<StdPong> Handle(StdPing r, CancellationToken ct) =>
            Task.FromResult(new StdPong(r.Value));
    }

    private sealed class NoopLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: true);
        public LocalizedString this[string name, params object[] arguments] => this[name];
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Array.Empty<LocalizedString>();
    }

    [Fact]
    public async Task AddMeridianStandard_Wires_Mediator_End_To_End()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(NoopLocalizer<>));

        services.AddMeridianStandard(typeof(StandardPipelineExtensionsTests).Assembly);

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var response = await mediator.Send(new StdPing(42));

        Assert.Equal(42, response.Value);
    }

    [Fact]
    public void AddMeridianStandard_Throws_On_Empty_Assemblies()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddMeridianStandard(Array.Empty<System.Reflection.Assembly>()));
    }

    [Fact]
    public void AddMeridianStandard_Registers_Default_AuditSink_When_None_Supplied()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(NoopLocalizer<>));

        services.AddMeridianStandard(typeof(StandardPipelineExtensionsTests).Assembly);

        var provider = services.BuildServiceProvider();
        var sink = provider.GetService<IAuditSink>();

        Assert.NotNull(sink);
        Assert.IsType<LoggerAuditSink>(sink);
    }

    private sealed class CapturingSink : IAuditSink
    {
        public int Count { get; private set; }
        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void AddMeridianStandard_Honours_Pre_Registered_AuditSink()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(NoopLocalizer<>));
        services.AddSingleton<IAuditSink, CapturingSink>();

        services.AddMeridianStandard(typeof(StandardPipelineExtensionsTests).Assembly);

        var provider = services.BuildServiceProvider();
        var sink = provider.GetRequiredService<IAuditSink>();

        Assert.IsType<CapturingSink>(sink);
    }
}
