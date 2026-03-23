using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.EventDrivenCqrs;

internal static class EventDrivenDependencyInjection
{
    public static IServiceCollection AddEventDrivenCqrsServices(this IServiceCollection services)
    {
        services.AddSingleton<IOrderWriteStore, InMemoryOrderWriteStore>();
        services.AddSingleton<IOrderReadStore, InMemoryOrderReadStore>();
        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        services.AddSingleton<IMediatorLogger, ConsoleMediatorLogger>();

        services.AddMeridianMapping(cfg => cfg.AddProfile<EventDrivenProfile>());

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<EventDrivenMarker>();
            cfg.AddValidationBehavior();
            cfg.AddLoggingBehavior();
            cfg.AddCachingBehavior();
        });

        services.AddTransient<IValidator<Commands.OpenOrderCommand>, Commands.OpenOrderCommandValidator>();
        services.AddTransient<IValidator<Commands.AdvanceOrderCommand>, Commands.AdvanceOrderCommandValidator>();

        return services;
    }
}
