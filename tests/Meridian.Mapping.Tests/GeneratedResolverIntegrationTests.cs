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
