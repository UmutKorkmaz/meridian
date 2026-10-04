using FluentValidation;
using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MValidationException = Meridian.Mediator.Behaviors.ValidationException;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var services = new ServiceCollection();
var counts = new Counts();
services.AddSingleton(counts);
services.AddScoped<ScopeState>();
services.AddSingleton<IStringLocalizer<Command>, Localizer>();
services.AddTransient<IRequestHandler<Command, int>, Handler>();
services.AddTransient<IRequestHandler<Empty, int>, EmptyHandler>();
services.AddSingleton<Meridian.Mediator.Behaviors.IValidator<Command>, ManualValidator>();
services.AddValidatorsFromAssemblyContaining<Validator>();
services.AddMeridianFluentValidation(typeof(Validator).Assembly, typeof(Validator).Assembly);
services.AddMeridianFluentValidation(typeof(Validator).Assembly);
services.AddMeridianMediator(c => c.AddLocalizedValidationBehavior());
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
ScopeState first;
using (var scope = provider.CreateScope())
{
    var sender = scope.ServiceProvider.GetRequiredService<IMediator>();
    first = scope.ServiceProvider.GetRequiredService<ScopeState>();
    using var cancel = new CancellationTokenSource();
    var invalid = new Command("", cancel.Token, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
    var send = sender.Send(invalid, cancel.Token);
    await Task.WhenAny(invalid.Entered.Task, send).WaitAsync(TimeSpan.FromSeconds(10));
    Require(invalid.Entered.Task.IsCompletedSuccessfully && !send.IsCompleted && counts.Handler == 0, "Async barrier");
    Require(await invalid.Entered.Task == cancel.Token, "Token identity");
    invalid.Release.SetResult(false);
    try { await send; throw new InvalidOperationException("Validation should fail"); }
    catch (MValidationException error)
    {
        Require(error.Errors.SequenceEqual(new[] { new Meridian.Mediator.Behaviors.ValidationError("Name", "manual"), new Meridian.Mediator.Behaviors.ValidationError("Name", "translated"), new Meridian.Mediator.Behaviors.ValidationError("Name", "raw") }), "Exact localized aggregate");
    }
    Require(first.Calls == 1 && counts.Handler == 0, "Duplicate validation");
    Require(scope.ServiceProvider.GetServices<Meridian.Mediator.Behaviors.IValidator<Command>>().Count() == 2, "Manual + one adapter");
    var cancelled = new Command("ok", cancel.Token, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
    var pending = sender.Send(cancelled, cancel.Token);
    await Task.WhenAny(cancelled.Entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
    Require(cancelled.Entered.Task.IsCompletedSuccessfully, "Cancellation barrier");
    cancel.Cancel();
    try { await pending; throw new InvalidOperationException("Cancellation should throw"); }
    catch (OperationCanceledException error) { Require(error.CancellationToken == cancel.Token && counts.Handler == 0, "Cancellation reaches rule"); }
}
Require(first.Disposed, "Scope disposal");
using (var scope = provider.CreateScope())
{
    var second = scope.ServiceProvider.GetRequiredService<ScopeState>();
    Require(first.Id != second.Id, "Scope isolation");
    var valid = new Command("ok", default, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
    valid.Release.SetResult(true);
    Require(await scope.ServiceProvider.GetRequiredService<IMediator>().Send(valid) == 42, "Valid handler");
    Require(second.Calls == 1 && counts.Handler == 1, "Valid exactly once");
}
var emptyServices = new ServiceCollection();
emptyServices.AddTransient<IRequestHandler<Empty, int>, EmptyHandler>();
emptyServices.AddMeridianFluentValidation();
emptyServices.AddMeridianMediator(c => c.AddValidationBehavior());
using var emptyProvider = emptyServices.BuildServiceProvider();
Require(await emptyProvider.GetRequiredService<IMediator>().Send(new Empty()) == 7, "Zero validators");
foreach (var assembly in new[] { typeof(FluentValidationAdapter<>).Assembly, typeof(IMediator).Assembly, typeof(AbstractValidator<>).Assembly })
{
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant();
    Console.WriteLine($"LOADED assembly={assembly.GetName().Name} path={assembly.Location} sha256={hash}");
}
Console.WriteLine("COMMON fluentvalidation async=awaited cancellation=token-identical aggregation=exact handlers=1 scopes=isolated localizer=preserved duplicate=once zero=pass isolatedNuGet=true");
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
public sealed record Command(string Name, CancellationToken Expected, TaskCompletionSource<CancellationToken> Entered, TaskCompletionSource<bool> Release) : IRequest<int>;
public sealed record Empty : IRequest<int>;
public sealed class Counts { public int Handler { get; set; } }
public sealed class ScopeState : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public int Calls { get; set; }
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
public sealed class Validator : AbstractValidator<Command>
{
    public Validator(ScopeState state)
    {
        RuleFor(x => x.Name).MustAsync(async (request, _, ct) =>
        {
            if (ct != request.Expected) throw new InvalidOperationException("Rule token mismatch");
            state.Calls++;
            request.Entered.SetResult(ct);
            return await request.Release.Task.WaitAsync(ct);
        }).WithMessage("key");
        RuleFor(x => x.Name).NotEmpty().WithMessage("raw");
    }
}
public sealed class ManualValidator : Meridian.Mediator.Behaviors.IValidator<Command>
{
    public Task<Meridian.Mediator.Behaviors.ValidationResult> ValidateAsync(Command request, CancellationToken ct) =>
        Task.FromResult(new Meridian.Mediator.Behaviors.ValidationResult { Errors = request.Name.Length == 0 ? [new("Name", "manual")] : [] });
}
public sealed class Handler(Counts counts) : IRequestHandler<Command, int>
{
    public Task<int> Handle(Command request, CancellationToken cancellationToken) { counts.Handler++; return Task.FromResult(42); }
}
public sealed class EmptyHandler : IRequestHandler<Empty, int>
{
    public Task<int> Handle(Empty request, CancellationToken cancellationToken) => Task.FromResult(7);
}
public sealed class Localizer : IStringLocalizer<Command>
{
    public LocalizedString this[string name] => new(name, name == "key" ? "translated" : name, name != "key");
    public LocalizedString this[string name, params object[] arguments] => this[name];
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
