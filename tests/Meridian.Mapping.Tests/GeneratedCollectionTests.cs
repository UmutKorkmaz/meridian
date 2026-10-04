namespace Meridian.Mapping.Tests;

public class GeneratedCollectionTests
{
    [Theory]
    [InlineData("ChildDto[]")] [InlineData("System.Collections.Generic.List<ChildDto>")]
    [InlineData("System.Collections.Generic.IEnumerable<ChildDto>")] [InlineData("System.Collections.Generic.IReadOnlyList<ChildDto>")]
    [InlineData("System.Collections.Generic.ICollection<ChildDto>")]
    public void List_Elements_Map(string destination) => GeneratedGraphTests.Proof($$"""
        public class Child { public int Value {get;set;} }
        public record ChildDto(long Value);
        public class Source { public System.Collections.Generic.List<Child> Values {get;set;}=new(); }
        public class Dto { public {{destination}} Values {get;set;}=default!; }
        [GenerateMapper(typeof(Child),typeof(ChildDto),Mode=GeneratedMappingMode.Structural)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps { public static bool Proof() { var s=new Source {Values=new() {new Child {Value=42},new Child {Value=99} } }; var d=MapToDto(s); var a=System.Linq.Enumerable.ToArray(d.Values); return a.Length==2 && a[0].Value==42 && a[1].Value==99 && !object.ReferenceEquals(s.Values,d.Values); } }
        """);

    [Fact]
    public void Dictionary_Duplicates_Last_Win() => GeneratedGraphTests.Proof("""
        public class Source { public System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int,int>> Values {get;set;}=new[] {new System.Collections.Generic.KeyValuePair<int,int>(7,42),new System.Collections.Generic.KeyValuePair<int,int>(7,99)}; }
        public class Dto { public System.Collections.Generic.Dictionary<long,long> Values {get;set;}=new(); }
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps { public static bool Proof() { var d=MapToDto(new Source()); return d.Values.Count==1 && d.Values[7L]==99L; } }
        """);

    [Theory]
    [InlineData("Legacy",false)] [InlineData("Structural",false)] [InlineData("Structural",true)]
    public void Null_Collections_Respect_Mode_Policy(string mode,bool allow) => GeneratedGraphTests.Proof(Fixture(mode, ", AllowNullCollections="+allow.ToString().ToLowerInvariant(), $$"""
        var d=MapToDto(new Source {Values=null}); return {{(mode=="Legacy" || allow ? "d.Values==null" : "d.Values!=null && System.Linq.Enumerable.Count(d.Values)==0")}};
        """));

    [Theory]
    [InlineData(9999)] [InlineData(10000)] [InlineData(10001)]
    public void Collection_Default_Boundaries(int count) => GeneratedGraphTests.Proof(Fixture("Structural","",$$"""
        var s=new Source {Values=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Range(0,{{count}}))};
        try { var d=MapToDto(s); return {{count}}<=10000 && System.Linq.Enumerable.Count(d.Values!)=={{count}} && !object.ReferenceEquals(s.Values,d.Values); }
        catch(MeridianMappingLimitException ex) { return {{count}}==10001 && ex.MaxAllowed==10000 && ex.ObservedValue==10001 && ex.Limit==MeridianMappingLimit.MaxCollectionItems; }
        """));

    [Fact]
    public void Legacy_Same_Type_Collection_Aliases() => GeneratedGraphTests.Proof(Fixture("Legacy","","var s=new Source {Values=new[] {42,99}}; return object.ReferenceEquals(s.Values,MapToDto(s).Values);"));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Streaming_Limit_And_Cancellation_Dispose_Enumerator(bool cancel) => GeneratedGraphTests.Proof($$"""
        public class Source { public System.Collections.Generic.IEnumerable<int> Values {get;set;}=default!; }
        public class Dto { public long[] Values {get;set;}=default!; }
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps {
            static int moved; static bool disposed; static System.Threading.CancellationTokenSource c=new();
            static System.Collections.Generic.IEnumerable<int> Stream() { try { for(int i=0;i<10;i++) { moved++; if({{cancel.ToString().ToLowerInvariant()}} && moved==3) c.Cancel(); yield return i; } } finally { disposed=true; } }
            public static bool Proof() { try { MapToDto(new Source {Values=Stream()},new GeneratedMappingOptions {MaxCollectionItems=2,CancellationToken=c.Token,State=new object()}); return false; } catch(System.Exception ex) { return moved==3 && disposed && {{(cancel ? "ex is System.OperationCanceledException" : "ex is MeridianMappingLimitException e && e.MaxAllowed==2 && e.ObservedValue==3")}}; } }
        }
        """);

    [Fact]
    public void Counted_Source_Is_Rejected_Before_Enumeration_Or_Destination_Allocation() => GeneratedGraphTests.Proof("""
        public class Counted : System.Collections.Generic.IReadOnlyCollection<int> {
            public int Count=>10001;
            public System.Collections.Generic.IEnumerator<int> GetEnumerator()=>throw new System.Exception("enumerated");
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
        }
        public class Source { public Counted Values {get;set;}=new(); }
        public class Dto { public int[] Values {get;set;}=default!; }
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps { public static bool Proof() { try { MapToDto(new Source()); return false; } catch(MeridianMappingLimitException ex) { return ex.MaxAllowed==10000 && ex.ObservedValue==10001 && ex.SourceType==typeof(Counted) && ex.DestinationType==typeof(int[]); } } }
        """);

    [Fact]
    public void Nullable_Nested_Elements_And_Collection_Constructor_Map() => GeneratedGraphTests.Proof("""
        public class Node { public int Value {get;set;} }
        public record NodeDto(long Value);
        public class Source { public Node?[] Values {get;set;}=new Node?[] {new Node {Value=42},null}; }
        public record Dto(System.Collections.Generic.IReadOnlyList<NodeDto?> Values);
        [GenerateMapper(typeof(Node),typeof(NodeDto),Mode=GeneratedMappingMode.Structural)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps { public static bool Proof() { var d=MapToDto(new Source()); return d.Values.Count==2 && d.Values[0]!.Value==42 && d.Values[1]==null; } }
        """);

    [Fact]
    public void Streaming_Oversize_Item_Is_Not_Mapped() => GeneratedGraphTests.Proof("""
        public class Node { public static int Reads; public int Value {get { Reads++; return 42; }} }
        public class NodeDto { public long Value {get;set;} }
        public class Source { public System.Collections.Generic.IEnumerable<Node> Values {get;set;}=default!; }
        public class Dto { public NodeDto[] Values {get;set;}=default!; }
        [GenerateMapper(typeof(Node),typeof(NodeDto),Mode=GeneratedMappingMode.Structural)]
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.Structural)]
        public static partial class Maps {
            static bool disposed;
            static System.Collections.Generic.IEnumerable<Node> Stream() { try { for(int i=0;i<10001;i++) yield return new Node(); } finally { disposed=true; } }
            public static bool Proof() { try { MapToDto(new Source {Values=Stream()}); return false; } catch(MeridianMappingLimitException ex) { return ex.ObservedValue==10001 && Node.Reads==10000 && disposed; } }
        }
        """);

    private static string Fixture(string mode,string options,string body) => $$"""
        public class Source { public System.Collections.Generic.IEnumerable<int>? Values {get;set;} }
        public class Dto { public System.Collections.Generic.IEnumerable<int>? Values {get;set;} }
        [GenerateMapper(typeof(Source),typeof(Dto),Mode=GeneratedMappingMode.{{mode}}{{options}})]
        public static partial class Maps { public static bool Proof() { {{body}} } }
        """;
}
