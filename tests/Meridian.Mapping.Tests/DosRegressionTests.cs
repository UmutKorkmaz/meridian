using System.Diagnostics;
using Meridian.Mapping;

namespace Meridian.Mapping.Tests;

/// <summary>
/// Regression tests that simulate attacker-crafted pathological inputs and
/// verify Meridian rejects them promptly without exhausting memory or the
/// call stack. These tests are the practical demonstration that the
/// CVE class described in GHSA-rvv3-g6hj-g44x (AutoMapper's
/// CVE-2026-32933) is not reachable in Meridian with default configuration.
/// </summary>
/// <remarks>
/// Tests are kept deterministic and fast. Each assertion has an explicit
/// time budget — a test that does not complete within the budget is a
/// regression signal even if it eventually produces the expected result.
/// </remarks>
public class DosRegressionTests
{
    // ── Deep recursion ────────────────────────────────────────────────────

    public class TreeNode
    {
        public int Depth { get; set; }
        public TreeNode? Child { get; set; }
    }

    public class TreeNodeDto
    {
        public int Depth { get; set; }
        public TreeNodeDto? Child { get; set; }
    }

    private static TreeNode BuildDepthChain(int depth)
    {
        var head = new TreeNode { Depth = 0 };
        var current = head;
        for (var i = 1; i < depth; i++)
        {
            current.Child = new TreeNode { Depth = i };
            current = current.Child;
        }
        return head;
    }

    [Fact]
    public void Attacker_Crafted_100k_Deep_Graph_Does_Not_StackOverflow_Under_Default_Config()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<TreeNode, TreeNodeDto>());
        var mapper = cfg.CreateMapper();
        var source = BuildDepthChain(100_000);

        // With DefaultMaxDepth=64, mapping must complete within 1 s and
        // produce a depth-capped result — the interpreter/fast-path never
        // descends past the cap.
        var sw = Stopwatch.StartNew();
        var result = mapper.Map<TreeNode, TreeNodeDto>(source);
        sw.Stop();

        Assert.NotNull(result);
        Assert.True(sw.ElapsedMilliseconds < 1_000,
            $"Mapping a 100k-deep graph took {sw.ElapsedMilliseconds}ms — expected <1s because of MaxDepth=64 cap.");

        // Walk the result; the chain must stop at or before depth 64.
        var walker = result;
        var observedDepth = 0;
        while (walker is not null)
        {
            observedDepth++;
            walker = walker.Child;
        }
        Assert.True(observedDepth <= 64,
            $"Observed mapped depth {observedDepth} exceeds DefaultMaxDepth 64.");
    }

    [Fact]
    public void Attacker_Crafted_100k_Deep_Graph_Respects_Tight_Global_Cap()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.DefaultMaxDepth = 8;  // paranoid production setting
            c.CreateMap<TreeNode, TreeNodeDto>();
        });
        var mapper = cfg.CreateMapper();
        var source = BuildDepthChain(100_000);

        var result = mapper.Map<TreeNode, TreeNodeDto>(source);

        Assert.NotNull(result);
        var walker = result;
        var observedDepth = 0;
        while (walker is not null)
        {
            observedDepth++;
            walker = walker.Child;
        }
        Assert.True(observedDepth <= 8);
    }

    // ── Wide collection ───────────────────────────────────────────────────

    public class Item { public int Value { get; set; } }
    public class ItemDto { public int Value { get; set; } }

    [Fact]
    public void Attacker_Crafted_1M_Item_Collection_Throws_Before_Allocation()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        // An ICollection of this size would be 1 million pointers — still
        // cheap to construct as the SOURCE. What we want to verify is that
        // Meridian refuses to materialise 1M destination items and instead
        // throws MeridianMappingLimitException essentially instantly
        // (ICollection fast-path hit before any enumeration).
        var source = new FakeLargeCollection(1_000_000);

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<FakeLargeCollection, List<ItemDto>>(source));
        sw.Stop();

        Assert.Equal(MeridianMappingLimit.MaxCollectionItems, ex.Limit);
        Assert.Equal(10_000, ex.MaxAllowed);
        Assert.Equal(1_000_000, ex.ObservedValue);
        Assert.True(sw.ElapsedMilliseconds < 250,
            $"Collection-cap check took {sw.ElapsedMilliseconds}ms — expected near-instant rejection via ICollection fast-path.");
    }

    [Fact]
    public void Attacker_Crafted_Streaming_Input_Is_Bounded_During_Enumeration()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        // Pure IEnumerable — no Count — forces mid-enumeration count check.
        // We allow up to maxItems+1 items to be pulled before the exception;
        // the test verifies we do NOT pull the attacker's entire 10M items.
        var pulledCount = 0;
        IEnumerable<Item> Stream()
        {
            for (var i = 0; i < 10_000_000; i++)
            {
                pulledCount++;
                yield return new Item { Value = i };
            }
        }

        Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<IEnumerable<Item>, List<ItemDto>>(Stream()));

        Assert.True(pulledCount <= 10_001,
            $"Pulled {pulledCount} items from the source stream — expected to stop at 10_001 (DefaultMaxCollectionItems + 1).");
    }

    private sealed class FakeLargeCollection : System.Collections.Generic.ICollection<Item>
    {
        public FakeLargeCollection(int size) { Count = size; }
        public int Count { get; }
        public bool IsReadOnly => true;
        public System.Collections.Generic.IEnumerator<Item> GetEnumerator()
        {
            // Intentionally explodes if anyone tries to actually enumerate —
            // proves the fast path cap check happens BEFORE enumeration.
            throw new InvalidOperationException(
                "FakeLargeCollection should not be enumerated — the cap check " +
                "must fire on ICollection.Count before the enumerator is touched.");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
        public void Add(Item item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(Item item) => throw new NotSupportedException();
        public void CopyTo(Item[] array, int arrayIndex) => throw new NotSupportedException();
        public bool Remove(Item item) => throw new NotSupportedException();
    }

    // ── Heavy mapping load (smoke test for no pathological allocation) ────

    public class Wide
    {
        public int A { get; set; } public int B { get; set; } public int C { get; set; }
        public int D { get; set; } public int E { get; set; } public int F { get; set; }
        public int G { get; set; } public int H { get; set; } public int I { get; set; }
        public int J { get; set; }
        public string S1 { get; set; } = "";
        public string S2 { get; set; } = "";
    }

    public class WideDto
    {
        public int A { get; set; } public int B { get; set; } public int C { get; set; }
        public int D { get; set; } public int E { get; set; } public int F { get; set; }
        public int G { get; set; } public int H { get; set; } public int I { get; set; }
        public int J { get; set; }
        public string S1 { get; set; } = "";
        public string S2 { get; set; } = "";
    }

    [Fact]
    public void One_Million_Small_Maps_Complete_Within_Reasonable_Time()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Wide, WideDto>());
        var mapper = cfg.CreateMapper();
        var source = new Wide
        {
            A = 1, B = 2, C = 3, D = 4, E = 5, F = 6, G = 7, H = 8, I = 9, J = 10,
            S1 = "hello", S2 = "world",
        };

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000_000; i++)
        {
            _ = mapper.Map<Wide, WideDto>(source);
        }
        sw.Stop();

        // Fast-path post-P3.6 does this in well under a second on dev hardware.
        // Budget 10 seconds to comfortably clear CI noise; any longer is a regression.
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"1M small maps took {sw.ElapsedMilliseconds}ms — expected <10s on post-P3.6 fast path.");
    }
}
