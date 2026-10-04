using Meridian.Mapping;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var shared = new Child { Value = 42 };
var dto = Container.Maps.MapToDto(new Source { Value = 42, Child = shared });
Check(dto.Value == 42 && ReferenceEquals(dto.Child, shared), "legacy shallow/nested mapping");
Check(Container.Maps.MapToDto(null!) is null, "legacy null return");
Console.WriteLine("COMMON generated nested=42 shallow=true null=true");
var optional = Container.OptionalMaps.MapToOptionalDto(new OptionalSource { Value = 42, Seed = 99 });
Check(optional.Value == 42 && optional.ConstructorSeed == 7 && optional.Seed == 99, "optional constructor default and legacy member assignments");
Console.WriteLine("COMMON generated optional=49 constructorSeed=7 copiedSeed=99");
#if !BASELINE
var inherited = Container.EnhancedMaps.MapToInitDto(new InheritedSource { Id = 42, Name = "init" });
Check(inherited.Id == 42 && inherited.Name == "init", "inherited/init destination");
Check(Container.RequiredMaps.MapToRequiredDto(new Source { Value = 42 }).Value == 42, "SetsRequiredMembers zero constructor");
Console.WriteLine("CANDIDATE generated inherited=42 init=init required=42");
Check(Container.StrictMaps.Structural(new Source { Value = 42 }).Value == 42, "explicit structural method");
Check(Container.LegacyMismatchMaps.LegacyMismatch(new Source { Value = 42 }).Value == 0, "legacy mismatch remains informational under warnings as errors");
var options = new GenerateMapperAttribute(typeof(Source), typeof(Dto));
Check(options.Mode == GeneratedMappingMode.Legacy && options.MaxDepth == 64 && options.MaxCollectionItems == 10000 &&
    !options.PreserveReferences && !options.AllowUndefinedEnumValues && !options.AllowNullCollections && options.AllowNullDestinationValues &&
    !options.GenerateResolver && options.MethodName is null, "generated attribute defaults");
Console.WriteLine("CANDIDATE generated structural=42 explicitName=true legacyMismatch=0 defaults=true");
var immutable = Container.ImmutableMaps.MapToImmutableDto(new MemberSource { Original = "Ada", Age = 37 });
Check(immutable.Name == "Ada" && immutable.Age == 37 && immutable.Score == 3 && immutable.Note == "kept" && immutable.Seed == 73, "structural immutable, optional, required and static member configuration");
var explicitLegacy = Container.ExplicitLegacyMaps.MapToExplicitDto(new MemberSource { Original = "Ada", Age = 37 });
Check(explicitLegacy.Name == "Ada" && explicitLegacy.Score == 3 && explicitLegacy.Note == "kept", "legacy explicit member configuration");
Console.WriteLine("CANDIDATE generated immutable=Ada:37 seed=73 converter=3 ignore=kept explicitLegacy=true");
var independentInitializer = Container.IndependentInitializerMaps.MapToIndependentInitializerDto(new MemberSource { Original = "Ada", Age = 42 });
Check(independentInitializer.Original == "Ada" && independentInitializer.Age == 42, "optional different-type parameter preserves mapped initializer");
Console.WriteLine("CANDIDATE generated independentInitializer=Ada:42");
Check(Container.LowercaseMaps.MapToLowercaseDto(new LowercaseSource()).Name == "Ada", "case-insensitive positional constructor binding");
Console.WriteLine("CANDIDATE generated lowercaseConstructor=Ada");
var converted = Container.ConversionMaps.MapToConversionDto(new ConversionSource { Count = 42, Text = "1.5", Role = "Friday", Narrow = 42 });
Check(converted.Count == 42L && converted.Number == 0 && converted.Text == 1.5m && converted.Role == DayOfWeek.Friday && converted.Narrow == 42 && converted.Name == "seed", "structural conversions and null policies");
try { Container.ConversionMaps.MapToConversionDto(new ConversionSource { Narrow = 300 }); throw new Exception("unchecked conversion"); } catch (OverflowException) { }
try { Container.ConversionMaps.MapToConversionDto(new ConversionSource { Role = "99" }); throw new Exception("undefined enum accepted"); } catch (MeridianEnumMappingException) { }
Console.WriteLine("CANDIDATE generated conversions=42:0:1.5:Friday null=seed checked=true enumStrict=true");
var graph = new GraphSource { Value = 42 }; graph.Next = graph;
var generatedGraph = Container.GraphMaps.MapToGraphDto(graph);
Check(generatedGraph.Value == 42L && ReferenceEquals(generatedGraph, generatedGraph.Next), "generated mutable cycle identity");
Check(Container.GraphMaps.MapToGraphDto(graph, new GeneratedMappingOptions { MaxDepth = 1 }).Next is null, "depth precedes cached reference");
var collection = Container.CollectionMaps.MapToCollectionDto(new CollectionSource { Values = new[] { graph, graph } });
Check(collection.Values.Count == 2 && collection.Values[0].Value == 42 && ReferenceEquals(collection.Values[0], collection.Values[1]), "nested collection mapping and shared identity");
Check(Container.CollectionMaps.MapToCollectionDto(new CollectionSource()).Values.Count == 0, "null collection default");
try { Container.CollectionMaps.MapToCollectionDto(new CollectionSource { Values = new[] { graph, graph } }, new GeneratedMappingOptions { MaxCollectionItems = 1 }); throw new Exception("collection cap ignored"); } catch (MeridianMappingLimitException e) { Check(e.MaxAllowed == 1 && e.ObservedValue == 2, "collection limit values"); }
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { Container.GraphMaps.MapToGraphDto(graph, new GeneratedMappingOptions { CancellationToken = cancelled.Token }); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }
Console.WriteLine("CANDIDATE generated graph=42 cycle=true collection=2 shared=true null=empty depth=true limit=true cancel=true");
#endif
static void Check(bool result, string name) { if (!result) throw new InvalidOperationException(name); }
public sealed class Child { public int Value { get; set; } }
public sealed class Source { public int Value { get; set; } public Child? Child { get; set; } }
public sealed class Dto { public int Value { get; set; } public Child? Child { get; set; } }
public partial class Container
{
    [GenerateMapper(typeof(Source), typeof(Dto))]
    public static partial class Maps { }
    [GenerateMapper(typeof(OptionalSource), typeof(OptionalDto))]
    public static partial class OptionalMaps { }
#if !BASELINE
    [GenerateMapper(typeof(InheritedSource), typeof(InitDto))]
    public static partial class EnhancedMaps { }
    [GenerateMapper(typeof(Source), typeof(RequiredDto))]
    public static partial class RequiredMaps { }
    [GenerateMapper(typeof(Source), typeof(ScalarDto), Mode = GeneratedMappingMode.Structural, MethodName = "Structural")]
    public static partial class StrictMaps { }
    [GenerateMapper(typeof(Source), typeof(MismatchDto), MethodName = "LegacyMismatch")]
    public static partial class LegacyMismatchMaps { }
    [GenerateMapper(typeof(MemberSource), typeof(ImmutableDto), Mode = GeneratedMappingMode.Structural)]
    [MapMember("Name", "Original")]
    [MapMember("Score", "Original")]
    [MapWith("Score", typeof(MemberConverters), "Length")]
    [IgnoreMapMember("Note")]
    public static partial class ImmutableMaps { }
    [GenerateMapper(typeof(MemberSource), typeof(IndependentInitializerDto), Mode = GeneratedMappingMode.Structural)]
    public static partial class IndependentInitializerMaps { }
    [GenerateMapper(typeof(LowercaseSource), typeof(LowercaseDto), Mode = GeneratedMappingMode.Structural)]
    public static partial class LowercaseMaps { }
    [GenerateMapper(typeof(MemberSource), typeof(ExplicitDto))]
    [MapMember("Name", "Original", SourceType = typeof(MemberSource), DestinationType = typeof(ExplicitDto))]
    [MapMember("Score", "Original")]
    [MapWith("Score", typeof(MemberConverters), "Length")]
    [IgnoreMapMember("Note")]
    public static partial class ExplicitLegacyMaps { }
    [GenerateMapper(typeof(ConversionSource), typeof(ConversionDto), Mode = GeneratedMappingMode.Structural, AllowNullDestinationValues = false)]
    [ConvertMapMember("Text", GeneratedConversionKind.InvariantParse)]
    [ConvertMapMember("Role", GeneratedConversionKind.InvariantParse)]
    [ConvertMapMember("Narrow", GeneratedConversionKind.CheckedNumeric, SourceType = typeof(ConversionSource), DestinationType = typeof(ConversionDto))]
    public static partial class ConversionMaps { }
    [GenerateMapper(typeof(GraphSource), typeof(GraphDto), Mode = GeneratedMappingMode.Structural, PreserveReferences = true)]
    public static partial class GraphMaps { }
    [GenerateMapper(typeof(GraphSource), typeof(GraphDto), Mode = GeneratedMappingMode.Structural, PreserveReferences = true)]
    [GenerateMapper(typeof(CollectionSource), typeof(CollectionDto), Mode = GeneratedMappingMode.Structural)]
    public static partial class CollectionMaps { }
#endif
}
#if !BASELINE
public sealed class GraphSource { public int Value { get; set; } public GraphSource? Next { get; set; } }
public sealed class GraphDto { public long Value { get; set; } public GraphDto? Next { get; set; } }
public sealed class CollectionSource { public IEnumerable<GraphSource>? Values { get; set; } }
public sealed record CollectionDto(IReadOnlyList<GraphDto> Values);
public sealed class ConversionSource { public int Count { get; set; } public int? Number { get; set; } public string Text { get; set; } = "1.5"; public string Role { get; set; } = "Friday"; public int Narrow { get; set; } public string? Name { get; set; } }
public sealed class ConversionDto { public long Count { get; set; } public int Number { get; set; } public decimal Text { get; set; } public DayOfWeek Role { get; set; } public byte Narrow { get; set; } public string? Name { get; set; } = "seed"; }
public sealed class MemberSource { public string Original { get; set; } = ""; public int Age { get; set; } }
public sealed record ImmutableDto(string Name, int Age, int Seed = 73)
{
    public required int Score { get; init; }
    public string Note { get; init; } = "kept";
}
public sealed class LowercaseSource { public string name { get; set; } = "Ada"; }
public sealed record LowercaseDto(string Name);
public sealed class IndependentInitializerDto
{
    public IndependentInitializerDto(int original = 73, string age = "ctor") { }
    public string Original { get; init; } = "ctor";
    public int Age { get; init; } = 7;
}
public sealed class ExplicitDto
{
    public string Name { get; init; } = "";
    public int Score { get; init; }
    public string Note { get; init; } = "kept";
}
public static class MemberConverters { public static int Length(string value) => value.Length; }
public sealed class ScalarDto { public int Value { get; set; } }
public sealed class MismatchDto { public long Value { get; set; } }
public class BaseSource { public int Id { get; set; } }
public sealed class InheritedSource : BaseSource { public string Name { get; set; } = ""; }
public class BaseDto { public int Id { get; init; } }
public sealed class InitDto : BaseDto { public string Name { get; init; } = ""; }
public sealed class RequiredDto
{
    public required int Value { get; set; }
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public RequiredDto() { Value = 0; }
}
#endif

public sealed class OptionalSource { public int Value { get; set; } public int Seed { get; set; } }
public sealed class OptionalDto
{
    public OptionalDto(int seed = 7) { ConstructorSeed = seed; Seed = seed; }
    public int ConstructorSeed { get; }
    public int Seed { get; set; }
    public int Value { get; set; }
}
