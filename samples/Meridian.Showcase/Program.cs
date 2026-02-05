using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Meridian.Mapping;
using Meridian.Mapping.Configuration;
using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Extensions;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Publishing;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Showcase;

public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine("Meridian Showcase");
        Console.WriteLine();

        RunMappingDemo();
        await RunQueryMediatorDemo();
        await RunCommandMediatorDemo();
        await RunNotificationDemo();
        await RunStreamingDemo();
    }

    private static void RunMappingDemo()
    {
        WriteHeader("Mapping");

        var services = new ServiceCollection();
        ConfigureMapping(services);

        using var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapper>();

        var order = new OrderSource
        {
            FirstName = "  Ada  ",
            LastName = "  Lovelace  ",
            Subtotal = 125.50m,
            Shipping = new ShippingSource
            {
                Country = "  UK  ",
                City = "  London  "
            },
            Tags = ["  vip  "]
        };

        var mappedOrder = mapper.Map<OrderView>(order);
        Console.WriteLine(
            $"OrderView => {mappedOrder.CustomerName} | FinalTotal: {mappedOrder.FinalTotal} | Ship: {mappedOrder.Shipping.City}, {mappedOrder.Shipping.Country} | Preserved: {mappedOrder.IgnoredByDefault}");

        var customerCard = mapper.Map<CustomerCard>(new CustomerEnvelope
        {
            Profile = new CustomerProfile
            {
                DisplayName = "  Meridian Team  ",
                Tier = "  Gold  "
            }
        });
        Console.WriteLine($"IncludeMembers => {customerCard.DisplayName} ({customerCard.Tier})");

        var reversed = mapper.Map<ProductEntity>(new ProductDto
        {
            Id = 7,
            Name = "  Meridian Widget  "
        });
        Console.WriteLine($"ReverseMap => {reversed.Id}:{reversed.Name}");

        var projected = mapper.ProjectTo<ProductRow>(
                new[]
                {
                    new ProductEntity { Id = 1, Name = "  Meridian Widget  " },
                    new ProductEntity { Id = 2, Name = "  Meridian Gizmo  " }
                }.AsQueryable())
            .ToList();
        Console.WriteLine($"ProjectTo => {string.Join(", ", projected.Select(row => $"{row.Id}:{row.Name}"))}");
        Console.WriteLine();
    }

    private static async Task RunQueryMediatorDemo()
    {
        WriteHeader("Mediator Query");

        GetCatalogItemHandler.Reset();
        CorrelationContext.CorrelationId = null;

        var services = new ServiceCollection();
        ConfigureMapping(services);
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

    private static async Task RunCommandMediatorDemo()
    {
        WriteHeader("Mediator Command");

        PlaceOrderHandler.Reset();

        var services = new ServiceCollection();
        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.AddSingleton<ITransactionScopeProvider, DemoTransactionScopeProvider>();
        services.AddSingleton<IMediatorLogger, ConsoleMediatorLogger>();
        services.AddTransient<IValidator<PlaceOrderCommand>, PlaceOrderCommandValidator>();
        services.AddTransient<IAuthorizationHandler<PlaceOrderCommand>, PlaceOrderAuthorizationHandler>();

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ShowcaseAssemblyMarker>();
            cfg.AddValidationBehavior();
            cfg.AddLoggingBehavior();
            cfg.AddTransactionBehavior();
            cfg.AddCachingBehavior();
            cfg.AddAuthorizationBehavior();
            cfg.AddIdempotencyBehavior();
        });

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        try
        {
            await mediator.Send(new PlaceOrderCommand("ORD-401", "Ada", "widget-42", 99, "ORD-401"));
        }
        catch (UnauthorizedException ex)
        {
            Console.WriteLine($"Authorization => {ex.Message}");
        }

        var first = await mediator.Send(new PlaceOrderCommand("ORD-1001", "Ada", "widget-42", 2, "ORD-1001"));
        var second = await mediator.Send(new PlaceOrderCommand("ORD-1001", "Ada", "widget-42", 2, "ORD-1001"));

        Console.WriteLine($"Command => {first.OrderId}:{first.Message}");
        Console.WriteLine($"Idempotency => Handler executed {PlaceOrderHandler.ExecutionCount} time(s)");
        Console.WriteLine($"Cached response reused => {first.OrderId == second.OrderId}");
        Console.WriteLine();
    }

    private static async Task RunNotificationDemo()
    {
        WriteHeader("Notifications");

        AuditOrderPlacedHandler.Reset();
        FailingOrderPlacedHandler.Reset();

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ShowcaseAssemblyMarker>();
            cfg.NotificationPublisherType = typeof(ResilientTaskWhenAllPublisher);
        });

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        try
        {
            await mediator.Publish(new OrderPlacedNotification("ORD-1001"));
        }
        catch (AggregateException ex)
        {
            Console.WriteLine($"Publisher => {ex.InnerExceptions.Count} handler failure(s)");
        }

        Console.WriteLine($"Audit handler ran => {AuditOrderPlacedHandler.CallCount}");
        Console.WriteLine($"Failing handler ran => {FailingOrderPlacedHandler.CallCount}");
        Console.WriteLine();
    }

    private static async Task RunStreamingDemo()
    {
        WriteHeader("Streaming");

        var services = new ServiceCollection();
        services.AddSingleton<IMediatorLogger, ConsoleMediatorLogger>();

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ShowcaseAssemblyMarker>();
            cfg.AddOpenStreamBehavior(typeof(TracingStreamBehavior<,>));
        });

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var items = new List<string>();
        await foreach (var item in mediator.CreateStream(new OrderUpdateStream("ORD-1001", 3)))
        {
            items.Add(item);
        }

        Console.WriteLine($"Stream => {string.Join(" | ", items)}");
        Console.WriteLine();
    }

    private static void ConfigureMapping(IServiceCollection services)
    {
        services.AddTransient<OrderMarkupResolver>();
        services.AddMeridianMapping(cfg =>
        {
            cfg.AddProfiles(typeof(Program).Assembly);
            cfg.ValueTransformers.Add<string>(value => value.Trim());
        });
    }

    private static void WriteHeader(string title)
    {
        Console.WriteLine($"== {title} ==");
    }
}

public sealed class ShowcaseMappingProfile : Profile
{
    public ShowcaseMappingProfile()
    {
        CreateMap<OrderSource, OrderView>()
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => $"{src.FirstName} {src.LastName}"))
            .ForMember(dest => dest.FinalTotal, opt => opt.MapFrom<OrderMarkupResolver, decimal>(src => src.Subtotal))
            .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => src.Tags))
            .ForPath(dest => dest.Shipping.Country, opt => opt.MapFrom(src => src.Shipping.Country))
            .ForPath(dest => dest.Shipping.City, opt => opt.MapFrom(src => src.Shipping.City))
            .ForAllOtherMembers(opt => opt.Ignore());

        CreateMap<CustomerEnvelope, CustomerCard>()
            .IncludeMembers(src => src.Profile);
        CreateMap<CustomerProfile, CustomerCard>();

        CreateMap<ProductEntity, ProductDto>().ReverseMap();
        CreateMap<ProductEntity, ProductRow>()
            .ValidateMemberList(MemberList.Source);

        CreateMap<CatalogItemEntity, CatalogItemDto>();
    }
}

public sealed class ShowcaseAssemblyMarker;

public sealed class OrderMarkupResolver : IMemberValueResolver<OrderSource, OrderView, decimal, decimal>
{
    public decimal Resolve(
        OrderSource source,
        OrderView destination,
        decimal sourceMember,
        decimal destMember,
        ResolutionContext context)
    {
        return decimal.Round(sourceMember * 1.20m, 2);
    }
}

public sealed class ConsoleMediatorLogger : IMediatorLogger
{
    public void LogInformation(string message, params object[] args)
        => Console.WriteLine($"[info] {message} | {string.Join(", ", args.Select(FormatArg))}");

    public void LogWarning(string message, params object[] args)
        => Console.WriteLine($"[warn] {message} | {string.Join(", ", args.Select(FormatArg))}");

    public void LogError(Exception exception, string message, params object[] args)
        => Console.WriteLine($"[error] {message} | {string.Join(", ", args.Select(FormatArg))} | {exception.Message}");

    private static string FormatArg(object? value) => value?.ToString() ?? "<null>";
}

public sealed class InMemoryCacheProvider : ICacheProvider
{
    private readonly ConcurrentDictionary<string, object> _cache = new();

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        var found = _cache.TryGetValue(key, out var value);
        return Task.FromResult((found, value));
    }

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        _cache[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _cache.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, object> _responses = new();

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct)
    {
        var exists = _responses.TryGetValue(key, out var cached);
        return Task.FromResult((exists, cached));
    }

    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _responses[key] = response;
        return Task.CompletedTask;
    }
}

public sealed class DemoTransactionScopeProvider : ITransactionScopeProvider
{
    public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[transaction] begin");
        return Task.FromResult<ITransactionScope>(new DemoTransactionScope());
    }
}

public sealed class DemoTransactionScope : ITransactionScope
{
    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[transaction] commit");
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[transaction] rollback");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class TracingStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IMediatorLogger _logger;

    public TracingStreamBehavior(IMediatorLogger logger)
    {
        _logger = logger;
    }

    public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        return TraceAsync(request, next(), cancellationToken);
    }

    private async IAsyncEnumerable<TResponse> TraceAsync(
        TRequest request,
        IAsyncEnumerable<TResponse> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Streaming {RequestName}", typeof(TRequest).Name);

        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            _logger.LogInformation("Stream item from {RequestName}", typeof(TRequest).Name);
            yield return item;
        }
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

public record PlaceOrderCommand(
    string OrderId,
    string CustomerName,
    string Sku,
    int Quantity,
    string IdempotencyKey)
    : IRequest<OrderReceipt>, ITransactionalRequest, IAuthorizedRequest, IIdempotentRequest, ICacheInvalidatingRequest
{
    public string[] CacheKeysToInvalidate => [$"catalog:{Sku.Trim().ToUpperInvariant()}"];
}

public sealed class PlaceOrderCommandValidator : IValidator<PlaceOrderCommand>
{
    public Task<ValidationResult> ValidateAsync(PlaceOrderCommand instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(instance.CustomerName))
        {
            result.Errors.Add(new ValidationError(nameof(instance.CustomerName), "Customer name is required."));
        }

        if (instance.Quantity <= 0)
        {
            result.Errors.Add(new ValidationError(nameof(instance.Quantity), "Quantity must be greater than zero."));
        }

        return Task.FromResult(result);
    }
}

public sealed class PlaceOrderAuthorizationHandler : IAuthorizationHandler<PlaceOrderCommand>
{
    public Task<AuthorizationResult> AuthorizeAsync(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            request.Quantity > 10
                ? AuthorizationResult.Fail("Demo authorization only allows quantities up to 10.")
                : AuthorizationResult.Success());
    }
}

public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrderCommand, OrderReceipt>
{
    public static int ExecutionCount { get; private set; }

    public Task<OrderReceipt> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return Task.FromResult(new OrderReceipt(request.OrderId, $"Created order for {request.CustomerName} x{request.Quantity}."));
    }

    public static void Reset()
    {
        ExecutionCount = 0;
    }
}

public record OrderPlacedNotification(string OrderId) : INotification;

public sealed class AuditOrderPlacedHandler : INotificationHandler<OrderPlacedNotification>
{
    public static int CallCount { get; private set; }

    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        CallCount++;
        Console.WriteLine($"[notification] audited {notification.OrderId}");
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        CallCount = 0;
    }
}

public sealed class FailingOrderPlacedHandler : INotificationHandler<OrderPlacedNotification>
{
    public static int CallCount { get; private set; }

    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("Simulated downstream notification failure.");
    }

    public static void Reset()
    {
        CallCount = 0;
    }
}

public record OrderUpdateStream(string OrderId, int Count) : IStreamRequest<string>;

public sealed class OrderUpdateStreamHandler : IStreamRequestHandler<OrderUpdateStream, string>
{
    public async IAsyncEnumerable<string> Handle(
        OrderUpdateStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 1; index <= request.Count; index++)
        {
            yield return $"{request.OrderId}:update-{index}";
            await Task.Delay(10, cancellationToken);
        }
    }
}

public sealed class OrderSource
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public decimal Subtotal { get; init; }
    public ShippingSource Shipping { get; init; } = new();
    public string[]? Tags { get; init; }
}

public sealed class ShippingSource
{
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
}

public sealed class OrderView
{
    public string CustomerName { get; set; } = string.Empty;
    public decimal FinalTotal { get; set; }
    public ShippingView Shipping { get; set; } = new();
    public string[]? Tags { get; set; }
    public string IgnoredByDefault { get; set; } = "left-alone-by-ForAllOtherMembers";
}

public sealed class ShippingView
{
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public sealed class CustomerEnvelope
{
    public CustomerProfile Profile { get; init; } = new();
}

public sealed class CustomerProfile
{
    public string DisplayName { get; init; } = string.Empty;
    public string Tier { get; init; } = string.Empty;
}

public sealed class CustomerCard
{
    public string DisplayName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
}

public sealed class ProductEntity
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class ProductRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class CatalogItemEntity
{
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
}

public sealed class CatalogItemDto
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public sealed class OrderReceipt
{
    public OrderReceipt(string orderId, string message)
    {
        OrderId = orderId;
        Message = message;
    }

    public string OrderId { get; }
    public string Message { get; }
}
