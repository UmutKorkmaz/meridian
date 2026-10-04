using Microsoft.CodeAnalysis;

namespace Meridian.Mapping.Tests;

public class GeneratedConstructionTests
{
    [Fact]
    public void Positional_Record_Maps() => AssertValue(
        "public record Dto(string Name, int Age);", "Ada:37",
        "var dto = MapToDto(new Source { Name = \"Ada\", Age = 37 }); return dto.Name + \":\" + dto.Age;");

    [Fact]
    public void Required_Init_Members_Are_Assigned() => AssertValue(
        "public class Dto { public required string Name { get; init; } public required int Age { get; init; } }", "Ada:37",
        "var dto = MapToDto(new Source { Name = \"Ada\", Age = 37 }); return dto.Name + \":\" + dto.Age;");

    [Fact]
    public void Optional_Ctor_Default_Is_Honored() => AssertValue(
        "public class Dto { public string Label { get; } public Dto(string name, int missing = 73) { Label = name + \":\" + missing; } }", "Ada:73",
        "return MapToDto(new Source { Name = \"Ada\" }).Label;");

    [Fact]
    public void Missing_Required_Ctor_Is_Diagnostic() => AssertConstructionError(
        "public class Dto { public Dto(string missing) {} }");

    [Fact]
    public void Ambiguous_Constructors_Are_Diagnosed() => AssertConstructionError(
        "public class Dto { public Dto(string name) {} public Dto(int age) {} }");

    [Fact]
    public void Widest_Resolvable_Constructor_Wins() => AssertValue(
        "public class Dto { public string Label { get; } public Dto() { Label = \"wrong\"; } public Dto(string name, int age) { Label = name + \":\" + age; } public Dto(string name, int age, string missing) { Label = \"wrong\"; } }", "Ada:37",
        "return MapToDto(new Source { Name = \"Ada\", Age = 37 }).Label;");

    [Fact]
    public void Constructor_Consumed_Init_Member_Is_Not_Assigned_Twice() => AssertValue(
        "public class Dto { private string name; public string Name { get => name; init => throw new System.Exception(\"double write\"); } public Dto(string name) { this.name = name + \"!\"; } }", "Ada!",
        "return MapToDto(new Source { Name = \"Ada\" }).Name;");

    [Fact]
    public void Required_Member_Still_Needs_Initializer_Without_SetsRequiredMembers() => AssertValue(
        "public class Dto { public required string Name { get; init; } public Dto(string name) { Name = name; } }", "Ada",
        "return MapToDto(new Source { Name = \"Ada\" }).Name;");

    [Fact]
    public void Struct_Constructor_Receives_Source() => AssertValue(
        "public readonly record struct Dto(string Name, int Age);", "Ada:37",
        "var dto = MapToDto(new Source { Name = \"Ada\", Age = 37 }); return dto.Name + \":\" + dto.Age;");

    [Fact]
    public void Legacy_Constructor_Selection_Remains_No_Argument() => AssertValue(
        "public class Dto { public string Label { get; } public Dto() { Label = \"legacy\"; } public Dto(string name) { Label = name; } }", "legacy",
        "return MapToDto(new Source { Name = \"Ada\" }).Label;", "Legacy");

    [Theory]
    [InlineData("internal")]
    [InlineData("private")]
    [InlineData("protected")]
    public void Structural_Only_Uses_Public_Constructors(string access) => AssertConstructionError(
        "public class Dto { " + access + " Dto(string name) {} }");

    [Theory]
    [InlineData("int[]")]
    public void Constructor_Does_Not_Bypass_Pending_Structural_Support(string type)
    {
        var result = GeneratorTestHarness.Compile($$"""
            using Meridian.Mapping;
            public class Source { public {{type}} Value { get; set; } }
            public record Dto({{type}} Value);
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps {}
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN005");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Optional_Default_Literals_Are_Typed_And_Escaped()
    {
        var result = GeneratorTestHarness.Compile(Fixture("""
            public class Dto {
                public string Label { get; }
                public Dto(string name, decimal price = 1.25m, System.DayOfWeek day = System.DayOfWeek.Friday, string text = "a\"b") {
                    Label = name + ":" + price.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + day + ":" + text;
                }
            }
            """, "public static string Proof() => MapToDto(new Source { Name = \"Ada\" }).Label;"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("Ada:1.25:Friday:a\"b", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Assignable_Constructor_Reference_Is_Mapped()
    {
        var result = GeneratorTestHarness.Compile(Fixture("public record Dto(object Name);",
            "public static string Proof() => (string)MapToDto(new Source { Name = \"Ada\" }).Name;"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("Ada", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Array_Destination_Is_Construction_Diagnostic()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { }
            [GenerateMapper(typeof(Source), typeof(int[]), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps {}
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN003");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Optional_Different_Type_Parameter_Does_Not_Consume_String_Initializer() => AssertValue(
        "public class Dto { public string Name { get; init; } = \"ctor\"; public Dto(int name = 73) {} }", "Ada",
        "return MapToDto(new Source { Name = \"Ada\" }).Name;");

    [Fact]
    public void Optional_Different_Type_Parameter_Does_Not_Consume_Integer_Initializer() => AssertValue(
        "public class Dto { public int Age { get; init; } public Dto(string age = \"ctor\") { Age = 7; } }", "42",
        "return MapToDto(new Source { Age = 42 }).Age.ToString();");

    [Fact]
    public void Bound_Different_Type_Parameter_Does_Not_Consume_Initializer() => AssertValue(
        "public class Dto { public string Name { get; init; } = \"ctor\"; public Dto(object name) {} }", "Ada",
        "return MapToDto(new Source { Name = \"Ada\" }).Name;");

    [Fact]
    public void Different_Source_Binding_Does_Not_Consume_Initializer()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public string Name { get; set; } = "Ada"; public string Other { get; set; } = "constructor"; }
            public class Dto { public string Name { get; init; } = "ctor"; public string Observed { get; } public Dto(string name) { Observed = name; } }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            [MapMember("name", "Other")]
            public static partial class Maps { public static string Proof() { var dto = MapToDto(new Source()); return dto.Name + ":" + dto.Observed; } }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("Ada:constructor", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Optional_Absent_Record_Property_Is_Satisfied_By_Default() => AssertValue(
        "public record Dto(string Name, int Missing = 73);", "Ada:73",
        "var dto = MapToDto(new Source { Name = \"Ada\" }); return dto.Name + \":\" + dto.Missing;");

    [Fact]
    public void Optional_Default_Does_Not_Satisfy_Required_Initializer()
    {
        var result = GeneratorTestHarness.Compile(Fixture(
            "public class Dto { public required int Missing { get; init; } public Dto(int missing = 73) {} }", ""));
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN002");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("public record Dto(string Name);")]
    [InlineData("public readonly record struct Dto(string Name);")]
    public void Lowercase_Source_Maps_Positional_Record_Through_Constructor(string destination)
    {
        var result = GeneratorTestHarness.Compile($$"""
            using Meridian.Mapping;
            public class Source { public string name { get; set; } = "Ada"; }
            {{destination}}
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps { public static string Proof() => MapToDto(new Source()).Name; }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("Ada", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Theory]
    [InlineData("public class Dto { public required string Name { get; init; } public Dto(string Name) {} }")]
    [InlineData("public class Dto { public int Name { get; init; } public Dto(string Name) {} }")]
    public void Lowercase_Constructor_Binding_Does_Not_Bypass_Required_Or_Different_Type_Initializer(string destination)
    {
        var result = GeneratorTestHarness.Compile($$"""
            using Meridian.Mapping;
            public class Source { public string name { get; set; } = "Ada"; }
            {{destination}}
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps {}
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN002");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    private static void AssertConstructionError(string destination)
    {
        var result = GeneratorTestHarness.Compile(Fixture(destination, ""));
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN003" && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    private static void AssertValue(string destination, string expected, string proof, string mode = "Structural")
    {
        var result = GeneratorTestHarness.Compile(Fixture(destination, "public static string Proof() { " + proof + " }", mode));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(expected, GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    private static string Fixture(string destination, string proof, string mode = "Structural") => $$"""
        using Meridian.Mapping;
        public class Source { public string Name { get; set; } = ""; public int Age { get; set; } }
        {{destination}}
        [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.{{mode}})]
        public static partial class Maps { {{proof}} }
        """;
}
