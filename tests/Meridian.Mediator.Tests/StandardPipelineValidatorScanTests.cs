using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Tests;

/// <summary>
/// Locks in the AddMeridianStandard contract that IValidator&lt;T&gt;
/// implementations are auto-discovered from the supplied assemblies.
/// Without this, the wired LocalizedValidationBehavior silently no-ops
/// when validators exist in the assembly but are never registered —
/// exactly the bug the QuickStart sample caught the first time.
/// </summary>
public class StandardPipelineValidatorScanTests
{
    public sealed record SignUp(string Email) : IRequest<Unit>;

    public sealed class SignUpHandler : IRequestHandler<SignUp, Unit>
    {
        public Task<Unit> Handle(SignUp request, CancellationToken cancellationToken) =>
            Task.FromResult(Unit.Value);
    }

    public sealed class SignUpValidator : IValidator<SignUp>
    {
        public Task<ValidationResult> ValidateAsync(SignUp instance, CancellationToken cancellationToken)
        {
            var result = new ValidationResult();
            if (string.IsNullOrEmpty(instance.Email))
                result.Errors.Add(new ValidationError(nameof(instance.Email), "Email.Required"));
            return Task.FromResult(result);
        }
    }

    private sealed class EchoLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public LocalizedString this[string name, params object[] args] => this[name];
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Array.Empty<LocalizedString>();
    }

    [Fact]
    public async Task AddMeridianStandard_Auto_Registers_Validators_So_Validation_Actually_Fires()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(EchoLocalizer<>));

        // ONLY AddMeridianStandard — no separate AddSingleton<IValidator<>, ...>.
        services.AddMeridianStandard(typeof(StandardPipelineValidatorScanTests).Assembly);

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<ValidationException>(() =>
            mediator.Send(new SignUp("")));
    }

    [Fact]
    public void AddMeridianStandard_Registers_The_Validator_In_The_DI_Container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(EchoLocalizer<>));

        services.AddMeridianStandard(typeof(StandardPipelineValidatorScanTests).Assembly);

        var provider = services.BuildServiceProvider();
        var validators = provider.GetServices<IValidator<SignUp>>().ToList();

        Assert.Single(validators);
        Assert.IsType<SignUpValidator>(validators[0]);
    }
}
