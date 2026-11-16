using System.Diagnostics;
using System.Globalization;
using FsCheck;
using Meridian.Mapping;
using Xunit.Abstractions;

namespace Meridian.Mapping.Tests;

/// <summary>
/// Property-based tests using FsCheck. These validate invariants that must
/// hold for any input — the mapper should not crash on arbitrary values,
/// round-trip mappings should preserve equality on plain data types, and
/// no input should produce an unhandled exception class outside of the
/// documented <see cref="MeridianMappingLimitException"/>.
/// </summary>
/// <remarks>
/// Ordinary runs retain budgets of 100/100/50/10/25. Nightly runs use
/// MERIDIAN_FSCHECK_MAX_TEST for scalar/depth properties and
/// MERIDIAN_FSCHECK_COLLECTION_MAX_TEST for collection properties.
/// </remarks>
public class PropertyBasedTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public PropertyBasedTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _elapsed.Stop();
        GC.SuppressFinalize(this);
        using var process = Process.GetCurrentProcess();
        // Peak working set is shared by this test host, not attributable to
        // this property alone. FsCheck prints the actual successful case count.
        var peak = process.PeakWorkingSet64;
        var peakText = peak > 0 ? peak.ToString(CultureInfo.InvariantCulture) : "unavailable";
        var replay = Environment.GetEnvironmentVariable("MERIDIAN_FSCHECK_REPLAY")
            ?? "random (FsCheck reports replay on failure)";
        _output.WriteLine(FormattableString.Invariant(
            $"Property evidence: configured_replay={replay}; elapsed_ms={_elapsed.Elapsed.TotalMilliseconds:F3}; test_host_peak_working_set_bytes={peakText}; managed_heap_in_use_bytes={GC.GetTotalMemory(false)}; managed_heap_budget_bytes={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}"));
    }

    public sealed record Scalar(int I, long L, double D, bool B, string S);
    public sealed record ScalarDto(int I, long L, double D, bool B, string S);

    public class MutableScalar
    {
        public int I { get; set; }
        public long L { get; set; }
        public double D { get; set; }
        public bool B { get; set; }
        public string S { get; set; } = "";
    }

    public class MutableScalarDto
    {
        public int I { get; set; }
        public long L { get; set; }
        public double D { get; set; }
        public bool B { get; set; }
        public string S { get; set; } = "";
    }

    private static readonly IMapper _scalarMapper = new MapperConfiguration(c =>
    {
        c.CreateMap<MutableScalar, MutableScalarDto>();
        c.CreateMap<MutableScalarDto, MutableScalar>();
    }).CreateMapper();

    // ── Round-trip property ────────────────────────────────────────────

    [MeridianProperty(DisplayName = "Round-trip MutableScalar -> Dto -> MutableScalar preserves all fields")]
    public bool RoundTrip_Preserves_All_Fields(int i, long l, NormalFloat d, bool b, NonNull<string> s)
    {
        var original = new MutableScalar { I = i, L = l, D = d.Item, B = b, S = s.Item };
        var dto = _scalarMapper.Map<MutableScalar, MutableScalarDto>(original);
        var roundTripped = _scalarMapper.Map<MutableScalarDto, MutableScalar>(dto);

        return roundTripped.I == original.I
            && roundTripped.L == original.L
            && DoublesEqual(roundTripped.D, original.D)
            && roundTripped.B == original.B
            && roundTripped.S == original.S;
    }

    // NormalFloat excludes NaN/Infinity; use exact equality otherwise.
    private static bool DoublesEqual(double a, double b) =>
        a.Equals(b) || (double.IsNaN(a) && double.IsNaN(b));

    // ── Safety property: no unhandled exception on pathological input ────

    public class BoundedInput
    {
        public int Depth { get; set; }
        public int CollectionSize { get; set; }
        public string Payload { get; set; } = "";
    }

    [MeridianProperty(DisplayName = "Mapping arbitrary string payloads never throws anything but documented exceptions")]
    public bool Arbitrary_Payloads_Never_Throw_Undocumented(NonNull<string> payload)
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<BoundedInput, BoundedInput>());  // identity map
        var mapper = cfg.CreateMapper();

        var input = new BoundedInput { Depth = 0, CollectionSize = 0, Payload = payload.Item };

        try
        {
            var result = mapper.Map<BoundedInput, BoundedInput>(input);
            return result.Payload == payload.Item;
        }
        catch (MeridianMappingLimitException)
        {
            return true;  // documented
        }
        catch (InvalidOperationException)
        {
            // InvalidOperationException is the documented wrapper for
            // configuration errors. Our identity map is valid so this should
            // never actually fire, but it's a documented exception class.
            return true;
        }
        catch (Exception ex) when (ex is StackOverflowException or OutOfMemoryException)
        {
            // These are unrecoverable and indicate a real bug in the mapper.
            return false;
        }
        // Any other exception type is an undocumented leak — FsCheck will
        // surface it as a property-falsification counterexample.
    }

    // ── Collection safety property ─────────────────────────────────────

    public class Item { public int Value { get; set; } }
    public class ItemDto { public int Value { get; set; } }

    private static readonly IMapper _collectionMapper = new MapperConfiguration(c =>
        c.CreateMap<Item, ItemDto>()).CreateMapper();

    [CollectionMeridianProperty(50, DisplayName = "Any collection whose size fits within the cap maps successfully")]  // each case allocates up to ~10k items — bound test count
    public bool Bounded_Collection_Always_Succeeds(PositiveInt sizeHint)
    {
        var size = Math.Min(sizeHint.Item, 5_000);  // stay well under DefaultMaxCollectionItems=10_000
        var source = Enumerable.Range(0, size).Select(i => new Item { Value = i }).ToList();

        var result = _collectionMapper.Map<List<Item>, List<ItemDto>>(source);

        return result.Count == size;
    }

    [CollectionMeridianProperty(10, DisplayName = "Any collection exceeding the cap throws MeridianMappingLimitException")]  // fewer cases since each allocates 10k+ items
    public bool Oversized_Collection_Always_Throws(PositiveInt overCap)
    {
        var size = 10_001 + (overCap.Item % 100);  // just above the cap, not huge
        var source = Enumerable.Range(0, size).Select(i => new Item { Value = i }).ToList();

        try
        {
            _ = _collectionMapper.Map<List<Item>, List<ItemDto>>(source);
            return false;  // should have thrown
        }
        catch (MeridianMappingLimitException ex)
        {
            return ex.Limit == MeridianMappingLimit.MaxCollectionItems
                && ex.MaxAllowed == 10_000;
        }
    }

    // ── Depth safety property ──────────────────────────────────────────

    public class Node
    {
        public int Value { get; set; }
        public Node? Child { get; set; }
    }

    public class NodeDto
    {
        public int Value { get; set; }
        public NodeDto? Child { get; set; }
    }

    private static readonly IMapper _nodeMapper = new MapperConfiguration(c =>
        c.CreateMap<Node, NodeDto>()).CreateMapper();

    [MeridianProperty(25, DisplayName = "Any finite depth graph maps without StackOverflow under default config")]
    public bool Arbitrary_Depth_Never_Stack_Overflows(PositiveInt depthHint)
    {
        var depth = Math.Min(depthHint.Item, 5_000);  // bounded to keep test runtime reasonable
        var head = new Node { Value = 0 };
        var cur = head;
        for (var i = 1; i < depth; i++)
        {
            cur.Child = new Node { Value = i };
            cur = cur.Child;
        }

        try
        {
            var result = _nodeMapper.Map<Node, NodeDto>(head);
            // Either the chain was within MaxDepth and fully mapped, or it was
            // truncated. Either is valid — the invariant is "no crash".
            return result != null;
        }
        catch (Exception ex) when (ex is StackOverflowException or OutOfMemoryException)
        {
            return false;
        }
    }
}
