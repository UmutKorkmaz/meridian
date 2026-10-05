// Canonical documentation examples. Each "#region <name>" below is shown verbatim (after
// dedenting) by a guide or package README fence marked "<!-- snippet: <name> -->"; the
// assertions after each region verify returned values and expected failure paths.
using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
MappingMigration();
MappingLimits();
GeneratedMappings();
GeneratedOptions();
GeneratedPrecedence();
await MediatorMigration();
await MediatorBehaviors();
await StandardPipeline();
await StandardBehaviors();
await MediatorMetricsExample();
Console.WriteLine("COMMON documentation snippets=executed");

static void MappingMigration()
{
    #region mapping-registration
    var services = new ServiceCollection();
    services.AddMeridianMapping(cfg =>
    {
        cfg.AddProfile<CatalogProfile>();
        // Defaults: DefaultMaxDepth = 64, DefaultMaxCollectionItems = 10_000
    });
    #endregion

    #region mapping-alias
    var aliasServices = new ServiceCollection();
    aliasServices.AddAutoMapper(cfg => cfg.AddProfile<CatalogProfile>()); // alias of AddMeridianMapping
    #endregion

    using var provider = services.BuildServiceProvider();
    using var aliasProvider = aliasServices.BuildServiceProvider();
    var product = new Product { Id = 7, Name = "Desk" };
    var existingDto = new ProductDto { Id = 1, DisplayName = "stale" };

    #region mapping-usage
    var mapper = provider.GetRequiredService<IMapper>();
    var dto = mapper.Map<ProductDto>(product);
    mapper.Map(product, existingDto);
    #endregion

    var before = existingDto;
    Check(dto.Id == 7 && dto.DisplayName == "Desk", "profile ForMember rename on create-new");
    Check(ReferenceEquals(before, existingDto) && existingDto.Id == 7 && existingDto.DisplayName == "Desk", "map onto existing keeps identity and updates values");
    var aliasDto = aliasProvider.GetRequiredService<IMapper>().Map<ProductDto>(product);
    Check(aliasDto.Id == 7 && aliasDto.DisplayName == "Desk", "AddAutoMapper alias registers the same profile");
    var configuration = provider.GetRequiredService<IConfigurationProvider>();
    configuration.AssertConfigurationIsValid();
    Check(configuration.DefaultMaxDepth == 64 && configuration.DefaultMaxCollectionItems == 10000
        && !configuration.AllowNullCollections && configuration.AllowNullDestinationValues, "runtime mapping defaults");
    Console.WriteLine("COMMON mapping profile=Desk existing=same-instance alias=Desk defaults=64/10000/false/true");
}

static void MappingLimits()
{
    var order = new Order { Lines = [new() { Sku = "a" }, new() { Sku = "b" }, new() { Sku = "c" }] };

    #region mapping-limits
    var limited = new MapperConfiguration(cfg =>
    {
        cfg.DefaultMaxCollectionItems = 2; // demo only: a tiny cap to show the failure; real configurations raise, never lower, it
        cfg.CreateMap<Order, OrderDto>();
        cfg.CreateMap<OrderLine, OrderLineDto>();
    });
    try
    {
        limited.CreateMapper().Map<OrderDto>(order);
    }
    catch (MeridianMappingLimitException ex)
    {
        Console.WriteLine($"{ex.Limit}: allowed {ex.MaxAllowed}, observed {ex.ObservedValue}");
    }
    #endregion

    var observed = Throws<MeridianMappingLimitException>(() => limited.CreateMapper().Map<OrderDto>(order));
    Check(observed.Limit == MeridianMappingLimit.MaxCollectionItems && observed.MaxAllowed == 2 && observed.ObservedValue > 2, "collection cap failure");
    var withinCap = limited.CreateMapper().Map<OrderDto>(new Order { Lines = [new() { Sku = "a" }, new() { Sku = "b" }] });
    Check(withinCap.Lines.Count == 2 && withinCap.Lines[1].Sku == "b", "collection at cap maps");
    Console.WriteLine("COMMON mapping collectionCap=2 overflow=MeridianMappingLimitException atCap=2");
}

static void GeneratedMappings()
{
    var customer = new CustomerSource { Id = 42, FullName = "Ada Lovelace", Email = "ada@example.test" };

    #region generated-legacy-call
    var customerDto = CustomerMappers.MapToCustomerDto(customer);
    #endregion

    Check(customerDto.Id == 42 && customerDto.FullName == "Ada Lovelace" && customerDto.Email == "ada@example.test", "legacy generated copy");
    Check(CustomerMappers.MapToCustomerDto(null!) is null, "legacy generated null source returns default");

    #region generated-checked-conversions
    var widened = MeasurementMaps.MapToMeasurementDto(new Measurement { Ratio = double.MaxValue, Count = 3 });
    // widened.Ratio is float.PositiveInfinity: checked floating-point casts do not throw.
    try
    {
        MeasurementMaps.MapToMeasurementDto(new Measurement { Ratio = 1, Count = int.MaxValue + 1L });
    }
    catch (OverflowException)
    {
        // Integral narrowing outside the destination range throws.
    }
    #endregion

    Check(float.IsPositiveInfinity(widened.Ratio) && widened.Count == 3, "checked double->float yields Infinity");
    Throws<OverflowException>(() => MeasurementMaps.MapToMeasurementDto(new Measurement { Ratio = 1, Count = int.MaxValue + 1L }));
    Check(MeasurementMaps.MapToMeasurementDto(new Measurement { Ratio = 0.5, Count = 9 }) is { Ratio: 0.5f, Count: 9 }, "checked in-range conversion");
    Console.WriteLine("COMMON generated legacy=42 null=default doubleToFloat=Infinity longToInt=OverflowException");
}

static void GeneratedOptions()
{
    #region generated-options
    var chain = new Chain { Value = 1, Next = new Chain { Value = 2, Next = new Chain { Value = 3 } } };
    var byAttribute = ChainMaps.MapToChainDto(chain);                                             // attribute MaxDepth = 2
    var raised = ChainMaps.MapToChainDto(chain, new GeneratedMappingOptions { MaxDepth = 3 });    // option replaces the limit
    var lowered = ChainMaps.MapToChainDto(chain, new GeneratedMappingOptions { MaxDepth = 1 });
    #endregion

    Check(byAttribute.Next is { Value: 2, Next: null }, "attribute depth limit applies without options");
    Check(raised.Next is { Value: 2, Next: { Value: 3 } }, "direct option raises the attribute depth limit for one call");
    Check(lowered is { Value: 1, Next: null }, "direct option lowers the attribute depth limit for one call");
    Throws<ArgumentOutOfRangeException>(() => ChainMaps.MapToChainDto(chain, new GeneratedMappingOptions { MaxDepth = 0 }));
    Console.WriteLine("COMMON generated options=replace-attribute-limits depth=2/3/1 nonPositive=ArgumentOutOfRangeException");
}

static void GeneratedPrecedence()
{
    #region generated-precedence
    var configuration = new MapperConfiguration(cfg =>
    {
        cfg.UseGeneratedMappings(new BillingMaps.GeneratedResolver());
        // An explicit runtime map always wins over a generated descriptor for the same pair.
        cfg.CreateMap<Invoice, InvoiceDto>()
            .ForMember(d => d.Label, o => o.MapFrom(s => "profile:" + s.Number));
    });
    configuration.AssertConfigurationIsValid();
    var mapper = configuration.CreateMapper();
    var invoice = mapper.Map<InvoiceDto>(new Invoice { Number = "INV-1" });   // runtime profile
    var payment = mapper.Map<PaymentDto>(new Payment { Id = 9, Amount = 12m }); // generated resolver
    #endregion

    Check(invoice.Label == "profile:INV-1", "explicit runtime map takes precedence");
    Check(payment.Reference == 9 && payment.Amount == 12m, "unconfigured create-new pair uses the generated resolver");
    var direct = BillingMaps.MapToInvoiceDto(new Invoice { Number = "INV-2" });
    Check(direct.Label == "INV-2", "direct generated call keeps the generated rule");
    var existing = new PaymentDto { Reference = 1 };
    var existingFailure = Throws<InvalidOperationException>(() => mapper.Map(new Payment { Id = 3, Amount = 1m }, existing));
    Check(existing.Reference == 1 && existingFailure.Message.StartsWith("Missing mapping configuration for Payment -> PaymentDto", StringComparison.Ordinal), "map-to-existing never uses the generated descriptor");
    Console.WriteLine("COMMON generated precedence=profile resolver=create-new direct=generated existing=runtime-only");
}

static async Task MediatorMigration()
{
    #region mediator-registration
    var services = new ServiceCollection();
    services.AddMeridianMediator(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<GetPriceHandler>();
    });
    #endregion
    services.AddScoped<UnitOfWork>(); // scoped dependency used by the example handlers

    await using var provider = services.BuildServiceProvider();
    UnitOfWork firstUnit;
    await using (var scope = provider.CreateAsyncScope())
    {
        #region mediator-abstractions
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        var streams = scope.ServiceProvider.GetRequiredService<IStreamSender>();
        #endregion

        #region mediator-dispatch
        decimal price = await sender.Send(new GetPrice("desk"));
        await sender.Send(new ArchiveProduct("desk"));
        await publisher.Publish(new ProductArchived("desk"));
        var skus = new List<string>();
        await foreach (var sku in streams.CreateStream(new ListSkus(3)))
            skus.Add(sku);
        #endregion

        firstUnit = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
        Check(price == 129.5m, "typed request result");
        Check(firstUnit.Archived.SequenceEqual(new[] { "desk" }), "void command handler used the scoped dependency");
        Check(firstUnit.Notifications.Order(StringComparer.Ordinal).SequenceEqual(new[] { "email:desk", "search:desk" }), "both notification handlers ran");
        Check(skus.SequenceEqual(new[] { "sku-1", "sku-2", "sku-3" }), "stream sequence");
        using var canceled = new CancellationTokenSource();
        var received = 0;
        var cancellation = await ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in streams.CreateStream(new ListSkus(10), canceled.Token))
            {
                if (++received == 2) canceled.Cancel();
            }
        });
        Check(received == 2 && cancellation.CancellationToken == canceled.Token, "stream observes caller cancellation");
        var missing = await ThrowsAsync<InvalidOperationException>(() => sender.Send(new Unhandled()));
        Check(missing.Message.Contains(nameof(Unhandled), StringComparison.Ordinal), "missing handler fails with the request type");
        Check(publisher is not null && streams is not null, "focused abstractions resolve");
    }
    Check(firstUnit.Disposed, "scoped handler dependency disposed with its scope");
    await using (var second = provider.CreateAsyncScope())
    {
        Check(!ReferenceEquals(firstUnit, second.ServiceProvider.GetRequiredService<UnitOfWork>()), "scopes isolate scoped dependencies");
    }

    Console.WriteLine("COMMON mediator price=129.5 void=1 notifications=2 stream=3 cancel=token missing=InvalidOperationException scoped=disposed");
}

static async Task MediatorBehaviors()
{
    var services = new ServiceCollection();
    services.AddScoped<UnitOfWork>();
    #region mediator-open-behavior
    services.AddTransient<IValidator<CreateProduct>, CreateProductValidator>();
    services.AddMeridianMediator(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<GetPriceHandler>();
        cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        cfg.AddBehavior<CreateProductTiming>(order: 10);
    });
    #endregion

    await using var provider = services.BuildServiceProvider();
    await using (var scope = provider.CreateAsyncScope())
    {
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var unit = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
        Check(await sender.Send(new CreateProduct("lamp")) == "created:lamp", "valid request passes open and closed behaviors");
        var invalid = await ThrowsAsync<ValidationException>(() => sender.Send(new CreateProduct("")));
        Check(invalid.Errors.Single() == new ValidationError("Name", "Name is required."), "validation errors are returned");
        Check(unit.Created.SequenceEqual(new[] { "lamp" }) && unit.Timings == 1, "invalid request never reaches the closed behavior or handler");
    }
    Console.WriteLine("COMMON mediator validation=rejected openBehavior=ValidationBehavior closedBehavior=once");
}

static async Task StandardPipeline()
{
    #region standard-pipeline
    var services = new ServiceCollection();
    services.AddSingleton<IAuditSink, RecordingAuditSink>();             // replace the logger sink
    services.AddSingleton(typeof(IStringLocalizer<>), typeof(MessageCatalog<>));
    services.AddMeridianStandard(typeof(RenameCatalog).Assembly);       // correlation -> audit -> localized validation
    #endregion

    await using var provider = services.BuildServiceProvider();
    var audit = (RecordingAuditSink)provider.GetRequiredService<IAuditSink>();
    var sender = provider.GetRequiredService<ISender>();
    Check(CorrelationContext.CorrelationId is null, "no ambient correlation before dispatch");
    Check(await sender.Send(new RenameCatalog("Spring")) == "renamed:Spring", "valid request returns handler value");
    var rejected = await ThrowsAsync<ValidationException>(() => sender.Send(new RenameCatalog("")));
    Check(rejected.Errors.Single().ErrorMessage == "Catalog name is required.", "validator message is localized");
    Check(RenameCatalogHandler.Calls == 1, "rejected request never reaches the handler");
    Check(audit.Entries.Count == 2 && audit.Entries[0].Success && !audit.Entries[1].Success, "audit records each request once, including validation failure");
    Check(audit.Entries.All(e => !string.IsNullOrEmpty(e.CorrelationId)) && audit.Entries[0].CorrelationId != audit.Entries[1].CorrelationId, "correlation established per request before audit");
    Check(audit.Entries[1].FailureMessage is not { } message || !message.Contains("Catalog name", StringComparison.Ordinal), "privacy default keeps failure detail out of audit records");
    Check(CorrelationContext.CorrelationId is null, "ambient correlation restored after dispatch");
    Console.WriteLine("COMMON standard valid=renamed rejected=localized handler=1 audit=2 correlation=per-request privacy=redacted");
}

static async Task StandardBehaviors()
{
    #region behavior-composition
    var services = new ServiceCollection();
    services.AddSingleton<ICacheProvider, DictionaryCache>();
    services.AddSingleton<IIdempotencyStore, DictionaryIdempotencyStore>();
    services.AddSingleton<ITransactionScopeProvider, RecordingTransactions>();
    services.AddSingleton<IAuthorizationHandler<ShipOrder>, ShipOrderPolicy>();
    services.AddMeridianMediator(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<ShipOrderHandler>();
        cfg.AddAuthorizationBehavior(order: -300);
        cfg.AddIdempotencyBehavior(order: -200);
        cfg.AddCachingBehavior(order: -100);
        cfg.AddRetryBehavior(order: 0);
        cfg.AddTransactionBehavior(order: 100);
    });
    #endregion

    await using var provider = services.BuildServiceProvider();
    var sender = provider.GetRequiredService<ISender>();
    var transactions = (RecordingTransactions)provider.GetRequiredService<ITransactionScopeProvider>();
    var cache = (DictionaryCache)provider.GetRequiredService<ICacheProvider>();

    // Authorization: a registered policy decides; denial never reaches the handler.
    Check(await sender.Send(new ShipOrder("o-1", "warehouse")) == "shipped:o-1", "authorized command");
    var denied = await ThrowsAsync<UnauthorizedException>(() => sender.Send(new ShipOrder("o-2", "guest")));
    Check(denied is not MissingAuthorizationHandlerException && ShipOrderHandler.Shipped.SequenceEqual(new[] { "o-1" }), "denied command never runs");
    var missingPolicy = await ThrowsAsync<MissingAuthorizationHandlerException>(() => sender.Send(new CancelOrder("o-1")));
    Check(missingPolicy is not null && CancelOrderHandler.Calls == 0, "no policy fails closed by default");

    // Transactions: commit on success, rollback on failure, the original exception survives.
    Check(transactions.Log.SequenceEqual(new[] { "begin", "commit", "dispose" }), "transaction committed once");
    var failure = await ThrowsAsync<InvalidOperationException>(() => sender.Send(new ShipOrder("explode", "warehouse")));
    Check(failure.Message == "warehouse offline" && transactions.Log.Skip(3).SequenceEqual(new[] { "begin", "rollback", "dispose" }), "transaction rolled back and the handler error is preserved");

    // Idempotency: the same key replays the stored response without running the handler again.
    var first = await sender.Send(new PlacePayment("pay-1", 10m));
    var replay = await sender.Send(new PlacePayment("pay-1", 99m));
    Check(first == replay && first == "payment:10" && PlacePaymentHandler.Calls == 1, "idempotent replay");

    // Caching and invalidation use type-composed keys.
    Check(await sender.Send(new GetStock("desk")) == 5 && await sender.Send(new GetStock("desk")) == 5 && GetStockHandler.Calls == 1, "cache hit");
    Check(cache.Keys.Contains(CacheKeyComposer.Compose<GetStock>("stock:desk")), "composed cache key");
    await sender.Send(new RestockItem("desk"));
    Check(await sender.Send(new GetStock("desk")) == 6 && GetStockHandler.Calls == 2, "invalidation forces a fresh read");

    // Retry: transient failures retry with bounded backoff; non-transient ones do not.
    Check(await sender.Send(new SyncInventory(FailuresBeforeSuccess: 2)) == 3, "transient failure retried until success");
    await ThrowsAsync<ArgumentException>(() => sender.Send(new SyncInventory(FailuresBeforeSuccess: -1)));
    Check(SyncInventoryHandler.Attempts == 4, "non-transient ArgumentException is not retried");

    // Pre/post processors and exception handlers run in the pipeline around the handler.
    #region behavior-processors
    var processed = new ServiceCollection();
    processed.AddMeridianMediator(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<QuoteHandler>();
        cfg.AddRequestPreProcessor(typeof(QuotePreProcessor));
        cfg.AddRequestPostProcessor(typeof(QuotePostProcessor));
        // Exception handlers run only when their processor behavior is registered.
        cfg.AddOpenBehavior(typeof(RequestExceptionProcessorBehavior<,>));
    });
    #endregion
    await using var processedProvider = processed.BuildServiceProvider();
    var quoteSender = processedProvider.GetRequiredService<ISender>();
    Check(await quoteSender.Send(new Quote("desk")) == 100m && QuoteTrace.Steps.SequenceEqual(new[] { "pre:desk", "handler:desk", "post:100" }), "pre/post processor order");
    Check(await quoteSender.Send(new Quote("broken")) == -1m && QuoteTrace.Steps.Contains("handled:broken"), "exception handler supplies a fallback response");
    Console.WriteLine("COMMON behaviors authorization=allow/deny/fail-closed transaction=commit/rollback idempotency=replay cache=hit/invalidate retry=transient-only processors=pre/post exceptionHandler=fallback");
}

static async Task MediatorMetricsExample()
{
    var services = new ServiceCollection();
    #region mediator-metrics
    services.AddMeridianMediator(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<Program>();
        cfg.Metrics = new MediatorMetricsOptions { Enabled = true, MaximumRequestTypes = 100 };
    });
    #endregion

    var outcomes = new List<(string Type, string Outcome)>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) =>
    {
        if (instrument.Meter.Name == "Meridian.Mediator" && instrument.Name == "meridian.mediator.request.count") l.EnableMeasurementEvents(instrument);
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
    {
        string? type = null, outcome = null;
        foreach (var tag in tags)
        {
            if (tag.Key == "request.type") type = tag.Value as string;
            if (tag.Key == "outcome") outcome = tag.Value as string;
        }
        lock (outcomes) outcomes.Add((type ?? "", outcome ?? ""));
    });
    listener.Start();
    await using var provider = services.BuildServiceProvider();
    Check(await provider.GetRequiredService<ISender>().Send(new GetPrice("desk")) == 129.5m, "metrics do not change the result");
    lock (outcomes) Check(outcomes.Count == 1 && outcomes[0] == ("GetPrice", "success"), "one bounded success measurement with the request type tag");
    Console.WriteLine("COMMON metrics enabled=opt-in request.count=1 outcome=success request.type=GetPrice");
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Documentation assertion failed: " + name);
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T expected) { return expected; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T expected) { return expected; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

// ---- Mapping types ----
public sealed class Product { public int Id { get; set; } public string Name { get; set; } = ""; }
public sealed class ProductDto { public int Id { get; set; } public string DisplayName { get; set; } = ""; }

#region mapping-profile
public sealed class CatalogProfile : Profile
{
    public CatalogProfile()
    {
        CreateMap<Product, ProductDto>()
            .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name));
    }
}
#endregion

public sealed class Order { public List<OrderLine> Lines { get; set; } = []; }
public sealed class OrderLine { public string Sku { get; set; } = ""; }
public sealed class OrderDto { public List<OrderLineDto> Lines { get; set; } = []; }
public sealed class OrderLineDto { public string Sku { get; set; } = ""; }

#region generated-minimum
public class CustomerSource
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
}

public class CustomerDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
}

[GenerateMapper(typeof(CustomerSource), typeof(CustomerDto))]
public static partial class CustomerMappers
{
}
#endregion

public sealed class Chain { public int Value { get; set; } public Chain? Next { get; set; } }
public sealed class ChainDto { public int Value { get; set; } public ChainDto? Next { get; set; } }

#region generated-chain-mapper
[GenerateMapper(typeof(Chain), typeof(ChainDto), Mode = GeneratedMappingMode.Structural, MaxDepth = 2)]
public static partial class ChainMaps
{
}
#endregion

public sealed class Measurement { public double Ratio { get; set; } public long Count { get; set; } }
public sealed class MeasurementDto { public float Ratio { get; set; } public int Count { get; set; } }

#region generated-structural
[GenerateMapper(typeof(Measurement), typeof(MeasurementDto), Mode = GeneratedMappingMode.Structural)]
[ConvertMapMember("Ratio", GeneratedConversionKind.CheckedNumeric)]
[ConvertMapMember("Count", GeneratedConversionKind.CheckedNumeric)]
public static partial class MeasurementMaps
{
}
#endregion

public sealed class Invoice { public string Number { get; set; } = ""; }
public sealed class InvoiceDto { public string Label { get; set; } = ""; }
public sealed class Payment { public int Id { get; set; } public decimal Amount { get; set; } }
public sealed class PaymentDto { public int Reference { get; set; } public decimal Amount { get; set; } }

#region generated-resolver
[GenerateMapper(typeof(Invoice), typeof(InvoiceDto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true)]
[GenerateMapper(typeof(Payment), typeof(PaymentDto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true)]
[MapMember("Label", "Number")]
[MapMember("Reference", "Id")]
public static partial class BillingMaps
{
}
#endregion

// ---- Mediator types ----
public sealed class UnitOfWork : IDisposable
{
    public List<string> Archived { get; } = [];
    public List<string> Notifications { get; } = [];
    public List<string> Created { get; } = [];
    public int Timings { get; set; }
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

#region mediator-requests
public sealed record GetPrice(string Sku) : IRequest<decimal>;
public sealed record ArchiveProduct(string Sku) : IRequest;
public sealed record ProductArchived(string Sku) : INotification;
public sealed record ListSkus(int Count) : IStreamRequest<string>;

public sealed class GetPriceHandler : IRequestHandler<GetPrice, decimal>
{
    public Task<decimal> Handle(GetPrice request, CancellationToken cancellationToken) => Task.FromResult(129.5m);
}

public sealed class ArchiveProductHandler(UnitOfWork unitOfWork) : IRequestHandler<ArchiveProduct>
{
    public Task Handle(ArchiveProduct request, CancellationToken cancellationToken)
    {
        unitOfWork.Archived.Add(request.Sku);
        return Task.CompletedTask;
    }
}

public sealed class ListSkusHandler : IStreamRequestHandler<ListSkus, string>
{
    public async IAsyncEnumerable<string> Handle(ListSkus request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return $"sku-{i}";
        }
    }
}
#endregion

public sealed class EmailOnArchive(UnitOfWork unitOfWork) : INotificationHandler<ProductArchived>
{
    public Task Handle(ProductArchived notification, CancellationToken cancellationToken)
    {
        unitOfWork.Notifications.Add("email:" + notification.Sku);
        return Task.CompletedTask;
    }
}

public sealed class SearchOnArchive(UnitOfWork unitOfWork) : INotificationHandler<ProductArchived>
{
    public Task Handle(ProductArchived notification, CancellationToken cancellationToken)
    {
        unitOfWork.Notifications.Add("search:" + notification.Sku);
        return Task.CompletedTask;
    }
}

public sealed record Unhandled : IRequest<int>;

public sealed record CreateProduct(string Name) : IRequest<string>;
public sealed class CreateProductHandler(UnitOfWork unitOfWork) : IRequestHandler<CreateProduct, string>
{
    public Task<string> Handle(CreateProduct request, CancellationToken cancellationToken)
    {
        unitOfWork.Created.Add(request.Name);
        return Task.FromResult("created:" + request.Name);
    }
}

public sealed class CreateProductValidator : IValidator<CreateProduct>
{
    public Task<ValidationResult> ValidateAsync(CreateProduct instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(instance.Name)) result.Errors.Add(new ValidationError("Name", "Name is required."));
        return Task.FromResult(result);
    }
}

public sealed class CreateProductTiming(UnitOfWork unitOfWork) : IPipelineBehavior<CreateProduct, string>
{
    public async Task<string> Handle(CreateProduct request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        unitOfWork.Timings++;
        return await next();
    }
}

public sealed record RenameCatalog(string Name) : IRequest<string>;
public sealed class RenameCatalogHandler : IRequestHandler<RenameCatalog, string>
{
    public static int Calls { get; private set; }
    public Task<string> Handle(RenameCatalog request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult("renamed:" + request.Name);
    }
}

public sealed class RenameCatalogValidator : IValidator<RenameCatalog>
{
    public Task<ValidationResult> ValidateAsync(RenameCatalog instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();
        if (instance.Name.Length == 0) result.Errors.Add(new ValidationError("Name", "Catalog.Name.Required"));
        return Task.FromResult(result);
    }
}

public sealed class MessageCatalog<T> : IStringLocalizer<T>
{
    private static readonly Dictionary<string, string> Messages = new() { ["Catalog.Name.Required"] = "Catalog name is required." };
    public LocalizedString this[string name] => Messages.TryGetValue(name, out var value) ? new(name, value) : new(name, name, resourceNotFound: true);
    public LocalizedString this[string name, params object[] arguments] => this[name];
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Messages.Select(m => new LocalizedString(m.Key, m.Value));
}

public sealed class RecordingAuditSink : IAuditSink
{
    public List<AuditEntry> Entries { get; } = [];
    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        lock (Entries) Entries.Add(entry);
        return Task.CompletedTask;
    }
}

public sealed record ShipOrder(string OrderId, string Role) : IRequest<string>, IAuthorizedRequest, ITransactionalRequest;
public sealed class ShipOrderHandler : IRequestHandler<ShipOrder, string>
{
    public static List<string> Shipped { get; } = [];
    public Task<string> Handle(ShipOrder request, CancellationToken cancellationToken)
    {
        if (request.OrderId == "explode") throw new InvalidOperationException("warehouse offline");
        Shipped.Add(request.OrderId);
        return Task.FromResult("shipped:" + request.OrderId);
    }
}

public sealed class ShipOrderPolicy : IAuthorizationHandler<ShipOrder>
{
    public Task<AuthorizationResult> AuthorizeAsync(ShipOrder request, CancellationToken cancellationToken) =>
        Task.FromResult(request.Role == "warehouse" ? AuthorizationResult.Success() : AuthorizationResult.Fail("role"));
}

public sealed record CancelOrder(string OrderId) : IRequest<bool>, IAuthorizedRequest;
public sealed class CancelOrderHandler : IRequestHandler<CancelOrder, bool>
{
    public static int Calls { get; private set; }
    public Task<bool> Handle(CancelOrder request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(true);
    }
}

public sealed class RecordingTransactions : ITransactionScopeProvider
{
    public List<string> Log { get; } = [];
    public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
    {
        Log.Add("begin");
        return Task.FromResult<ITransactionScope>(new Scope(Log));
    }

    private sealed class Scope(List<string> log) : ITransactionScope
    {
        public Task CommitAsync(CancellationToken cancellationToken) { log.Add("commit"); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken) { log.Add("rollback"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { log.Add("dispose"); return ValueTask.CompletedTask; }
    }
}

public sealed record PlacePayment(string Key, decimal Amount) : IRequest<string>, IIdempotentRequest
{
    public string IdempotencyKey => Key;
}

public sealed class PlacePaymentHandler : IRequestHandler<PlacePayment, string>
{
    public static int Calls { get; private set; }
    public Task<string> Handle(PlacePayment request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult("payment:" + request.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

public sealed class DictionaryIdempotencyStore : IIdempotencyStore
{
    private readonly Dictionary<string, object> _responses = [];
    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
        Task.FromResult(_responses.TryGetValue(key, out var value) ? (true, (object?)value) : (false, null));
    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _responses[key] = response;
        return Task.CompletedTask;
    }
}

public sealed record GetStock(string Sku) : IRequest<int>, ICacheableQuery
{
    public string CacheKey => "stock:" + Sku;
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(1);
}

public sealed class GetStockHandler : IRequestHandler<GetStock, int>
{
    public static int Calls { get; private set; }
    public static int Level { get; set; } = 5;
    public Task<int> Handle(GetStock request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Level);
    }
}

public sealed record RestockItem(string Sku) : IRequest, ICacheInvalidatingRequest
{
    // Invalidation names the cached query type so the composed key matches.
    public string[] CacheKeysToInvalidate => [CacheKeyComposer.Compose<GetStock>("stock:" + Sku)];
}

public sealed class RestockItemHandler : IRequestHandler<RestockItem>
{
    public Task Handle(RestockItem request, CancellationToken cancellationToken)
    {
        GetStockHandler.Level++;
        return Task.CompletedTask;
    }
}

public sealed class DictionaryCache : ICacheProvider
{
    private readonly Dictionary<string, object> _values = [];
    public IReadOnlyCollection<string> Keys => _values.Keys;
    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? (true, (object?)value) : (false, null));
    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed record SyncInventory(int FailuresBeforeSuccess) : IRequest<int>, IRetryableRequest
{
    public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(1);
}

public sealed class SyncInventoryHandler : IRequestHandler<SyncInventory, int>
{
    private static int _remaining = -1;
    public static int Attempts { get; private set; }
    public Task<int> Handle(SyncInventory request, CancellationToken cancellationToken)
    {
        Attempts++;
        if (request.FailuresBeforeSuccess < 0) throw new ArgumentException("bad inventory request");
        if (_remaining < 0) _remaining = request.FailuresBeforeSuccess;
        if (_remaining-- > 0) throw new TimeoutException("inventory service busy");
        _remaining = -1;
        return Task.FromResult(Attempts);
    }
}

public static class QuoteTrace { public static List<string> Steps { get; } = []; }
public sealed record Quote(string Sku) : IRequest<decimal>;
public sealed class QuoteHandler : IRequestHandler<Quote, decimal>
{
    public Task<decimal> Handle(Quote request, CancellationToken cancellationToken)
    {
        QuoteTrace.Steps.Add("handler:" + request.Sku);
        if (request.Sku == "broken") throw new InvalidOperationException("pricing unavailable");
        return Task.FromResult(100m);
    }
}

public sealed class QuotePreProcessor : IRequestPreProcessor<Quote>
{
    public Task Process(Quote request, CancellationToken cancellationToken)
    {
        QuoteTrace.Steps.Add("pre:" + request.Sku);
        return Task.CompletedTask;
    }
}

public sealed class QuotePostProcessor : IRequestPostProcessor<Quote, decimal>
{
    public Task Process(Quote request, decimal response, CancellationToken cancellationToken)
    {
        QuoteTrace.Steps.Add("post:" + response.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }
}

public sealed class QuoteFallback : IRequestExceptionHandler<Quote, decimal, InvalidOperationException>
{
    public Task Handle(Quote request, InvalidOperationException exception, RequestExceptionHandlerState<decimal> state, CancellationToken cancellationToken)
    {
        QuoteTrace.Steps.Add("handled:" + request.Sku);
        state.SetHandled(-1m);
        return Task.CompletedTask;
    }
}
