using Meridian.Mapping;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Meridian.QuickStart;

/// <summary>
/// Runnable end-to-end demo of every public Meridian feature:
///
///  - Standard mediator pipeline (correlation + audit + localised validation)
///  - Mapper with the runtime fast path (compiled at config time)
///  - Source-generated mapper (zero reflection, AOT-safe)
///  - Turkish-locale string helpers (the only mapper that ships these)
///
/// Run with: <c>dotnet run --project samples/Meridian.QuickStart</c>
/// Exit code 0 = everything wired up correctly. Exit code 1 = a sample
/// step failed; the printed message identifies the offending feature.
/// This program is in CI as a smoke test that the public API is still
/// usable from a fresh consumer project.
/// </summary>
public static class Program
{
    public static async Task<int> Main()
    {
        List<string> failures = [];

        try
        {
            await DemoMediatorPipeline();
            Log("✓ Mediator pipeline (correlation + audit + localised validation)");
        }
        catch (Exception ex)
        {
            failures.Add($"Mediator pipeline: {ex.Message}");
        }

        try
        {
            DemoRuntimeMapper();
            Log("✓ Runtime mapper (config-time compiled fast path)");
        }
        catch (Exception ex)
        {
            failures.Add($"Runtime mapper: {ex.Message}");
        }

        try
        {
            DemoSourceGeneratedMapper();
            Log("✓ Source-generated mapper ([GenerateMapper] attribute)");
        }
        catch (Exception ex)
        {
            failures.Add($"Source-generated mapper: {ex.Message}");
        }

        try
        {
            DemoTurkishCulture();
            Log("✓ Turkish culture helpers (dotted/dotless I)");
        }
        catch (Exception ex)
        {
            failures.Add($"Turkish culture: {ex.Message}");
        }

        try
        {
            DemoSafetyDefaults();
            Log("✓ Safety defaults (depth cap returns truncated, collection cap throws)");
        }
        catch (Exception ex)
        {
            failures.Add($"Safety defaults: {ex.Message}");
        }

        if (failures.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("All Meridian QuickStart demos completed successfully.");
            return 0;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Failures:");
        foreach (string f in failures)
        {
            Console.Error.WriteLine($"  ✗ {f}");
        }
        return 1;
    }

    // ── Mediator pipeline demo ───────────────────────────────────────────

    private static async Task DemoMediatorPipeline()
    {
        ServiceCollection services = new();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(EchoLocalizer<>));

        services.AddMeridianStandard(typeof(Program).Assembly);

        ServiceProvider provider = services.BuildServiceProvider();
        IMediator mediator = provider.GetRequiredService<IMediator>();

        // Establish a caller-side correlation id so it flows through the audit log.
        CorrelationContext.CorrelationId = "quickstart-correlation";

        CreateOrderResponse response = await mediator.Send(new CreateOrder("ORD-001", 99.95m));
        Require(response.Accepted, "expected response.Accepted == true");
        Require(response.OrderNumber == "ORD-001", "expected echo of OrderNumber");

        // Validation failure path — ensures localised validation runs.
        try
        {
            await mediator.Send(new CreateOrder("", 0m));
            throw new InvalidOperationException("expected ValidationException for empty order");
        }
        catch (ValidationException ex)
        {
            Require(ex.Errors.Count == 2, $"expected 2 validation errors, got {ex.Errors.Count}");
        }
    }

    public sealed record CreateOrder(string OrderNumber, decimal Total) : IRequest<CreateOrderResponse>;
    public sealed record CreateOrderResponse(bool Accepted, string OrderNumber);

    public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, CreateOrderResponse>
    {
        public Task<CreateOrderResponse> Handle(CreateOrder request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CreateOrderResponse(true, request.OrderNumber));
        }
    }

    public sealed class CreateOrderValidator : IValidator<CreateOrder>
    {
        public Task<ValidationResult> ValidateAsync(CreateOrder instance, CancellationToken cancellationToken)
        {
            ValidationResult result = new();
            if (string.IsNullOrEmpty(instance.OrderNumber))
            {
                result.Errors.Add(new ValidationError(nameof(instance.OrderNumber), "Order.Number.Required"));
            }

            if (instance.Total <= 0m)
            {
                result.Errors.Add(new ValidationError(nameof(instance.Total), "Order.Total.MustBePositive"));
            }

            return Task.FromResult(result);
        }
    }

    private sealed class EchoLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public LocalizedString this[string name, params object[] args] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        {
            return [];
        }
    }

    // ── Runtime mapper demo ──────────────────────────────────────────────

    private static void DemoRuntimeMapper()
    {
        MapperConfiguration config = new(c =>
        {
            c.CreateMap<CustomerEntity, CustomerDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.FirstName + " " + s.LastName));
        });
        IMapper mapper = config.CreateMapper();

        CustomerEntity entity = new() { Id = 7, FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com" };
        CustomerDto dto = mapper.Map<CustomerEntity, CustomerDto>(entity);

        Require(dto.Id == 7, "Id round-trip");
        Require(dto.Email == "ada@example.com", "Email round-trip");
        Require(dto.DisplayName == "Ada Lovelace", "MapFrom expression");
    }

    public class CustomerEntity
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public class CustomerDto
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
    }

    // ── Source-generated mapper demo ─────────────────────────────────────

    private static void DemoSourceGeneratedMapper()
    {
        ProductEntity src = new() { Sku = "SKU-1", Price = 19.99m, InStock = true };
        ProductDto dto = ProductMappers.MapToProductDto(src);

        Require(dto.Sku == "SKU-1", "source-gen Sku");
        Require(dto.Price == 19.99m, "source-gen Price");
        Require(dto.InStock, "source-gen InStock");
    }

    public class ProductEntity
    {
        public string Sku { get; set; } = "";
        public decimal Price { get; set; }
        public bool InStock { get; set; }
    }

    public class ProductDto
    {
        public string Sku { get; set; } = "";
        public decimal Price { get; set; }
        public bool InStock { get; set; }
    }

    // ── Turkish culture demo ─────────────────────────────────────────────

    private static void DemoTurkishCulture()
    {
        // The classic dotted/dotless I round-trip — naive invariant
        // case folding produces wrong answers.
        Require(TurkishCulture.ToUpper("istanbul") == "İSTANBUL",
            "tr-TR uppercase of 'istanbul' must produce 'İSTANBUL', not 'ISTANBUL'");
        Require(TurkishCulture.ToLower("ISTANBUL") == "ıstanbul",
            "tr-TR lowercase of 'ISTANBUL' must produce 'ıstanbul', not 'istanbul'");
        Require(TurkishCulture.IgnoreCaseComparer.Equals("İzmir", "izmir"),
            "tr-TR case-insensitive comparer must match 'İzmir' and 'izmir'");
    }

    // ── Safety defaults demo ─────────────────────────────────────────────

    public class Node { public int V { get; set; } public Node? C { get; set; } }
    public class NodeDto { public int V { get; set; } public NodeDto? C { get; set; } }
    public class Item { public int X { get; set; } }
    public class ItemDto { public int X { get; set; } }

    private static void DemoSafetyDefaults()
    {
        MapperConfiguration cfg = new(c =>
        {
            c.CreateMap<Node, NodeDto>();
            c.CreateMap<Item, ItemDto>();
        });
        IMapper mapper = cfg.CreateMapper();

        // Build a 1000-deep chain — well past the default 64-depth cap.
        Node head = new() { V = 0 };
        Node cur = head;
        for (int i = 1; i < 1_000; i++)
        {
            cur.C = new Node { V = i };
            cur = cur.C;
        }

        NodeDto dto = mapper.Map<Node, NodeDto>(head);
        int depth = 0;
        NodeDto? walker = dto;
        while (walker is not null)
        {
            depth++;
            walker = walker.C;
        }
        Require(depth <= 64, $"depth cap should truncate to ≤64, observed {depth}");

        // 11k-item collection — past the default 10_000 collection cap.
        List<Item> oversized = [.. Enumerable.Range(0, 11_000).Select(i => new Item { X = i })];
        try
        {
            _ = mapper.Map<List<Item>, List<ItemDto>>(oversized);
            throw new InvalidOperationException("expected MeridianMappingLimitException");
        }
        catch (MeridianMappingLimitException) { /* expected */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
    }
}

// Source generator targets — declared at file scope so the generator
// can pick them up. The container class is partial; the generator emits
// the MapToProductDto method into ProductMappers.Meridian.g.cs.
[GenerateMapper(typeof(Program.ProductEntity), typeof(Program.ProductDto))]
public static partial class ProductMappers
{
}
