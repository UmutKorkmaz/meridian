using Meridian.Mapping;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.VerticalSlice;

internal static class DependencyInjection
{
    public static IServiceCollection AddVerticalSliceServices(this IServiceCollection services)
    {
        services.AddSingleton<ITodoStore, InMemoryTodoStore>();
        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        services.AddSingleton<IMediatorLogger, ConsoleMediatorLogger>();
        services.AddSingleton<TodoProfile>();

        services.AddMeridianMapping(cfg => cfg.AddProfile<TodoProfile>());

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<VerticalSliceMarker>();
            cfg.AddValidationBehavior();
            cfg.AddLoggingBehavior();
            cfg.AddCachingBehavior();
        });

        services.AddTransient<IValidator<Backlog.GetTodoBoardQuery>, Backlog.GetTodoBoardQueryValidator>();
        services.AddTransient<IValidator<Backlog.CreateTodoCommand>, Backlog.CreateTodoCommandValidator>();
        services.AddTransient<IValidator<Backlog.StartTodoCommand>, Backlog.StartTodoCommandValidator>();

        return services;
    }
}
