using Microsoft.CodeAnalysis;

namespace Meridian.Mapping.Tests;

public class GeneratedGraphTests
{
    [Fact]
    public void Nested_Different_Shapes_Map() => Proof("""
        public class Child { public int Value {get;set;} }
        public record ChildDto(long Value);
        public class Source { public Child Child {get;set;} = new(); }
        public record Dto(ChildDto Child);
        [GenerateMapper(typeof(Child),typeof(ChildDto),Mode=GeneratedMappingMode.Structural)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps { public static bool Proof() { var s=new Source {Child=new Child {Value=42}}; var d=MapToDto(s); return d.Child.Value==42L; } }
        """);

    [Theory]
    [InlineData(63)] [InlineData(64)] [InlineData(65)]
    public void Depth_Default_Boundaries(int count) => Proof(Graph("", $$"""
        var s=new Source {Value=1}; var p=s;
        for(int i=1;i<{{count}};i++) { p.Next=new Source {Value=i+1}; p=p.Next; }
        var d=MapToDto(s); int seen=0; while(d!=null) { seen++; if(d.Value!=seen) return false; d=d.Next; }
        return seen==System.Math.Min({{count}},64);
        """));

    [Fact]
    public void Cyclic_Mutable_Graph_And_Operation_Isolation() => Proof(Graph(", PreserveReferences=true", "var s=new Source {Value=42}; s.Next=s; var a=MapToDto(s); var b=MapToDto(s); return a.Value==42 && object.ReferenceEquals(a,a.Next) && !object.ReferenceEquals(a,b);"));

    [Fact]
    public void Depth_Cap_Precedes_Reference_Cache() => Proof(Graph(", PreserveReferences=true", "var s=new Source {Value=42}; s.Next=s; var d=MapToDto(s,new GeneratedMappingOptions {MaxDepth=1}); return d.Value==42 && d.Next==null;"));

    [Fact]
    public void Diamond_Identity_And_Equal_Value_Distinct_Nodes() => Proof("""
        public record Node { public int Value {get;set;} }
        public class NodeDto { public int Value {get;set;} }
        public class Source { public Node Left {get;set;}=new(); public Node Right {get;set;}=new(); }
        public class Dto { public NodeDto Left {get;set;}=new(); public NodeDto Right {get;set;}=new(); }
        [GenerateMapper(typeof(Node),typeof(NodeDto),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
        public static partial class Maps { public static bool Proof() { var n=new Node {Value=42}; var shared=MapToDto(new Source {Left=n,Right=n}); var distinct=MapToDto(new Source {Left=n,Right=new Node {Value=42}}); return shared.Left.Value==42 && object.ReferenceEquals(shared.Left,shared.Right) && distinct.Left.Value==42 && distinct.Right.Value==42 && !object.ReferenceEquals(distinct.Left,distinct.Right); } }
        """);

    [Fact]
    public void Constructor_Only_Cycle_With_Reference_Preservation_Is_Diagnostic()
    {
        var result=GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public Source? Next {get;set;} }
            public class Dto { public Dto? Next {get;} public Dto(Dto? next) { Next=next; } }
            [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
            public static partial class Maps {}
            """);
        Assert.Contains(result.Diagnostics,d=>d.Id=="MERIDIANGEN005");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(),d=>d.Severity==DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("MaxDepth=0")] [InlineData("MaxDepth=-1")] [InlineData("MaxCollectionItems=0")] [InlineData("MaxCollectionItems=-1")]
    public void Options_Validate_Before_Null_Source(string option) => Proof(Graph("", "try { MapToDto(null!,new GeneratedMappingOptions {"+option+"}); return false; } catch(System.ArgumentOutOfRangeException) { return true; }"));

    [Fact]
    public void Precancelled_Operation_Does_Not_Read_Source() => Proof(Graph("", "using var c=new System.Threading.CancellationTokenSource(); c.Cancel(); try { MapToDto(new Source(),new GeneratedMappingOptions {CancellationToken=c.Token}); return false; } catch(System.OperationCanceledException) { return true; }"));

    [Theory]
    [InlineData("init")] [InlineData("required")]
    public void Recursive_Initializer_With_Preservation_Is_Diagnostic(string kind)
    {
        var property = kind == "init" ? "public Dto? Next {get;init;}" : "public required Dto? Next {get;set;}";
        var result=GeneratorTestHarness.Compile("#nullable enable\nusing Meridian.Mapping; public class Source { public Source? Next {get;set;} } public class Dto {"+property+"} [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)] public static partial class Maps {}");
        Assert.Contains(result.Diagnostics,d=>d.Id=="MERIDIANGEN005");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(),d=>d.Severity==DiagnosticSeverity.Error);
    }

    [Fact]
    public void Required_Scalar_Allows_Mutable_Cycle() => Proof(Graph(", PreserveReferences=true",
        "var s=new Source {Value=42}; s.Next=s; var d=MapToDto(s); return d.Value==42 && object.ReferenceEquals(d,d.Next);")
        .Replace("public class Dto { public int Value", "public class Dto { public required int Value"));

    [Fact]
    public void Attribute_Depth_And_Option_Override() => Proof(Graph(", MaxDepth=2",
        "var s=new Source {Value=1,Next=new Source {Value=2,Next=new Source {Value=3}}}; var a=MapToDto(s); var b=MapToDto(s,new GeneratedMappingOptions {MaxDepth=3}); return a.Next!.Value==2 && a.Next.Next==null && b.Next!.Next!.Value==3;"));

    [Fact]
    public void Cache_Separates_Destination_Types() => Proof("""
        public class Node { public int Value {get;set;} }
        public class First { public int Value {get;set;} }
        public class Second { public long Value {get;set;} }
        public class Source { public Node Left {get;set;}=new(); public Node Right {get;set;}=new(); }
        public class Dto { public First Left {get;set;}=new(); public Second Right {get;set;}=new(); }
        [GenerateMapper(typeof(Node),typeof(First),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
        [GenerateMapper(typeof(Node),typeof(Second),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps { public static bool Proof() { var n=new Node {Value=42}; var d=MapToDto(new Source {Left=n,Right=n}); return d.Left.Value==42 && d.Right.Value==42L; } }
        """);

    [Fact]
    public void Reference_Preservation_Does_Not_Cache_Uninitialized_Struct_Copies() => Proof("""
        public class Node { public int Value {get;set;} }
        public struct NodeDto { public int Value {get;set;} }
        public class Source { public Node Left {get;set;}=new(); public Node Right {get;set;}=new(); }
        public class Dto { public NodeDto Left {get;set;} public NodeDto Right {get;set;} }
        [GenerateMapper(typeof(Node),typeof(NodeDto),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps { public static bool Proof() { var n=new Node {Value=42}; var d=MapToDto(new Source {Left=n,Right=n}); return d.Left.Value==42 && d.Right.Value==42; } }
        """);

    [Fact]
    public void Noncyclic_Init_Nested_Mapping_With_Preservation_Works() => Proof("""
        public class Node { public int Value {get;set;} }
        public record NodeDto(long Value);
        public class Source { public Node Child {get;set;}=new(); }
        public class Dto { public required NodeDto Child {get;init;} }
        [GenerateMapper(typeof(Node),typeof(NodeDto),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural,PreserveReferences=true)]
        public static partial class Maps { public static bool Proof() => MapToDto(new Source {Child=new Node {Value=42}}).Child.Value==42; }
        """);

    [Theory]
    [InlineData("__MeridianMap0")]
    [InlineData("GeneratedMappingContext")]
    public void Reserved_Traversal_Method_Names_Are_Diagnosed(string name)
    {
        var result=GeneratorTestHarness.Compile("using Meridian.Mapping; public class Source {} public class Dto {} [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural,MethodName=\""+name+"\")] public static partial class Maps {}");
        Assert.Contains(result.Diagnostics,d=>d.Id=="MERIDIANGEN004");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(),d=>d.Severity==DiagnosticSeverity.Error);
    }

    [Fact]
    public void Depth_And_Cycle_Identity_Match_Runtime_Supported_Intersection() => Proof(Graph(", PreserveReferences=true",
        "var s=new Source {Value=42}; s.Next=s; var configuration=new MapperConfiguration(c=>c.CreateMap<Source,Dto>().PreserveReferences()); var runtime=configuration.CreateMapper().Map<Source,Dto>(s); var generated=MapToDto(s); return runtime.Value==generated.Value && object.ReferenceEquals(runtime,runtime.Next) && object.ReferenceEquals(generated,generated.Next);"));

    [Theory]
    [InlineData("struct", "Structural", false)]
    [InlineData("class", "Structural", false)]
    [InlineData("struct", "Legacy", true)]
    public void Value_Source_Kinds_Compile_Strictly(string destinationKind, string mode, bool mixed)
    {
        var fixture = "public struct Source { public int Value {get;set;} } public " + destinationKind +
            " Dto { public int Value {get;set;} } [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode."+mode+")] ";
        if (mixed) fixture += "[GenerateMapper(typeof(Other),typeof(OtherDto),Mode=GeneratedMappingMode.Structural)] ";
        fixture += "public static partial class Maps { public static bool Proof() => MapToDto(new Source {Value=42}).Value==42; } public class Other {} public class OtherDto {}";
        Proof(fixture);
    }

    [Fact]
    public void Nullable_Options_Existing_Signature_Is_Diagnosed()
    {
        var result=GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source {} public class Dto {}
            [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
            public static partial class Maps { public static Dto MapToDto(Source source, GeneratedMappingOptions? options) => new(); }
            """);
        Assert.Contains(result.Diagnostics,d=>d.Id=="MERIDIANGEN004");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(),d=>d.Severity==DiagnosticSeverity.Error);
    }

    [Fact]
    public void One_Argument_Wrapper_Resolves_Typed_Options_Overload() => Proof("""
        public class Source { public int Value {get;set;} }
        public class Dto { public int Value {get;set;} }
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps {
            public static Dto MapToDto(Source source, string options) => new() {Value=-1};
            public static bool Proof() => MapToDto(new Source {Value=42}).Value==42;
        }
        """);

    [Theory]
    [InlineData("GeneratedMappingContext")]
    [InlineData("__MeridianMap0")]
    public void Container_Name_Colliding_With_Traversal_Helper_Is_Diagnosed(string name)
    {
        var result=GeneratorTestHarness.Compile("using Meridian.Mapping; public class Source {} public class Dto {} [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)] public static partial class "+name+" {}");
        Assert.Contains(result.Diagnostics,d=>d.Id=="MERIDIANGEN004");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(),d=>d.Severity==DiagnosticSeverity.Error);
    }

    internal static string Graph(string options,string body) => $$"""
        public class Source { public int Value {get;set;} public Source? Next {get;set;} }
        public class Dto { public int Value {get;set;} public Dto? Next {get;set;} }
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural{{options}})]
        public static partial class Maps { public static bool Proof() { {{body}} } }
        """;

    internal static void Proof(string fixture)
    {
        var result=GeneratorTestHarness.Compile("#nullable enable\nusing Meridian.Mapping;\n"+fixture);
        Assert.DoesNotContain(result.Diagnostics,d=>d.Severity==DiagnosticSeverity.Error);
        using var stream=new MemoryStream();
        var emit=result.Compilation.WithOptions(result.Compilation.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error)).Emit(stream);
        Assert.True(emit.Success,string.Join("\n",emit.Diagnostics));
        Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result,"Maps","Proof"));
    }
}
