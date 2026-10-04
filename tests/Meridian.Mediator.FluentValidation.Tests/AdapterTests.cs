using FluentValidation;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MValidator = Meridian.Mediator.Behaviors.IValidator<Meridian.Mediator.FluentValidation.Tests.AdapterTests.Request>;
using MValidationException = Meridian.Mediator.Behaviors.ValidationException;

namespace Meridian.Mediator.FluentValidation.Tests;

public sealed class AdapterTests
{
    public sealed record Request(string Name) : IRequest<int>;
    public sealed record ScanRequest(string Name) : IRequest<int>;
    public sealed record OtherRequest(string Name) : IRequest<int>;
    private sealed class Counts { public int Handler; public int First; public int Second; }
    private sealed class Handler(Counts counts) : IRequestHandler<Request, int>
    {
        public Task<int> Handle(Request request, CancellationToken cancellationToken) => Task.FromResult(++counts.Handler);
    }
    private sealed class ScanHandler(Counts counts) : IRequestHandler<ScanRequest, int>
    {
        public Task<int> Handle(ScanRequest request, CancellationToken cancellationToken) => Task.FromResult(++counts.Handler);
    }
    private sealed class Rules : AbstractValidator<Request>
    {
        public Rules(Action<Rules> configure) => configure(this);
    }
    private sealed class Manual : MValidator
    {
        public Task<Behaviors.ValidationResult> ValidateAsync(Request instance, CancellationToken cancellationToken) =>
            Task.FromResult(new Behaviors.ValidationResult { Errors = [new ValidationError("Name", "manual")] });
    }
    public sealed class ScopeState : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Calls { get; set; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    public sealed class ScanValidator : AbstractValidator<ScanRequest>
    {
        public ScanValidator(ScopeState state) => RuleFor(x => x.Name).Must(_ => { state.Calls++; return false; }).WithMessage("scan");
    }
    public sealed class OtherValidator : AbstractValidator<OtherRequest>
    {
        public OtherValidator() => RuleFor(x => x.Name).NotEmpty().WithMessage("other");
    }
    private sealed class Localizer : IStringLocalizer<Request>
    {
        public LocalizedString this[string name] => new(name, name == "key" ? "translated" : name, name != "key");
        public LocalizedString this[string name, params object[] arguments] => this[name];
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
    private static ServiceCollection Services(Counts counts, bool localized = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(counts);
        services.AddTransient<IRequestHandler<Request, int>, Handler>();
        services.AddTransient<IRequestHandler<ScanRequest, int>, ScanHandler>();
        services.AddMeridianMediator(c => { if (localized) c.AddLocalizedValidationBehavior(); else c.AddValidationBehavior(); });
        return services;
    }
    private static ServiceProvider Provider(ServiceCollection services) => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    private static void Errors(MValidationException error, params ValidationError[] expected) => Assert.Equal(expected, error.Errors);

    [Fact]
    public async Task Async_Rule_Is_Awaited()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new Counts();
        var services = Services(counts);
        services.AddScoped<global::FluentValidation.IValidator<Request>>(_ => new Rules(v => v.RuleFor(x => x.Name).MustAsync(async (_, ct) => { entered.SetResult(); return await release.Task.WaitAsync(ct); }).WithMessage("async-error")));
        services.AddMeridianFluentValidation();
        using var provider = Provider(services);
        using var scope = provider.CreateScope();
        var send = scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Request("x"));
        try
        {
            await Task.WhenAny(entered.Task, send).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(entered.Task.IsCompletedSuccessfully);
            Assert.False(send.IsCompleted);
            Assert.Equal(0, counts.Handler);
        }
        finally { release.TrySetResult(false); }
        Errors(await Assert.ThrowsAsync<MValidationException>(() => send), new ValidationError("Name", "async-error"));
        Assert.Equal(0, counts.Handler);
    }

    [Fact]
    public async Task Cancellation_Reaches_Rule()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new Counts();
        var services = Services(counts);
        services.AddScoped<global::FluentValidation.IValidator<Request>>(_ => new Rules(v => v.RuleFor(x => x.Name).MustAsync(async (_, ct) => { entered.SetResult(ct); return await release.Task.WaitAsync(ct); })));
        services.AddMeridianFluentValidation();
        using var provider = Provider(services);
        using var scope = provider.CreateScope();
        using var source = new CancellationTokenSource();
        var send = scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Request("x"), source.Token);
        try
        {
            await Task.WhenAny(entered.Task, send).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(entered.Task.IsCompletedSuccessfully);
            Assert.Equal(source.Token, await entered.Task);
            source.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            Assert.Equal(source.Token, error.CancellationToken);
            Assert.Equal(0, counts.Handler);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task Multiple_Validators_Aggregate()
    {
        var counts = new Counts();
        var services = Services(counts);
        services.AddScoped<global::FluentValidation.IValidator<Request>>(_ => new Rules(v => v.RuleFor(x => x.Name).Must(_ => { counts.First++; return false; }).WithMessage("first")));
        services.AddScoped<global::FluentValidation.IValidator<Request>>(_ => new Rules(v => v.RuleFor(x => x.Name).Must(_ => { counts.Second++; return false; }).WithMessage("second")));
        services.AddMeridianFluentValidation();
        using var provider = Provider(services);
        using var scope = provider.CreateScope();
        Errors(await Assert.ThrowsAsync<MValidationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Request("x"))), new ValidationError("Name", "first"), new ValidationError("Name", "second"));
        Assert.Equal(1, counts.First); Assert.Equal(1, counts.Second); Assert.Equal(0, counts.Handler);
        Assert.Single(scope.ServiceProvider.GetServices<MValidator>());
    }

    [Fact]
    public async Task Duplicate_Registration_Does_Not_Double_Validate()
    {
        var counts = new Counts();
        var services = Services(counts);
        services.AddScoped<ScopeState>();
        services.AddValidatorsFromAssemblyContaining<ScanValidator>();
        Assert.Same(services, services.AddMeridianFluentValidation(typeof(ScanValidator).Assembly, typeof(ScanValidator).Assembly));
        services.AddMeridianFluentValidation(typeof(ScanValidator).Assembly);
        using var provider = Provider(services);
        using var scope = provider.CreateScope();
        Errors(await Assert.ThrowsAsync<MValidationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ScanRequest("x"))), new ValidationError("Name", "scan"));
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<ScopeState>().Calls);
        Assert.Equal(0, counts.Handler);
        Assert.Single(scope.ServiceProvider.GetServices<Behaviors.IValidator<ScanRequest>>());
        Assert.Single(scope.ServiceProvider.GetServices<Behaviors.IValidator<OtherRequest>>());
    }

    [Fact]
    public async Task Public_Assembly_Scanner_Preserves_Scoped_Dependencies()
    {
        var counts = new Counts(); var services = Services(counts); services.AddScoped<ScopeState>();
        services.AddMeridianFluentValidation(typeof(ScanValidator).Assembly);
        using var provider = Provider(services);
        ScopeState first;
        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<ScopeState>();
            Errors(await Assert.ThrowsAsync<MValidationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ScanRequest("x"))), new ValidationError("Name", "scan"));
            Assert.Equal(1, first.Calls); Assert.False(first.Disposed);
        }
        Assert.True(first.Disposed);
        using var second = provider.CreateScope(); var state = second.ServiceProvider.GetRequiredService<ScopeState>();
        Assert.NotEqual(first.Id, state.Id);
        Errors(await Assert.ThrowsAsync<MValidationException>(() => second.ServiceProvider.GetRequiredService<IMediator>().Send(new ScanRequest("x"))), new ValidationError("Name", "scan"));
        Assert.Equal(1, state.Calls); Assert.Equal(0, counts.Handler);
    }

    [Fact]
    public async Task Existing_Typed_Validator_Uses_Consumer_Scope()
    {
        var counts = new Counts(); var services = Services(counts); services.AddScoped<ScopeState>();
        services.AddScoped<global::FluentValidation.IValidator<Request>>(sp => new Rules(v => v.RuleFor(x => x.Name).Must(_ => { sp.GetRequiredService<ScopeState>().Calls++; return true; })));
        services.AddMeridianFluentValidation();
        using var provider = Provider(services);
        ScopeState first;
        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<ScopeState>();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Request("x")));
            Assert.Equal(1, first.Calls);
        }
        Assert.True(first.Disposed);
        using var second = provider.CreateScope(); var state = second.ServiceProvider.GetRequiredService<ScopeState>();
        Assert.Equal(2, await second.ServiceProvider.GetRequiredService<IMediator>().Send(new Request("x")));
        Assert.NotEqual(first.Id, state.Id); Assert.Equal(1, state.Calls); Assert.Equal(2, counts.Handler);
    }

    [Fact]
    public async Task Manual_Meridian_Validator_Is_Preserved()
    {
        var counts = new Counts(); var services = Services(counts);
        var manual = new Manual(); services.AddSingleton<MValidator>(manual);
        services.AddScoped<global::FluentValidation.IValidator<Request>>(_ => new Rules(v => v.RuleFor(x => x.Name).NotEmpty().WithMessage("fv")));
        services.AddMeridianFluentValidation(); services.AddMeridianFluentValidation();
        using var provider = Provider(services); using var scope = provider.CreateScope();
        Assert.Contains(manual, scope.ServiceProvider.GetServices<MValidator>());
        Errors(await Assert.ThrowsAsync<MValidationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Request(""))), new ValidationError("Name", "manual"), new ValidationError("Name", "fv"));
        Assert.Equal(0, counts.Handler);
    }

    [Fact]
    public async Task Localizer_Is_Preserved_And_Missing_Key_Falls_Back()
    {
        var counts = new Counts(); var services = Services(counts, localized: true);
        var localizer = new Localizer(); services.AddSingleton<IStringLocalizer<Request>>(localizer);
        services.AddScoped<global::FluentValidation.IValidator<Request>>(_ => new Rules(v => { v.RuleFor(x => x.Name).NotEmpty().WithMessage("key"); v.RuleFor(x => x.Name).NotEmpty().WithMessage("raw"); }));
        services.AddMeridianFluentValidation();
        using var provider = Provider(services); using var scope = provider.CreateScope();
        Assert.Same(localizer, scope.ServiceProvider.GetRequiredService<IStringLocalizer<Request>>());
        Errors(await Assert.ThrowsAsync<MValidationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Request(""))), new ValidationError("Name", "translated"), new ValidationError("Name", "raw"));
        Assert.Equal(0, counts.Handler);
    }

    [Fact]
    public async Task Zero_Validators_Passes_And_Valid_Request_Runs_Handler_Once()
    {
        var counts = new Counts(); var services = Services(counts); services.AddMeridianFluentValidation();
        using var provider = Provider(services); using var scope = provider.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetServices<MValidator>());
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Request("x"))); Assert.Equal(1, counts.Handler);
        var adapter = new FluentValidationAdapter<Request>([]);
        Assert.True((await adapter.ValidateAsync(new Request("x"), default)).IsValid);
    }

    [Fact]
    public void Invalid_Registration_Arguments_Fail_At_Registration()
    {
        Assert.Throws<ArgumentNullException>(() => Meridian.Mediator.FluentValidation.ServiceCollectionExtensions.AddMeridianFluentValidation(null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddMeridianFluentValidation(null!));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMeridianFluentValidation([null!]));
        Assert.Throws<ArgumentNullException>(() => new FluentValidationAdapter<Request>(null!));
    }

    [Fact]
    public async Task Adapter_Maps_Exact_Property_And_Message()
    {
        var adapter = new FluentValidationAdapter<Request>([new Rules(v => v.RuleFor(x => x.Name).NotEmpty().WithMessage("required"))]);
        var result = await adapter.ValidateAsync(new Request(""), default);
        Assert.False(result.IsValid); Assert.Equal([new ValidationError("Name", "required")], result.Errors);
    }
}
