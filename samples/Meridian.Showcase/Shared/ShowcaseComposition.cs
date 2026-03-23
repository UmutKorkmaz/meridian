using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Showcase;

public static class ShowcaseComposition
{
    public static IServiceCollection AddShowcaseMappings(this IServiceCollection services)
    {
        services.AddTransient<OrderMarkupResolver>();
        services.AddMeridianMapping(cfg =>
        {
            cfg.AddProfiles(typeof(ShowcaseMappingProfile).Assembly);
            cfg.ValueTransformers.Add<string>(value => value.Trim());
        });

        return services;
    }
}
