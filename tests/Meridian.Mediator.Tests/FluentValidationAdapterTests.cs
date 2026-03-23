using FluentValidation;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public record FluentValidatedRequest(string Email) : IRequest<string>;

public class FluentValidatedRequestValidator : AbstractValidator<FluentValidatedRequest>
{
    public FluentValidatedRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Email is required");
        RuleFor(x => x.Email).EmailAddress().WithMessage("Email is invalid");
    }
}

public class FluentValidatedRequestDomainValidator : AbstractValidator<FluentValidatedRequest>
{
    public FluentValidatedRequestDomainValidator()
    {
        RuleFor(x => x.Email).Must(email => !string.IsNullOrWhiteSpace(email) && email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Email must use example.com");
    }
}

public class FluentValidatedRequestHandler : IRequestHandler<FluentValidatedRequest, string>
{
    public Task<string> Handle(FluentValidatedRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"ok:{request.Email}");
    }
}

public class FluentValidationAdapterTests
{
    [Fact]
    public async Task FluentValidationAdapter_Aggregates_All_FluentValidation_Errors()
    {
        var adapter = new FluentValidationAdapter<FluentValidatedRequest>(
            new global::FluentValidation.IValidator<FluentValidatedRequest>[]
            {
                new FluentValidatedRequestValidator(),
                new FluentValidatedRequestDomainValidator()
            });

        var result = await adapter.ValidateAsync(new FluentValidatedRequest(string.Empty), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "Email");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Email must use example.com");
    }

    [Fact]
    public async Task AddFluentValidationFromAssembly_Enables_ValidationBehavior()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<FluentValidationAdapterTests>();
            cfg.AddFluentValidationFromAssemblyContaining<FluentValidationAdapterTests>();
            cfg.AddValidationBehavior();
        });
        services.AddTransient<IRequestHandler<FluentValidatedRequest, string>, FluentValidatedRequestHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var exception = await Assert.ThrowsAsync<Meridian.Mediator.Behaviors.ValidationException>(
            () => mediator.Send(new FluentValidatedRequest("not-an-email")));

        Assert.Contains(exception.Errors, e => e.ErrorMessage == "Email is invalid");
        Assert.Contains(exception.Errors, e => e.ErrorMessage == "Email must use example.com");
    }

    [Fact]
    public async Task AddMeridianMediator_Does_Not_Enable_FluentValidation_Without_Explicit_Registration()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<FluentValidationAdapterTests>();
            cfg.AddValidationBehavior();
        });
        services.AddTransient<IRequestHandler<FluentValidatedRequest, string>, FluentValidatedRequestHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new FluentValidatedRequest("not-an-email"));

        Assert.Equal("ok:not-an-email", result);
    }
}
