using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Tests;

public class StandardPipelineExtensionsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Repeated_Standard_Registration_Mixed_Behaviors_Run_Once_In_Order(bool valid, bool instanceValidator)
    {
        var probe = new PipelineProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(NoopLocalizer<>));
        services.AddSingleton(probe);
        services.AddSingleton<IAuditSink>(probe);
        services.AddSingleton<ITransactionScopeProvider>(probe);
        if (instanceValidator)
            services.AddSingleton<IValidator<RegistrationRequest>>(new RegistrationValidator(probe));
        else
            services.AddScoped<IValidator<RegistrationRequest>, RegistrationValidator>();

        for (var i = 0; i < 2; i++)
        {
            services.AddMeridianStandard(typeof(StandardPipelineExtensionsTests).Assembly);
            services.AddMeridianMediator(configuration =>
            {
                configuration.RegisterServicesFromAssemblyContaining<RegistrationHandler>();
                configuration.AddBehavior<RegistrationClosedBehavior>();
                configuration.AddOpenBehavior(typeof(RegistrationOpenBehavior<,>), order: 10);
                configuration.AddTransactionBehavior(order: 20);
            });
        }

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        if (valid)
        {
            Assert.Equal("accepted", await mediator.Send(new RegistrationRequest(true)));
            Assert.Equal(new[] { "validate", "closed-before", "open-before", "begin", "handler",
                "commit", "dispose", "open-after", "closed-after", "audit" }, probe.Events);
            Assert.Equal(1, probe.TransactionCount);
        }
        else
        {
            await Assert.ThrowsAsync<ValidationException>(() => mediator.Send(new RegistrationRequest(false)));
            Assert.Equal(new[] { "validate", "audit" }, probe.Events);
            Assert.Equal(0, probe.TransactionCount);
        }

        Assert.Equal(1, probe.ValidatorCount);
        Assert.Single(probe.Audits);
        Assert.False(string.IsNullOrWhiteSpace(probe.ValidationCorrelationId));
        Assert.Equal(probe.ValidationCorrelationId, probe.Audits[0].CorrelationId);
        Assert.Equal(valid, probe.Audits[0].Success);
        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IValidator<RegistrationRequest>));
        Assert.Equal(instanceValidator ? ServiceLifetime.Singleton : ServiceLifetime.Scoped, descriptor.Lifetime);
        var validator = Assert.Single(scope.ServiceProvider.GetServices<IValidator<RegistrationRequest>>());
        Assert.IsType<RegistrationValidator>(validator);
        if (instanceValidator)
            Assert.Same(descriptor.ImplementationInstance, validator);
    }

    public sealed record RegistrationRequest(bool Valid) : IRequest<string>, ITransactionalRequest;

    public sealed class PipelineProbe : IAuditSink, ITransactionScopeProvider, ITransactionScope
    {
        public List<string> Events { get; } = new();
        public List<AuditEntry> Audits { get; } = new();
        public int ValidatorCount { get; set; }
        public int TransactionCount { get; private set; }
        public string? ValidationCorrelationId { get; set; }
        public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            Events.Add("audit");
            Audits.Add(entry);
            return Task.CompletedTask;
        }
        public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
        {
            TransactionCount++;
            Events.Add("begin");
            return Task.FromResult<ITransactionScope>(this);
        }
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Events.Add("commit");
            return Task.CompletedTask;
        }
        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            Events.Add("rollback");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class RegistrationHandler(PipelineProbe probe) : IRequestHandler<RegistrationRequest, string>
    {
        public Task<string> Handle(RegistrationRequest request, CancellationToken cancellationToken)
        {
            probe.Events.Add("handler");
            return Task.FromResult("accepted");
        }
    }

    public sealed class RegistrationValidator(PipelineProbe probe) : IValidator<RegistrationRequest>
    {
        public Task<ValidationResult> ValidateAsync(RegistrationRequest request, CancellationToken cancellationToken)
        {
            probe.ValidatorCount++;
            probe.ValidationCorrelationId = CorrelationContext.CorrelationId;
            probe.Events.Add("validate");
            var result = new ValidationResult();
            if (!request.Valid)
                result.Errors.Add(new ValidationError("Valid", "Required"));
            return Task.FromResult(result);
        }
    }

    public sealed class RegistrationClosedBehavior(PipelineProbe probe) : IPipelineBehavior<RegistrationRequest, string>
    {
        public async Task<string> Handle(RegistrationRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            probe.Events.Add("closed-before");
            var result = await next();
            probe.Events.Add("closed-after");
            return result;
        }
    }

    public sealed class RegistrationOpenBehavior<TRequest, TResponse>(PipelineProbe probe) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            probe.Events.Add("open-before");
            var result = await next();
            probe.Events.Add("open-after");
            return result;
        }
    }

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
