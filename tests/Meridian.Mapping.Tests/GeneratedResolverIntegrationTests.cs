using Microsoft.CodeAnalysis;

namespace Meridian.Mapping.Tests;

public class GeneratedResolverIntegrationTests
{
    [Theory]
    [InlineData("Explicit_Profile_Wins_Over_Generated_Resolver")]
    [InlineData("Generated_Unconfigured_Pair_Is_Used")]
    [InlineData("Existing_Destination_Keeps_Runtime_Semantics")]
    [InlineData("Operation_Items_State_And_Global_Caps_Are_Preserved")]
    [InlineData("Catalogued_Ambiguity_Is_Rejected")]
    [InlineData("Opaque_Ambiguity_Is_Rejected_Before_Execution")]
    [InlineData("Nested_Explicit_Map_Preserves_Operation")]
    [InlineData("Nested_Runtime_To_Generated_Preserves_Depth")]
    [InlineData("Direct_Generated_Method_Remains_Independent")]
    [InlineData("Nested_Runtime_Cannot_Raise_Generated_Cap")]
    [InlineData("Mixed_Reference_Identity_Is_Preserved")]
    [InlineData("Scoped_Resolver_And_Global_Transformer_Run_Once")]
    [InlineData("Custom_ConfigurationProvider_Still_Compiles")]
    [InlineData("Global_Null_Collection_Policy_Is_Preserved")]
    [InlineData("Legacy_Resolver_Preserves_Shallow_Aliases")]
    [InlineData("Alternating_Traversal_Keeps_Generated_Caps")]
    [InlineData("Runtime_PerMap_Depth_Still_Overrides_Global_Outside_Bridge")]
    [InlineData("Existing_Unconfigured_Destination_Does_Not_Use_Resolver")]
    [InlineData("Included_Derived_Child_Uses_Runtime_Map")]
    [InlineData("Opaque_Descriptor_Sees_Inherited_Caps")]
    [InlineData("Global_Null_Setter_Policy_Is_Preserved")]
    [InlineData("Custom_Mapper_Context_Bridge_Fails_Explicitly")]
    [InlineData("Registration_Is_Snapshotted_And_Validated")]
    [InlineData("Duplicate_Catalog_Entries_Are_Rejected")]
    public void Resolver_Contract(string scenario)
    {
        var result = GeneratorTestHarness.Compile(Fixture);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result, "Checks", scenario));
    }

    [Theory]
    [InlineData("GeneratedResolver", "Mode=GeneratedMappingMode.Structural,")]
    [InlineData("GeneratedMappingContext", "")]
    [InlineData("__MeridianMap0", "")]
    public void Resolver_Helper_Collisions_Are_Diagnosed(string name, string mode)
    {
        var result=GeneratorTestHarness.Compile($"using Meridian.Mapping; public class S {{ public int Value {{get;set;}} }} public class D {{ public int Value {{get;set;}} }} [GenerateMapper(typeof(S),typeof(D), {mode} GenerateResolver=true)] public static partial class {name} {{ }}");
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN004");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unsupported_Global_Init_Null_Policy_Rejects_Before_Traversal(bool nested)
    {
        var result=GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public static int Reads; public string Name {get {Reads++;return null;}} }
            public class Dto {public string Name {get;init;}="seed";}
            public record CtorDto(string Name);
            public class Outer {public static int Reads;public Source Child {get{Reads++;return new Source();}}}
            public class OuterDto {public Dto Child {get;set;}}
            [GenerateMapper(typeof(Outer),typeof(OuterDto),Mode=GeneratedMappingMode.Structural,GenerateResolver=true)]
            [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural,GenerateResolver=true)]
            [GenerateMapper(typeof(Source),typeof(CtorDto),Mode=GeneratedMappingMode.Structural,GenerateResolver=true)]
            public static partial class Maps
            {
                public static bool ProofNested()
                {
                    var mapper=new MapperConfiguration(c=>{c.AllowNullDestinationValues=false;c.UseGeneratedMappings(new GeneratedResolver());c.CreateMap<Source,Dto>();}).CreateMapper();
                    try{mapper.Map<Outer,OuterDto>(new Outer());return false;}catch(System.NotSupportedException){return Source.Reads==0&&Outer.Reads==0;}
                }
                public static bool Proof()
                {
                    var mapper=new MapperConfiguration(c=>{c.AllowNullDestinationValues=false;c.UseGeneratedMappings(new GeneratedResolver());}).CreateMapper();
                    try {mapper.Map<Source,Dto>(new Source());return false;}catch(System.NotSupportedException){}
                    if(Source.Reads!=0)return false;
                    if(MapToDto(new Source()).Name!=null)return false;
                    if(mapper.Map<Source,CtorDto>(new Source()).Name!=null)return false;
                    var explicitMapper=new MapperConfiguration(c=>{c.AllowNullDestinationValues=false;c.UseGeneratedMappings(new GeneratedResolver());c.CreateMap<Source,Dto>();}).CreateMapper();
                    return explicitMapper.Map<Source,Dto>(new Source()).Name=="seed";
                }
            }
            """);
        Assert.Empty(result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error));
        Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result,"Maps",nested?"ProofNested":"Proof"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_Type_Nested_Descriptor_Uses_Copy_Context_And_Cache(bool interpreter)
    {
        var resolver = new SameResolver();
        var configuration = SameConfiguration(interpreter, resolver);
        var mapper = configuration.CreateMapper(new SameServices());
        var child = new SameChild { Value = 42 };
        var state = new object();
        var result = mapper.Map<SameParent, SameParentDto>(new() { Left = child, Right = child }, options =>
        { options.State = state; options.Items["answer"] = 7; });
        Assert.NotSame(child, result.Left);
        Assert.Same(result.Left, result.Right);
        Assert.Equal(49, result.Left!.Value);
        Assert.Equal(2, resolver.Calls);
        Assert.Same(state, resolver.Seen!.State);
        Assert.IsType<SameServices>(resolver.Seen.ServiceProvider);
        Assert.Equal(1, resolver.Seen.Depth);
        Assert.Equal(4, resolver.Seen.MaxDepth);
        Assert.Equal(3, resolver.Seen.MaxCollectionItems);
        result.Left.Value = 100;
        Assert.Equal(42, child.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_Type_Explicit_Map_Wins_Without_Resolver_Lookup(bool interpreter)
    {
        var resolver = new SameResolver { ThrowOnLookup = true };
        var configuration = SameConfiguration(interpreter, resolver, explicitChild: true);
        var source = new SameChild { Value = 42 };
        var result = configuration.CreateMapper().Map<SameParent, SameParentDto>(new() { Left = source });
        Assert.NotSame(source, result.Left);
        Assert.Equal(88, result.Left!.Value);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_Type_Generated_Dispatch_Respects_Depth_Cap(bool interpreter)
    {
        var resolver = new SameResolver();
        var configuration = SameConfiguration(interpreter, resolver, maxDepth: 1);
        var result = configuration.CreateMapper().Map<SameParent, SameParentDto>(new() { Left = new SameChild() });
        Assert.Null(result.Left);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_Type_Unregistered_And_Existing_Destination_Preserve_Alias(bool interpreter)
    {
        var child = new SameChild { Value = 42 };
        var source = new SameParent { Left = child };
        var unregistered = SameConfiguration(interpreter, null).CreateMapper().Map<SameParent, SameParentDto>(source);
        Assert.Same(child, unregistered.Left);
        var resolver = new SameResolver { ThrowOnLookup = true };
        var mapper = SameConfiguration(interpreter, resolver).CreateMapper();
        var existing = new SameParentDto();
        Assert.Same(existing, mapper.Map(source, existing));
        Assert.Same(child, existing.Left);
        Assert.Equal(0, resolver.Calls);
        resolver.ThrowOnLookup = false;
        var fresh = mapper.Map<SameParent, SameParentDto>(source, o => o.Items["answer"] = 7);
        Assert.NotSame(child, fresh.Left);
        Assert.Equal(49, fresh.Left!.Value);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void Same_Type_Destination_Reuse_Does_Not_Invoke_Descriptor()
    {
        var resolver = new SameResolver { ThrowOnLookup = true };
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<SameParent, SameParentDto>().ForMember(d => d.Left, o => { o.MapFrom(s => s.Left); o.UseDestinationValue(); });
            c.CreateMap<SameChild, SameChild>();
        });
        var existing = new SameParentDto { Left = new SameChild() };
        var original = existing.Left;
        var result = configuration.CreateMapper().Map(new SameParent { Left = new SameChild { Value = 42 } }, existing);
        Assert.Same(original, result.Left);
        Assert.Equal(42, result.Left!.Value);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Same_Type_Element_Registration_Does_Not_Deep_Copy_Assignable_Collection()
    {
        var resolver = new SameResolver();
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<SameCollectionParent, SameCollectionDto>();
        });
        var values = new[] { new SameChild { Value = 42 } };
        var mapped = configuration.CreateMapper().Map<SameCollectionParent, SameCollectionDto>(new() { Values = values });
        Assert.Same(values, mapped.Values);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Same_Type_Collection_Pair_Registration_Executes_Before_Assignability()
    {
        var resolver = new SameCollectionResolver();
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<SameCollectionParent, SameCollectionDto>();
        });
        var values = new[] { new SameChild { Value = 42 } };
        var mapped = configuration.CreateMapper().Map<SameCollectionParent, SameCollectionDto>(new() { Values = values });
        Assert.NotSame(values, mapped.Values);
        Assert.Equal(43, mapped.Values![0].Value);
        Assert.Equal(1, resolver.Calls);
    }

    public sealed class SameCollectionParent { public SameChild[]? Values { get; set; } }
    public sealed class SameCollectionDto { public SameChild[]? Values { get; set; } }
    private sealed class SameCollectionResolver : IGeneratedMappingResolver
    {
        public int Calls;
        public bool TryGetMapping(Type source, Type destination, out GeneratedMappingDescriptor? mapping)
        {
            mapping = source == typeof(SameChild[]) && destination == source
                ? new GeneratedMappingDescriptor(source, destination, (value, context) =>
                { Calls++; return ((SameChild[])value!).Select(c => new SameChild { Value = c.Value + 1 }).ToArray(); }) : null;
            return mapping is not null;
        }
    }

    [Fact]
    public void Same_Type_Compiled_Member_Is_Read_Once()
    {
        var configuration = new MapperConfiguration(c => { c.UseGeneratedMappings(new SameResolver()); c.CreateMap<ReadOnceParent, ReadOnceDto>(); });
        Assert.NotNull(configuration.FindTypeMap(typeof(ReadOnceParent), typeof(ReadOnceDto))!.CompiledFastPath);
        var source = new ReadOnceParent();
        var mapped = configuration.CreateMapper().Map<ReadOnceParent, ReadOnceDto>(source, o => o.Items["answer"] = 7);
        Assert.Equal(1, source.Reads);
        Assert.Equal(49, mapped.Left!.Value);
    }

    public sealed class ReadOnceParent
    {
        public int Reads;
        public SameChild Left { get { Reads++; return new SameChild { Value = 42 }; } }
    }
    public sealed class ReadOnceDto { public SameChild? Left { get; set; } }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_Type_Nested_Ambiguity_Throws_Before_Descriptor_Execution(bool interpreter)
    {
        var first = new SameResolver();
        var second = new SameResolver();
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(first, second);
            var map = c.CreateMap<SameParent, SameParentDto>();
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Throws<InvalidOperationException>(() => configuration.CreateMapper().Map<SameParent, SameParentDto>(new() { Left = new SameChild() }));
        Assert.Equal(0, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    private static MapperConfiguration SameConfiguration(bool interpreter, SameResolver? resolver, bool explicitChild = false, int maxDepth = 4)
    {
        var configuration = new MapperConfiguration(c =>
        {
            c.DefaultMaxDepth = maxDepth;
            c.DefaultMaxCollectionItems = 3;
            if (resolver is not null) c.UseGeneratedMappings(resolver);
            var map = c.CreateMap<SameParent, SameParentDto>();
            if (interpreter) map.AfterMap((s, d) => { });
            if (explicitChild) c.CreateMap<SameChild, SameChild>().AfterMap((s, d) => d.Value = 88);
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(SameParent), typeof(SameParentDto))!.CompiledFastPath is not null);
        return configuration;
    }

    public sealed class SameChild { public int Value { get; set; } }
    public sealed class SameParent { public SameChild? Left { get; set; } public SameChild? Right { get; set; } }
    public sealed class SameParentDto { public SameChild? Left { get; set; } public SameChild? Right { get; set; } }
    private sealed class SameServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
    private sealed class SameResolver : IGeneratedMappingResolver
    {
        public int Calls;
        public bool ThrowOnLookup;
        public Meridian.Mapping.Execution.ResolutionContext? Seen;
        public bool TryGetMapping(Type source, Type destination, out GeneratedMappingDescriptor? mapping)
        {
            if (ThrowOnLookup && source == typeof(SameChild) && destination == source) throw new InvalidOperationException("Unexpected resolver lookup");
            mapping = source == typeof(SameChild) && destination == source
                ? new GeneratedMappingDescriptor(source, destination, (value, context) =>
                {
                    Calls++; Seen = context;
                    if (context.TryGetMapped(value!, destination, out var cached)) return cached;
                    var copy = new SameChild { Value = ((SameChild)value!).Value + (int)context.Items["answer"]! };
                    context.CacheMapped(value!, destination, copy);
                    return copy;
                }) : null;
            return mapping is not null;
        }
    }

    [Theory]
    [InlineData(false, "property")]
    [InlineData(true, "property")]
    [InlineData(false, "expression")]
    [InlineData(true, "expression")]
    public void Declared_Base_Explicit_Map_Precedes_Runtime_Descriptor(bool interpreter, string shape)
    {
        var resolver = new DeclaredResolver(typeof(DeclaredDerived));
        var actions = 0;
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<DeclaredBase, DeclaredBase>().AfterMap((s, d) => { actions++; d.Value = 88; });
            var map = c.CreateMap<DeclaredParent, DeclaredDto>();
            if (shape == "expression") map.ForMember(d => d.Item, o => o.MapFrom(s => s.Alternate));
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(DeclaredParent), typeof(DeclaredDto))!.CompiledFastPath is not null);
        var child = new DeclaredDerived { Value = 42 };
        var mapped = configuration.CreateMapper().Map<DeclaredParent, DeclaredDto>(new()
        { Item = child, Alternate = child, Envelope = new() { Item = child } });
        Assert.NotSame(child, mapped.Item);
        Assert.Equal(88, mapped.Item!.Value);
        Assert.Equal(1, actions);
        Assert.Equal(0, resolver.Calls);
        mapped.Item.Value = 7;
        Assert.Equal(42, child.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_Interface_Explicit_Map_Precedes_Runtime_Descriptor(bool interpreter)
    {
        var resolver = new DeclaredResolver(typeof(DeclaredDerived));
        var actions = 0;
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<IDeclaredChild, DeclaredBase>().AfterMap((s, d) => { actions++; d.Value = 88; });
            var map = c.CreateMap<DeclaredParent, DeclaredDto>().ForMember(d => d.Item, o => o.MapFrom(s => s.Contract));
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(DeclaredParent), typeof(DeclaredDto))!.CompiledFastPath is not null);
        var child = new DeclaredDerived { Value = 42 };
        var mapped = configuration.CreateMapper().Map<DeclaredParent, DeclaredDto>(new() { Contract = child });
        Assert.NotSame(child, mapped.Item);
        Assert.Equal(88, mapped.Item!.Value);
        Assert.Equal(1, actions);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_Base_Included_Map_Precedes_Runtime_Descriptor(bool interpreter)
    {
        var resolver = new DeclaredResolver(typeof(DeclaredDerived));
        var actions = 0;
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<DeclaredBase, DeclaredBase>().Include<DeclaredDerived, DeclaredDerivedDto>();
            c.CreateMap<DeclaredDerived, DeclaredDerivedDto>().AfterMap((s, d) => { actions++; d.Value = 88; });
            var map = c.CreateMap<DeclaredParent, DeclaredDto>();
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(DeclaredParent), typeof(DeclaredDto))!.CompiledFastPath is not null);
        var mapped = configuration.CreateMapper().Map<DeclaredParent, DeclaredDto>(new() { Item = new DeclaredDerived { Value = 42 } });
        Assert.IsType<DeclaredDerivedDto>(mapped.Item);
        Assert.Equal(88, mapped.Item.Value);
        Assert.Equal(1, actions);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_Base_Descriptor_Fallback_Preserves_Operation(bool interpreter)
    {
        var resolver = new DeclaredResolver(typeof(DeclaredBase));
        var configuration = new MapperConfiguration(c =>
        {
            c.DefaultMaxDepth = 4;
            c.DefaultMaxCollectionItems = 3;
            c.UseGeneratedMappings(resolver);
            var map = c.CreateMap<DeclaredParent, DeclaredDto>();
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(DeclaredParent), typeof(DeclaredDto))!.CompiledFastPath is not null);
        var child = new DeclaredDerived { Value = 42 };
        var state = new object();
        var mapped = configuration.CreateMapper().Map<DeclaredParent, DeclaredDto>(new() { Item = child }, o => o.State = state);
        Assert.NotSame(child, mapped.Item);
        Assert.Equal(999, mapped.Item!.Value);
        Assert.Equal(1, resolver.Calls);
        Assert.Same(state, resolver.Seen!.State);
        Assert.Equal(1, resolver.Seen.Depth);
        Assert.Equal(4, resolver.Seen.MaxDepth);
        Assert.Equal(3, resolver.Seen.MaxCollectionItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_Base_Runtime_Map_Still_Precedes_Declared_Map(bool interpreter)
    {
        var resolver = new DeclaredResolver(typeof(DeclaredDerived));
        var runtimeActions = 0;
        var baseActions = 0;
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<DeclaredBase, DeclaredBase>().AfterMap((s, d) => { baseActions++; d.Value = 88; });
            c.CreateMap<DeclaredDerived, DeclaredBase>().AfterMap((s, d) => { runtimeActions++; d.Value = 77; });
            var map = c.CreateMap<DeclaredParent, DeclaredDto>();
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(DeclaredParent), typeof(DeclaredDto))!.CompiledFastPath is not null);
        var mapped = configuration.CreateMapper().Map<DeclaredParent, DeclaredDto>(new() { Item = new DeclaredDerived { Value = 42 } });
        Assert.Equal(77, mapped.Item!.Value);
        Assert.Equal(1, runtimeActions);
        Assert.Equal(0, baseActions);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_Flattened_Member_Preserves_Terminal_Type(bool interpreter)
    {
        var resolver = new DeclaredResolver(typeof(DeclaredDerived));
        var actions = 0;
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<DeclaredBase, DeclaredBase>().AfterMap((s, d) => { actions++; d.Value = 88; });
            var map = c.CreateMap<DeclaredParent, DeclaredFlatDto>();
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(DeclaredParent), typeof(DeclaredFlatDto))!.CompiledFastPath is not null);
        var mapped = configuration.CreateMapper().Map<DeclaredParent, DeclaredFlatDto>(new()
        { Envelope = new() { Item = new DeclaredDerived { Value = 42 } } });
        Assert.Equal(88, mapped.EnvelopeItem!.Value);
        Assert.Equal(1, actions);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData("constant")]
    [InlineData("function")]
    [InlineData("converter")]
    [InlineData("resolver")]
    [InlineData("null-substitute")]
    public void Declared_Unknown_Output_Does_Not_Reuse_Source_Property_Metadata(string kind)
    {
        var resolver = new DeclaredResolver(typeof(DeclaredDerived));
        var actions = 0;
        var child = new DeclaredDerived { Value = 42 };
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<DeclaredBase, DeclaredBase>().AfterMap((s, d) => { actions++; d.Value = 88; });
            c.CreateMap<DeclaredParent, DeclaredDto>().ForMember(d => d.Item, o =>
            {
                if (kind == "constant") o.UseValue(child);
                if (kind == "function") o.MapFrom((s, d) => child);
                if (kind == "converter") o.ConvertUsing((DeclaredBase? value) => child, s => s.Item);
                if (kind == "resolver") o.MapFrom<DeclaredValueResolver>();
                if (kind == "null-substitute") { o.MapFrom(s => s.Item); o.NullSubstitute(child); }
            });
        });
        Assert.Null(configuration.FindTypeMap(typeof(DeclaredParent), typeof(DeclaredDto))!.CompiledFastPath);
        var mapped = configuration.CreateMapper().Map<DeclaredParent, DeclaredDto>(new());
        Assert.Equal(999, mapped.Item!.Value);
        Assert.Equal(0, actions);
        Assert.Equal(1, resolver.Calls);
    }

    public sealed class DeclaredFlatDto { public DeclaredBase? EnvelopeItem { get; set; } }
    public sealed class DeclaredValueResolver : Meridian.Mapping.Converters.IValueResolver<DeclaredParent, DeclaredDto, DeclaredBase>
    {
        public DeclaredBase Resolve(DeclaredParent source, DeclaredDto destination, DeclaredBase destMember,
            Meridian.Mapping.Execution.ResolutionContext context) => new DeclaredDerived { Value = 42 };
    }

    public interface IDeclaredChild { int Value { get; set; } }
    public class DeclaredBase : IDeclaredChild { public int Value { get; set; } }
    public sealed class DeclaredDerived : DeclaredBase { }
    public sealed class DeclaredDerivedDto : DeclaredBase { }
    public sealed class DeclaredParent
    {
        public DeclaredBase? Item { get; set; }
        public DeclaredBase? Alternate { get; set; }
        public IDeclaredChild? Contract { get; set; }
        public DeclaredEnvelope? Envelope { get; set; }
    }
    public sealed class DeclaredEnvelope { public DeclaredBase? Item { get; set; } }
    public sealed class DeclaredDto { public DeclaredBase? Item { get; set; } }
    private sealed class DeclaredResolver(Type declaredType) : IGeneratedMappingResolver
    {
        public int Calls;
        public Meridian.Mapping.Execution.ResolutionContext? Seen;
        public bool TryGetMapping(Type source, Type destination, out GeneratedMappingDescriptor? mapping)
        {
            mapping = source == declaredType && destination == typeof(DeclaredBase)
                ? new GeneratedMappingDescriptor(source, destination, (value, context) =>
                { Calls++; Seen = context; return new DeclaredBase { Value = 999 }; }) : null;
            return mapping is not null;
        }
    }

    [Theory]
    [InlineData(false, "default")]
    [InlineData(true, "default")]
    [InlineData(false, "global-null")]
    [InlineData(true, "global-null")]
    [InlineData(false, "member-null")]
    [InlineData(true, "member-null")]
    public void Null_Normalization_Collection_Does_Not_Dispatch_Original_Source_Pair(bool explicitMap, string policy)
    {
        var resolver = new NormalizationResolver(typeof(IEnumerable<NormalizationSource>), typeof(IEnumerable<NormalizationDto>),
            value => ((IEnumerable<NormalizationSource>)value!).Select(x => new NormalizationDto { Value = x.Value }).ToList());
        var actions = 0;
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            c.AllowNullCollections = policy == "global-null";
            var map = c.CreateMap<NormalizationParent, NormalizationParentDto>();
            if (policy == "member-null") map.ForMember(d => d.Values, o => { o.MapFrom(s => s.Values); o.AllowNull(); });
            if (explicitMap) c.CreateMap<IEnumerable<NormalizationSource>, IEnumerable<NormalizationDto>>()
                .ConvertUsing(source => { actions++; return source.Select(x => new NormalizationDto { Value = x.Value }).ToList(); });
        });
        // Collection members are deliberately ineligible for the compiled fast path.
        Assert.Null(configuration.FindTypeMap(typeof(NormalizationParent), typeof(NormalizationParentDto))!.CompiledFastPath);
        var mapped = configuration.CreateMapper().Map<NormalizationParent, NormalizationParentDto>(new());
        if (policy == "default") Assert.Empty(Assert.IsAssignableFrom<IEnumerable<NormalizationDto>>(mapped.Values));
        else Assert.Null(mapped.Values);
        Assert.Equal(0, actions);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Null_Normalization_Value_Default_Does_Not_Dispatch_Original_Source_Pair(bool interpreter, bool explicitMap)
    {
        var resolver = new NormalizationResolver(typeof(int?), typeof(long), value => (long)(int)value!);
        var actions = 0;
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            if (explicitMap) c.CreateMap<int?, long>().ConvertUsing(source => { actions++; return source ?? 0; });
            var map = c.CreateMap<NormalizationValueSource, NormalizationValueDto>();
            if (interpreter) map.AfterMap((s, d) => { });
        });
        Assert.Equal(!interpreter, configuration.FindTypeMap(typeof(NormalizationValueSource), typeof(NormalizationValueDto))!.CompiledFastPath is not null);
        Assert.Equal(0, configuration.CreateMapper().Map<NormalizationValueSource, NormalizationValueDto>(new()).Value);
        Assert.Equal(0, actions);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Null_Normalization_Preserves_Runtime_Pair_Descriptor_For_Synthesized_Value()
    {
        var resolver = new NormalizationResolver(typeof(List<NormalizationDto>), typeof(IEnumerable<NormalizationDto>),
            value => ((List<NormalizationDto>)value!).Append(new NormalizationDto { Value = 77 }).ToList());
        var configuration = new MapperConfiguration(c =>
        { c.UseGeneratedMappings(resolver); c.CreateMap<NormalizationParent, NormalizationParentDto>(); });
        var mapped = configuration.CreateMapper().Map<NormalizationParent, NormalizationParentDto>(new());
        Assert.Equal(77, Assert.Single(mapped.Values!).Value);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void Null_Normalization_Preserves_Genuine_Nonnull_Declared_Descriptor_Fallback()
    {
        var resolver = new NormalizationResolver(typeof(IEnumerable<NormalizationDto>), typeof(IEnumerable<NormalizationDto>),
            value => ((IEnumerable<NormalizationDto>)value!).Select(x => new NormalizationDto { Value = x.Value + 1 }).ToList());
        var configuration = new MapperConfiguration(c =>
        { c.UseGeneratedMappings(resolver); c.CreateMap<NormalizationParentDto, NormalizationParentDto>(); });
        var source = new List<NormalizationDto> { new() { Value = 42 } };
        var mapped = configuration.CreateMapper().Map<NormalizationParentDto, NormalizationParentDto>(new() { Values = source });
        Assert.NotSame(source, mapped.Values);
        Assert.Equal(43, Assert.Single(mapped.Values!).Value);
        Assert.Equal(1, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_Normalization_Existing_Destination_And_Reuse_Exclude_Declared_Descriptor(bool reuse)
    {
        var resolver = new NormalizationResolver(typeof(IEnumerable<NormalizationSource>), typeof(IEnumerable<NormalizationDto>),
            value => throw new InvalidOperationException("Unexpected declared descriptor"));
        var configuration = new MapperConfiguration(c =>
        {
            c.UseGeneratedMappings(resolver);
            var map = c.CreateMap<NormalizationParent, NormalizationParentDto>();
            if (reuse) map.ForMember(d => d.Values, o => { o.MapFrom(s => s.Values); o.UseDestinationValue(); });
        });
        var original = new List<NormalizationDto> { new() { Value = 42 } };
        var destination = new NormalizationParentDto { Values = original };
        var mapped = configuration.CreateMapper().Map(new NormalizationParent(), destination);
        Assert.Same(destination, mapped);
        if (reuse) Assert.Same(original, mapped.Values);
        Assert.Empty(mapped.Values!);
        Assert.Equal(0, resolver.Calls);
    }

    public sealed class NormalizationSource { public int Value { get; set; } }
    public sealed class NormalizationDto { public int Value { get; set; } }
    public sealed class NormalizationParent { public IEnumerable<NormalizationSource>? Values { get; set; } }
    public sealed class NormalizationParentDto { public IEnumerable<NormalizationDto>? Values { get; set; } }
    public sealed class NormalizationValueSource { public int? Value { get; set; } }
    public sealed class NormalizationValueDto { public long Value { get; set; } }
    private sealed class NormalizationResolver(Type sourceType, Type destinationType, Func<object?, object?> map) : IGeneratedMappingResolver
    {
        public int Calls;
        public bool TryGetMapping(Type source, Type destination, out GeneratedMappingDescriptor? mapping)
        {
            mapping = source == sourceType && destination == destinationType
                ? new GeneratedMappingDescriptor(source, destination, (value, context) => { Calls++; return map(value); }) : null;
            return mapping is not null;
        }
    }

    private const string Fixture = """
        using System;
        using System.Collections.Generic;
        using Meridian.Mapping;
        using Meridian.Mapping.Execution;
        public class Source { public int Value { get; set; } public Source Child { get; set; } public int[] Values { get; set; } }
        public class Dto { public int Value { get; set; } public Dto Child { get; set; } public long[] Values { get; set; } }
        public class Outer { public Source Item { get; set; } }
        public class OuterDto { public Dto Item { get; set; } }
        [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true, MaxDepth = 4, MaxCollectionItems = 3, PreserveReferences = true)]
        [GenerateMapper(typeof(Outer), typeof(OuterDto), Mode = GeneratedMappingMode.Structural, GenerateResolver=true)]
        public static partial class Maps { }
        public class Resolver : IGeneratedMappingResolver
        {
            public int Calls;
            public ResolutionContext Seen;
            public bool TryGetMapping(Type source, Type destination, out GeneratedMappingDescriptor mapping)
            {
                mapping = source == typeof(Source) && destination == typeof(Dto)
                    ? new GeneratedMappingDescriptor(source, destination, (value, context) => { Calls++; Seen=context; return new Dto { Value = 77 }; }) : null;
                return mapping != null;
            }
        }
        public static class Checks
        {
            public static bool Generated_Unconfigured_Pair_Is_Used()
            {
                var c = new MapperConfiguration(c => c.UseGeneratedMappings(new Maps.GeneratedResolver()));
                c.AssertConfigurationIsValid();
                return c.CreateMapper().Map<Source, Dto>(new Source { Value=42 }).Value == 42;
            }
            public static bool Explicit_Profile_Wins_Over_Generated_Resolver()
            {
                var resolver = new Resolver(); int before=0, after=0;
                var mapper = new MapperConfiguration(c => { c.UseGeneratedMappings(resolver); c.CreateMap<Source,Dto>().BeforeMap((s,d) => before++).AfterMap((s,d) => after++); }).CreateMapper();
                return mapper.Map<Source,Dto>(new Source {Value=42}).Value == 42 && before==1 && after==1 && resolver.Calls==0;
            }
            public static bool Existing_Destination_Keeps_Runtime_Semantics()
            {
                var resolver = new Resolver(); var mapper = new MapperConfiguration(c => { c.UseGeneratedMappings(resolver); c.CreateMap<Source,Dto>(); }).CreateMapper();
                var destination=new Dto(); return ReferenceEquals(destination,mapper.Map(new Source {Value=8}, destination)) && destination.Value==8 && resolver.Calls==0;
            }
            public static bool Operation_Items_State_And_Global_Caps_Are_Preserved()
            {
                var resolver=new Resolver(); var state=new object(); var mapper=new MapperConfiguration(c=>c.UseGeneratedMappings(resolver)).CreateMapper();
                mapper.Map<Source,Dto>(new Source(), o=>{o.State=state;o.Items["key"]=42;});
                if (!ReferenceEquals(state,resolver.Seen.State) || (int)resolver.Seen.Items["key"]!=42) return false;
                var bounded=new MapperConfiguration(c=>{c.DefaultMaxDepth=1;c.DefaultMaxCollectionItems=1;c.UseGeneratedMappings(new Maps.GeneratedResolver());}).CreateMapper();
                if(bounded.Map<Source,Dto>(new Source {Child=new Source()}).Child!=null) return false;
                var collectionMapper=new MapperConfiguration(c=>{c.DefaultMaxCollectionItems=1;c.UseGeneratedMappings(new Maps.GeneratedResolver());}).CreateMapper();
                try {collectionMapper.Map<Source,Dto>(new Source {Values=new[]{1,2}});return false;} catch(MeridianMappingLimitException){return true;}
            }
            public static bool Catalogued_Ambiguity_Is_Rejected()
            {
                var c=new MapperConfiguration(c=>c.UseGeneratedMappings(new Maps.GeneratedResolver(),new Resolver()));
                try {c.AssertConfigurationIsValid();return false;} catch(InvalidOperationException){return true;}
            }
            public static bool Opaque_Ambiguity_Is_Rejected_Before_Execution()
            {
                var a=new Resolver();var b=new Resolver();var c=new MapperConfiguration(c=>c.UseGeneratedMappings(a,b));c.AssertConfigurationIsValid();
                try {c.CreateMapper().Map<Source,Dto>(new Source());return false;}catch(InvalidOperationException){return a.Calls==0 && b.Calls==0;}
            }
            public static bool Nested_Explicit_Map_Preserves_Operation()
            {
                // Runtime source subclass map must win inside a generated root.
                var state=new object(); CheckAction.State=state;CheckAction.Actions=0;
                var mapper=new MapperConfiguration(c=>{c.UseGeneratedMappings(new Maps.GeneratedResolver());c.CreateMap<ChildSource,Dto>().AfterMap<CheckAction>();}).CreateMapper();
                var result=mapper.Map<Source,Dto>(new Source {Child=new ChildSource()},o=>{o.State=state;o.Items["key"]=42;});
                return result.Child.Value==99 && CheckAction.Actions==1;
            }
            public static bool Nested_Runtime_To_Generated_Preserves_Depth()
            {
                var mapper=new MapperConfiguration(c=>{c.DefaultMaxDepth=2;c.UseGeneratedMappings(new Maps.GeneratedResolver());c.CreateMap<Outer,OuterDto>();}).CreateMapper();
                var result=mapper.Map<Outer,OuterDto>(new Outer {Item=new Source {Child=new Source()}});
                return result.Item!=null && result.Item.Child==null;
            }
            public static bool Nested_Runtime_Cannot_Raise_Generated_Cap()
            {
                var mapper=new MapperConfiguration(c=>{c.UseGeneratedMappings(new Maps.GeneratedResolver());c.CreateMap<ChildSource,Dto>().MaxDepth(100);}).CreateMapper();
                var source=new Source {Child=new ChildSource {Child=new ChildSource {Child=new ChildSource {Child=new ChildSource()}}}};
                var result=mapper.Map<Source,Dto>(source);
                if(result.Child.Child.Child.Child!=null)return false;
                try {mapper.Map<Source,Dto>(new Source {Child=new ChildSource {Values=new[]{1,2,3,4}}});return false;}catch(MeridianMappingLimitException){return true;}
            }
            public static bool Mixed_Reference_Identity_Is_Preserved()
            {
                var mapper=new MapperConfiguration(c=>{c.UseGeneratedMappings(new Maps.GeneratedResolver());c.CreateMap<ChildSource,Dto>().PreserveReferences();}).CreateMapper();
                var source=new Source();source.Child=new ChildSource {Child=source};
                var mapped=mapper.Map<Source,Dto>(source);return ReferenceEquals(mapped,mapped.Child.Child);
            }
            public static bool Scoped_Resolver_And_Global_Transformer_Run_Once()
            {
                var configuration=new MapperConfiguration(c=>{c.UseGeneratedMappings(new Maps.GeneratedResolver());c.ValueTransformers.Add<int>(n=>n+1);c.CreateMap<ChildSource,Dto>().ForMember(d=>d.Value,o=>o.MapFrom<ScopedResolver>());});
                var a=new Services();var b=new Services();
                var first=configuration.CreateMapper(a).Map<Source,Dto>(new Source{Child=new ChildSource()},o=>o.Items["answer"]=40);
                var second=configuration.CreateMapper(b).Map<Source,Dto>(new Source{Child=new ChildSource()},o=>o.Items["answer"]=80);
                return first.Child.Value==41&&second.Child.Value==81&&a.Resolver.Calls==1&&b.Resolver.Calls==1;
            }
            public static bool Custom_ConfigurationProvider_Still_Compiles()=>new CustomProvider().CreateMapper()!=null;
            public static bool Global_Null_Collection_Policy_Is_Preserved()
            {
                var mapper=new MapperConfiguration(c=>{c.AllowNullCollections=true;c.UseGeneratedMappings(new Maps.GeneratedResolver());}).CreateMapper();
                return mapper.Map<Source,Dto>(new Source()).Values==null&&Maps.MapToDto(new Source()).Values.Length==0;
            }
            public static bool Legacy_Resolver_Preserves_Shallow_Aliases()
            {
                var mapper=new MapperConfiguration(c=>c.UseGeneratedMappings(new LegacyMaps.GeneratedResolver())).CreateMapper();
                var source=new LegacySource{Values=new[]{1,2}};return ReferenceEquals(source.Values,mapper.Map<LegacySource,LegacyDto>(source).Values);
            }
            public static bool Alternating_Traversal_Keeps_Generated_Caps()
            {
                var mapper=new MapperConfiguration(c=>{c.UseGeneratedMappings(new Maps.GeneratedResolver());c.CreateMap<ChildSource,Dto>().MaxDepth(100);}).CreateMapper();
                var root=new Source {Child=new ChildSource {Child=new Source {Child=new ChildSource {Child=new Source()}}}};
                if(mapper.Map<Source,Dto>(root).Child.Child.Child.Child!=null)return false;
                try {mapper.Map<Source,Dto>(new Source{Child=new ChildSource{Child=new Source{Values=new[]{1,2,3,4}}}});return false;}catch(MeridianMappingLimitException){return true;}
            }
            public static bool Runtime_PerMap_Depth_Still_Overrides_Global_Outside_Bridge()
            {
                var mapper=new MapperConfiguration(c=>{c.DefaultMaxDepth=1;c.CreateMap<Source,Dto>().MaxDepth(3);}).CreateMapper();
                var value=mapper.Map<Source,Dto>(new Source{Child=new Source {Child=new Source {Child=new Source()}}});
                return value.Child.Child!=null&&value.Child.Child.Child==null;
            }
            public static bool Existing_Unconfigured_Destination_Does_Not_Use_Resolver()
            {
                var resolver=new Resolver();var mapper=new MapperConfiguration(c=>c.UseGeneratedMappings(resolver)).CreateMapper();
                try {mapper.Map(new Source(),new Dto());return false;}catch(InvalidOperationException){return resolver.Calls==0;}
            }
            public static bool Included_Derived_Child_Uses_Runtime_Map()
            {
                var mapper=new MapperConfiguration(c=>{c.UseGeneratedMappings(new Maps.GeneratedResolver());c.CreateMap<Source,Dto>().Include<GrandChildSource,GrandChildDto>();c.CreateMap<GrandChildSource,GrandChildDto>().AfterMap((s,d)=>d.Value=123);}).CreateMapper();
                return mapper.Map<Outer,OuterDto>(new Outer {Item=new GrandChildSource()}).Item is GrandChildDto child&&child.Value==123;
            }
            public static bool Opaque_Descriptor_Sees_Inherited_Caps()
            {
                var resolver=new ForeignResolver();
                var mapper=new MapperConfiguration(c=>{c.UseGeneratedMappings(new Maps.GeneratedResolver(),resolver);c.CreateMap<ChildSource,Dto>();}).CreateMapper();
                mapper.Map<Source,Dto>(new Source{Child=new ChildSource{Child=new ForeignSource()}});
                return resolver.Seen.Depth==2&&resolver.Seen.MaxDepth==4&&resolver.Seen.MaxCollectionItems==3;
            }
            public static bool Global_Null_Setter_Policy_Is_Preserved()
            {
                var mapper=new MapperConfiguration(c=>{c.AllowNullDestinationValues=false;c.UseGeneratedMappings(new NullMaps.GeneratedResolver());}).CreateMapper();
                return mapper.Map<NullSource,NullDto>(new NullSource()).Name=="seed"&&NullMaps.MapToNullDto(new NullSource()).Name==null;
            }
            public static bool Custom_Mapper_Context_Bridge_Fails_Explicitly()
            {
                var mapper=System.Reflection.DispatchProxy.Create<IMapper,MapperProxy>();
                var context=new ResolutionContext(mapper);
                try{context.TryMapConfigured(new Source(),typeof(Source),typeof(Dto),0,out var _);return false;}catch(NotSupportedException){return true;}
            }
            public static bool Registration_Is_Snapshotted_And_Validated()
            {
                var expression=new MapperConfigurationExpression();expression.UseGeneratedMappings(new Maps.GeneratedResolver());
                var configuration=new MapperConfiguration(expression);expression.UseGeneratedMappings(new Resolver());
                configuration.AssertConfigurationIsValid();
                try{expression.UseGeneratedMappings(new IGeneratedMappingResolver[]{null});return false;}catch(ArgumentNullException){}
                return ((IGeneratedMappingConfigurationProvider)configuration).GeneratedResolvers.Count==1;
            }
            public static bool Duplicate_Catalog_Entries_Are_Rejected()
            {
                var configuration=new MapperConfiguration(c=>c.UseGeneratedMappings(new DuplicateCatalog()));
                try{configuration.AssertConfigurationIsValid();return false;}catch(InvalidOperationException){return true;}
            }
            public static bool Direct_Generated_Method_Remains_Independent()
            {
                var source=new Source {Value=42};source.Child=source;
                var result=Maps.MapToDto(source);return result.Value==42&&ReferenceEquals(result,result.Child);
            }
        }
        public class CheckAction : IMappingAction<ChildSource,Dto>
        {
            public static object State;
            public static int Actions;
            public void Process(ChildSource source, Dto destination, ResolutionContext context)
            {
                if(ReferenceEquals(State,context.State)&&(int)context.Items["key"]==42 && context.Depth==1) Actions++;
                destination.Value=99;
            }
        }
        public class ScopedResolver : Meridian.Mapping.Converters.IValueResolver<ChildSource,Dto,int>
        {
            public int Calls;
            public int Resolve(ChildSource source,Dto destination,int member,ResolutionContext context) {Calls++;return (int)context.Items["answer"];}
        }
        public class Services : IServiceProvider
        {
            public ScopedResolver Resolver=new ScopedResolver();
            public object GetService(Type type)=>type==typeof(ScopedResolver)?Resolver:null;
        }
        public class CustomProvider : IConfigurationProvider
        {
            public TypeMap FindTypeMap(Type s,Type d)=>null;
            public IMapper CreateMapper()=>new Mapper(this);
            public IMapper CreateMapper(IServiceProvider services)=>new Mapper(this,services);
            public void AssertConfigurationIsValid(){}
            public IReadOnlyCollection<TypeMap> GetAllTypeMaps()=>Array.Empty<TypeMap>();
            public bool AllowNullCollections=>false;
            public bool AllowNullDestinationValues=>true;
            public int DefaultMaxDepth=>64;
            public int DefaultMaxCollectionItems=>10000;
            public string GetMappingPlan<TSource,TDestination>()=>"";
            public string GetMappingPlan(Type source,Type destination)=>"";
        }
        public class LegacySource {public int[] Values {get;set;}}
        public class LegacyDto {public int[] Values {get;set;}}
        [GenerateMapper(typeof(LegacySource),typeof(LegacyDto),GenerateResolver=true)]
        public static partial class LegacyMaps {}
        public class ChildSource : Source { }
        public class GrandChildSource : ChildSource { }
        public class GrandChildDto : Dto { }
        public class DuplicateCatalog : Resolver, IGeneratedMappingDescriptorCatalog
        {
            private static readonly GeneratedMappingDescriptor Entry=new GeneratedMappingDescriptor(typeof(Source),typeof(Dto),(s,c)=>new Dto());
            public IReadOnlyList<GeneratedMappingDescriptor> GeneratedMappings=>new[]{Entry,Entry};
        }
        public class ForeignSource : Source { }
        public class ForeignResolver : IGeneratedMappingResolver
        {
            public ResolutionContext Seen;
            public bool TryGetMapping(Type source,Type destination,out GeneratedMappingDescriptor mapping)
            {
                mapping=source==typeof(ForeignSource)&&destination==typeof(Dto)?new GeneratedMappingDescriptor(source,destination,(s,c)=>{Seen=c;return new Dto();}):null;
                return mapping!=null;
            }
        }
        public class MapperProxy : System.Reflection.DispatchProxy
        {
            protected override object Invoke(System.Reflection.MethodInfo method,object[] args)=>throw new InvalidOperationException("No fresh operation may be started");
        }
        public class NullSource {public string Name {get;set;}}
        public class NullDto {public string Name {get;set;}="seed";}
        [GenerateMapper(typeof(NullSource),typeof(NullDto),Mode=GeneratedMappingMode.Structural,GenerateResolver=true)]
        public static partial class NullMaps {}
        """;
}
