using System.Collections.Concurrent;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Showcase;

public static class QueryMediatorDemo
{
    public static async Task RunAsync()
    {
        ShowcaseOutput.WriteHeader("Mediator Query");

        GetCatalogItemHandler.Reset();
        CorrelationContext.CorrelationId = null;

        var services = new ServiceCollection();
        services.AddShowcaseMappings();
        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        services.AddSingleton<IMediatorLogger, ConsoleMediatorLogger>();
        services.AddTransient<IValidator<GetCatalogItemQuery>, GetCatalogItemQueryValidator>();

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ShowcaseAssemblyMarker>();
            cfg.AddValidationBehavior();
            cfg.AddLoggingBehavior();
            cfg.AddRetryBehavior();
            cfg.AddCachingBehavior();
            cfg.AddCorrelationIdBehavior();
        });

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        try
        {
            await mediator.Send(new GetCatalogItemQuery("   "));
        }
        catch (ValidationException ex)
        {
            var error = ex.Errors[0];
            Console.WriteLine($"Validation => {error.PropertyName}: {error.ErrorMessage}");
        }

        var first = await mediator.Send(new GetCatalogItemQuery(" widget-42 "));
        var second = await mediator.Send(new GetCatalogItemQuery(" widget-42 "));

        Console.WriteLine($"Query => {first.Sku}:{first.Name}:{first.Price}");
        Console.WriteLine($"Retry/Cache => Handler invoked {GetCatalogItemHandler.InvocationCount} time(s)");
        Console.WriteLine($"Correlation => {GetCatalogItemHandler.LastCorrelationId}");
        Console.WriteLine($"Second query served same payload => {second.Sku}:{second.Name}:{second.Price}");
        Console.WriteLine();
    }
}

public record GetCatalogItemQuery(string Sku) : IRequest<CatalogItemDto>, ICacheableQuery, IRetryableRequest
{
    public string CacheKey => $"catalog:{Sku.Trim().ToUpperInvariant()}";
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(5);
    public int MaxRetries => 1;
    public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(25);
}

public sealed class GetCatalogItemQueryValidator : IValidator<GetCatalogItemQuery>
{
    public Task<ValidationResult> ValidateAsync(GetCatalogItemQuery instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(instance.Sku))
        {
            result.Errors.Add(new ValidationError(nameof(instance.Sku), "SKU is required."));
        }

        return Task.FromResult(result);
    }
}

public sealed class GetCatalogItemHandler : IRequestHandler<GetCatalogItemQuery, CatalogItemDto>
{
    private static readonly ConcurrentDictionary<string, int> AttemptCounts = new();
    private readonly IMapper _mapper;

    public static int InvocationCount { get; private set; }
    public static string? LastCorrelationId { get; private set; }

    public GetCatalogItemHandler(IMapper mapper)
    {
        _mapper = mapper;
    }

    public Task<CatalogItemDto> Handle(GetCatalogItemQuery request, CancellationToken cancellationToken)
    {
        InvocationCount++;
        LastCorrelationId = CorrelationContext.CorrelationId;

        var attempt = AttemptCounts.AddOrUpdate(request.CacheKey, 1, (_, current) => current + 1);
        if (attempt == 1)
        {
            throw new InvalidOperationException("Transient catalog lookup failure.");
        }

        var entity = new CatalogItemEntity
        {
            Sku = request.Sku.Trim().ToUpperInvariant(),
            Name = "  Meridian Widget  ",
            Price = 42.50m
        };

        return Task.FromResult(_mapper.Map<CatalogItemDto>(entity));
    }

    public static void Reset()
    {
        InvocationCount = 0;
        LastCorrelationId = null;
        AttemptCounts.Clear();
    }
}
