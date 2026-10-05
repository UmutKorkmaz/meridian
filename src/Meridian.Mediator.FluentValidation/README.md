# Meridian.Mediator.FluentValidation

Optional asynchronous FluentValidation integration for Meridian Mediator on .NET 8, 9 and 10.

<!-- illustrative: configuration fragment, not compiled as shown, runnable equivalent in the packaged consumer; fixture: tests/consumers/Meridian.FluentValidationConsumer/Program.cs -->
```csharp
using FluentValidation;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.FluentValidation;

services.AddMeridianMediator(c =>
{
    c.RegisterServicesFromAssemblyContaining<CreateOrder>();
    c.AddValidationBehavior();
});
services.AddMeridianFluentValidation(typeof(CreateOrderValidator).Assembly);

public sealed class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator(IOrderPolicy policy)
    {
        RuleFor(x => x.CustomerId)
            .MustAsync((id, cancellationToken) => policy.CanOrderAsync(id, cancellationToken))
            .WithMessage("Order.Customer.Invalid");
    }
}
```

The adapter awaits `ValidateAsync`, which executes synchronous and asynchronous rules, forwards the original cancellation token, and maps every failure's property name and message into Meridian validation errors. All validators run sequentially; invalid requests prevent handler execution. Cancellation and validator exceptions propagate to the caller.

Public non-abstract typed validators in explicitly supplied assemblies are registered using FluentValidation's scanner with scoped lifetimes. Register their scoped dependencies in the same container. Repeated calls and repeated assemblies add only one adapter per distinct closed typed validator service. Existing typed FluentValidation registrations (including factories and instances) are supported: register them first, then call `services.AddMeridianFluentValidation()` without assemblies. If additional request types are registered later, call the method again before building the provider. Open generic validator registrations require closed typed registrations to identify the adapted request types.

Consumer-supplied Meridian validators are retained alongside the adapter. An empty validator collection is valid. The registration method installs adapters only: explicitly select one Meridian validation behavior. To translate message keys, use `AddLocalizedValidationBehavior()` in place of `AddValidationBehavior()` and register your `IStringLocalizer<TRequest>`. Missing keys retain the original message. The adapter leaves the localizer registration intact.

This package directly depends on Meridian.Mediator, FluentValidation 12.1.1, FluentValidation.DependencyInjectionExtensions 12.1.1 and Microsoft.Extensions.DependencyInjection.Abstractions 10.0.9. Both FluentValidation packages use Apache-2.0; Meridian uses MIT. FluentValidation and other backend integrations remain outside Meridian's core packages. This integration does not install ASP.NET MVC automatic validation; asynchronous rules must run through the async mediator pipeline.

See the official [async validation](https://docs.fluentvalidation.net/en/latest/async.html), [typed registration and scanning](https://docs.fluentvalidation.net/en/latest/di.html), and [FluentValidation 12 support floor](https://docs.fluentvalidation.net/en/latest/upgrading-to-12.html) documentation.
