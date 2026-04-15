using System.Globalization;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Tests;

public class LocalizedValidationBehaviorTests
{
    public sealed record CreateWorkflow(string Title) : IRequest<Unit>;

    public sealed class CreateWorkflowHandler : IRequestHandler<CreateWorkflow, Unit>
    {
        public Task<Unit> Handle(CreateWorkflow r, CancellationToken ct) =>
            Task.FromResult(Unit.Value);
    }

    public sealed class CreateWorkflowValidator : IValidator<CreateWorkflow>
    {
        public Task<ValidationResult> ValidateAsync(CreateWorkflow r, CancellationToken ct)
        {
            var result = new ValidationResult();
            if (string.IsNullOrEmpty(r.Title))
            {
                result.Errors.Add(new ValidationError(
                    nameof(r.Title), "Workflow.Title.Required"));
            }
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Stub localiser that maps known resource keys to canned strings —
    /// stands in for an IStringLocalizerFactory backed by .resx files.
    /// </summary>
    private sealed class StubLocalizer : IStringLocalizer<CreateWorkflow>
    {
        private readonly Dictionary<string, string> _map;
        public StubLocalizer(Dictionary<string, string> map) { _map = map; }

        public LocalizedString this[string name] =>
            _map.TryGetValue(name, out var v)
                ? new LocalizedString(name, v, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            _map.Select(kv => new LocalizedString(kv.Key, kv.Value));
    }

    private static IMediator BuildMediator(IStringLocalizer<CreateWorkflow> localizer)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IValidator<CreateWorkflow>, CreateWorkflowValidator>();
        services.AddSingleton(localizer);
        services.AddMeridianMediator(c =>
        {
            c.RegisterServicesFromAssembly(typeof(LocalizedValidationBehaviorTests).Assembly);
            c.AddLocalizedValidationBehavior();
        });
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Resource_Key_Is_Replaced_With_Localised_Message()
    {
        var localizer = new StubLocalizer(new Dictionary<string, string>
        {
            ["Workflow.Title.Required"] = "İş akışı başlığı zorunludur.",
        });
        var mediator = BuildMediator(localizer);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            mediator.Send(new CreateWorkflow("")));

        Assert.Single(ex.Errors);
        Assert.Equal("Title", ex.Errors[0].PropertyName);
        Assert.Equal("İş akışı başlığı zorunludur.", ex.Errors[0].ErrorMessage);
    }

    [Fact]
    public async Task Missing_Resource_Key_Falls_Back_To_Raw_Message_Unchanged()
    {
        // Empty localiser — every key reports ResourceNotFound. Behaviour
        // must keep the raw "Workflow.Title.Required" string rather than
        // letting an empty/blank LocalizedString.Value leak through.
        var localizer = new StubLocalizer(new Dictionary<string, string>());
        var mediator = BuildMediator(localizer);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            mediator.Send(new CreateWorkflow("")));

        Assert.Equal("Workflow.Title.Required", ex.Errors[0].ErrorMessage);
    }

    [Fact]
    public async Task Valid_Request_Bypasses_Localisation_Path()
    {
        var localizer = new StubLocalizer(new Dictionary<string, string>());
        var mediator = BuildMediator(localizer);

        var response = await mediator.Send(new CreateWorkflow("ok"));

        Assert.Equal(Unit.Value, response);
    }
}
