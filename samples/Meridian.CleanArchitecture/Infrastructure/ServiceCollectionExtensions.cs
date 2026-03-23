using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.CleanArchitecture;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCleanArchitectureServices(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IMediatorLogger, DemoMediatorLogger>();
        services.AddSingleton<IProductCatalog, InMemoryProductCatalog>();
        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        services.AddSingleton<ITransactionScopeProvider, DemoTransactionScopeProvider>();

        services.AddMeridianMapping(cfg =>
        {
            cfg.AddProfile<CatalogProfile>();
            cfg.ValueTransformers.Add<string>(value => value.Trim());
        });

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CleanArchitectureAssemblyMarker>();
            cfg.AddValidationBehavior();
            cfg.AddLoggingBehavior();
            cfg.AddCachingBehavior();
            cfg.AddTransactionBehavior();
        });

        services.AddTransient<IValidator<BrowseCatalogQuery>, BrowseCatalogQueryValidator>();
        services.AddTransient<IValidator<GetProductBySkuQuery>, GetProductBySkuQueryValidator>();
        services.AddTransient<IValidator<ReserveInventoryCommand>, ReserveInventoryCommandValidator>();

        return services;
    }
}
