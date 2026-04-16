using Meridian.Mapping;

namespace Meridian.Showcase;

/// <summary>
/// Demos for the safety defaults and culture support introduced in
/// Meridian v1.1. Kept separate from the original feature demos so the
/// older sections remain a stable reference for adopters who haven't
/// migrated yet.
/// </summary>
public static class SafetyAndModernFeaturesDemo
{
    public static Task Run()
    {
        ShowcaseOutput.WriteHeader("Safety defaults + culture (v1.1)");

        DemonstrateMaxDepth();
        DemonstrateMaxCollectionItems();
        DemonstrateTurkishCulture();
        DemonstrateSourceGenerator();

        Console.WriteLine();
        return Task.CompletedTask;
    }

    private static void DemonstrateMaxDepth()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Node, NodeDto>());
        var mapper = cfg.CreateMapper();

        // 10k-deep chain — would StackOverflow without the default 64 cap.
        var head = new Node { Value = 0 };
        var cur = head;
        for (var i = 1; i < 10_000; i++) { cur.Child = new Node { Value = i }; cur = cur.Child; }

        var dto = mapper.Map<Node, NodeDto>(head);
        var depth = 0;
        var walker = dto;
        while (walker is not null) { depth++; walker = walker.Child; }

        Console.WriteLine(
            $"MaxDepth => 10 000-deep input mapped to {depth} levels (capped at DefaultMaxDepth=64) — no StackOverflow.");
    }

    private static void DemonstrateMaxCollectionItems()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        var oversized = Enumerable.Range(0, 50_000).Select(i => new Item { Value = i }).ToList();
        try
        {
            _ = mapper.Map<List<Item>, List<ItemDto>>(oversized);
            Console.WriteLine("MaxCollectionItems => unexpectedly succeeded.");
        }
        catch (MeridianMappingLimitException ex)
        {
            Console.WriteLine(
                $"MaxCollectionItems => 50 000-item input rejected before allocation " +
                $"(observed {ex.ObservedValue}, max {ex.MaxAllowed}).");
        }
    }

    private static void DemonstrateTurkishCulture()
    {
        var lower = TurkishCulture.ToLower("ISTANBUL");
        var upper = TurkishCulture.ToUpper("istanbul");
        var equal = TurkishCulture.IgnoreCaseComparer.Equals("İzmir", "izmir");

        Console.WriteLine(
            $"TurkishCulture => 'ISTANBUL'.ToLower(tr-TR)='{lower}', " +
            $"'istanbul'.ToUpper(tr-TR)='{upper}', İzmir≈izmir={equal}");
    }

    private static void DemonstrateSourceGenerator()
    {
        var src = new ShowcaseSrc { Id = 42, Label = "generated" };
        var dst = ShowcaseGeneratedMappers.MapToShowcaseDst(src);
        Console.WriteLine(
            $"SourceGen => Id={dst.Id}, Label='{dst.Label}' (compiled at build time, zero reflection at runtime).");
    }

    public class Node { public int Value { get; set; } public Node? Child { get; set; } }
    public class NodeDto { public int Value { get; set; } public NodeDto? Child { get; set; } }
    public class Item { public int Value { get; set; } }
    public class ItemDto { public int Value { get; set; } }
    public class ShowcaseSrc { public int Id { get; set; } public string Label { get; set; } = ""; }
    public class ShowcaseDst { public int Id { get; set; } public string Label { get; set; } = ""; }
}

[GenerateMapper(typeof(SafetyAndModernFeaturesDemo.ShowcaseSrc), typeof(SafetyAndModernFeaturesDemo.ShowcaseDst))]
public static partial class ShowcaseGeneratedMappers
{
}
