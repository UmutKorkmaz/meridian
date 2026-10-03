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
#endif
}
#if !BASELINE
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
