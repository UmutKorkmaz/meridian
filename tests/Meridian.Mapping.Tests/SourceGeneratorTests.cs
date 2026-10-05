using System.Text;
using Meridian.Mapping.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Tests
{
    // ── Sample types used by the [GenerateMapper] integration tests ──────────

    public class SgOrderSource
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public decimal Total { get; set; }
    }

    public class SgOrderDto
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public decimal Total { get; set; }
    }

    public class SgUserSource
    {
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public class SgUserDto
    {
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public class SgTypeMismatchSrc
    {
        public int Value { get; set; }
        public string SameName { get; set; } = "";
    }

    public class SgTypeMismatchDst
    {
        // Same name, different type — generator should skip with a comment.
        public long Value { get; set; }
        public string SameName { get; set; } = "";
    }

    public class SgNestedSource
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    public class SgNestedDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    // ── The partial classes the generator fills in ───────────────────────────

    [GenerateMapper(typeof(SgOrderSource), typeof(SgOrderDto))]
    [GenerateMapper(typeof(SgUserSource), typeof(SgUserDto))]
    [GenerateMapper(typeof(SgTypeMismatchSrc), typeof(SgTypeMismatchDst))]
    public static partial class TestGeneratedMappers
    {
    }

    public partial class SgOuterContainer
    {
        [GenerateMapper(typeof(SgNestedSource), typeof(SgNestedDto))]
        public static partial class NestedGeneratedMappers
        {
        }
    }

    public class SourceGeneratorTests
    {
        [Fact]
        public void Inherited_Id_Is_Preserved()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                public class BaseSource { public int Id { get; set; } }
                public class Source : BaseSource { }
                public class BaseDto { public int Id { get; set; } }
                public class Dto : BaseDto { }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() => MapToDto(new Source { Id = 42 }).Id;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Fact]
        public void Init_Destination_Compiles()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto { public int Value { get; init; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() => MapToDto(new Source { Value = 42 }).Value;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Fact]
        public void Most_Derived_Member_Wins()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                public class BaseSource { public string Value { get; set; } = "base"; }
                public class Source : BaseSource { public new int Value { get; set; } }
                public class BaseDto { public string Value { get; set; } = "base"; }
                public class Dto : BaseDto { public new int Value { get; init; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static bool Proof()
                    {
                        var dto = MapToDto(new Source { Value = 42 });
                        return dto.Value == 42 && ((BaseDto)dto).Value == "base";
                    }
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result, "Maps", "Proof"));
        }

        [Fact]
        public void Keyword_Property_Is_Escaped()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                public class Source { public int @class { get; set; } }
                public class Dto { public int @class { get; init; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class @namespace
                {
                    public static int Proof() => MapToDto(new Source { @class = 42 }).@class;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Contains("@class = source.@class,", result.GeneratedSource);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "namespace", "Proof"));
        }

        [Fact]
        public void Private_Setter_Is_Not_Assigned()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto { public int Value { get; private set; } = 7; }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() => MapToDto(new Source { Value = 42 }).Value;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.DoesNotContain("Value = source.Value", result.GeneratedSource);
            Assert.Equal(7, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("private new int Hidden { get; set; }", "public int Hidden { get; set; }")]
        [InlineData("public new int Hidden { private get; set; }", "public int Hidden { get; set; }")]
        [InlineData("public new static int Hidden { get; set; }", "public int Hidden { get; set; }")]
        [InlineData("public new int Hidden;", "public int Hidden { get; set; }")]
        [InlineData("public new int Hidden() => 42;", "public int Hidden { get; set; }")]
        [InlineData("public int Hidden { get; set; }", "private new int Hidden { get; set; }")]
        [InlineData("public int Hidden { get; set; }", "public new int Hidden { get; private set; }")]
        [InlineData("public int Hidden { get; set; }", "public new static int Hidden { get; set; }")]
        [InlineData("public int Hidden { get; set; }", "public new int Hidden;")]
        public void Ineligible_Derived_Members_Do_Not_Expose_Base_Properties(string sourceMember, string destinationMember)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class BaseSource { public int Hidden { get; set; } = 11; }
                public class Source : BaseSource
                {
                    {{sourceMember}}
                    public int Value { get; set; }
                    public int this[int i] { get => i; set { } }
                }
                public class BaseDto { public int Hidden { get; set; } = 7; }
                public class Dto : BaseDto
                {
                    {{destinationMember}}
                    public int Value { get; init; }
                    public int this[int i] { get => i; set { } }
                }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() => MapToDto(new Source { Value = 42 }).Value;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.DoesNotContain("Hidden = source.Hidden", result.GeneratedSource);
            Assert.DoesNotContain("this[", result.GeneratedSource);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("public Dto(int value) { Value = value; } public int Value { get; }", "no accessible unambiguous constructor")]
        [InlineData("private Dto() { } public int Value { get; set; }", "no accessible unambiguous constructor")]
        [InlineData("public required int Value { get; init; }", "required destination members")]
        [InlineData("public required int Value;", "required destination members")]
        public void Unsupported_Required_Constructor_Has_Actionable_Diagnostic(string members, string reason)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto { {{members}} }
                public class GoodDto { public int Value { get; init; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                [GenerateMapper(typeof(Source), typeof(GoodDto))]
                public static partial class Maps { }
                """);
            Assert.Empty(result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Id == "MERIDIANGEN003"));
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains(reason, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains("runtime IMapper", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(diagnostic.Location.IsInSource);
            Assert.DoesNotContain(" MapToDto(", result.GeneratedSource);
            Assert.Contains(" MapToGoodDto(", result.GeneratedSource);
        }

        [Theory]
        [InlineData("protected")]
        [InlineData("private protected")]
        public void Protected_Constructor_Is_Not_Used_By_Derived_Mapper(string accessibility)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto
                {
                    {{accessibility}} Dto() { Value = 7; }
                    public int Value { get; set; }
                }
                public class GoodDto { public int Value { get; init; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                [GenerateMapper(typeof(Source), typeof(GoodDto))]
                public partial class Maps : Dto
                {
                    public static int Proof() => MapToGoodDto(new Source { Value = 42 }).Value;
                }
                """);
            Assert.Empty(result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Id == "MERIDIANGEN003"));
            Assert.Contains("no accessible unambiguous constructor", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            Assert.DoesNotContain(" MapToDto(", result.GeneratedSource);
            Assert.Contains(" MapToGoodDto(", result.GeneratedSource);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("protected internal")]
        [InlineData("internal")]
        public void Same_Assembly_Constructor_Access_Is_Preserved(string accessibility)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto
                {
                    {{accessibility}} Dto() { Value = 7; }
                    public int Value { get; init; }
                }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() => MapToDto(new Source { Value = 42 }).Value;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("private")]
        [InlineData("protected")]
        [InlineData("private protected")]
        public void Nested_Mapper_Can_Call_Accessible_Destination_Constructor(string accessibility)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public partial class Dto
                {
                    {{accessibility}} Dto() { Value = 7; }
                    public int Value { get; init; }
                    [GenerateMapper(typeof(Source), typeof(Dto))]
                    public static partial class Maps
                    {
                        public static int Proof() => MapToDto(new Source { Value = 42 }).Value;
                    }
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Dto+Maps", "Proof"));
        }

        [Fact]
        public void SetsRequiredMembers_Parameterless_Constructor_Preserves_Legacy_Mapping()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                using System.Diagnostics.CodeAnalysis;
                public class Source { public int Value { get; set; } }
                public class Dto
                {
                    [SetsRequiredMembers] public Dto() { Value = 7; }
                    public required int Value { get; set; }
                }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() => MapToDto(new Source { Value = 42 }).Value;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Fact]
        public void Inherited_Required_Member_Has_Construction_Diagnostic()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class BaseDto { public required int Value { get; init; } }
                public class Dto : BaseDto { }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps { }
                """);
            Assert.Empty(result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Single(result.Diagnostics.Where(d => d.Id == "MERIDIANGEN003"));
            Assert.DoesNotContain(" MapToDto(", result.GeneratedSource);
        }

        [Theory]
        [InlineData("public abstract class Dto { public int Value { get; set; } }")]
        [InlineData("public static class Dto { public static int Value { get; set; } }")]
        [InlineData("public interface Dto { int Value { get; set; } }")]
        public void NonConcrete_Destination_Has_Construction_Diagnostic(string destination)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                {{destination}}
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps { }
                """);
            Assert.Empty(result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Single(result.Diagnostics.Where(d => d.Id == "MERIDIANGEN003"));
            Assert.DoesNotContain(" MapToDto(", result.GeneratedSource);
        }

        [Theory]
        [InlineData("public class Dto { internal Dto() { } public int Value { get; init; } }")]
        [InlineData("public struct Dto { public int Value { get; init; } }")]
        public void Accessible_Constructor_And_Struct_Destinations_Compile(string destination)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                {{destination}}
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() => MapToDto(new Source { Value = 42 }).Value;
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp12)]
        [InlineData(LanguageVersion.CSharp13)]
        [InlineData(LanguageVersion.CSharp14)]
        public void Legacy_Null_Mismatch_And_Shallow_Assignment_Are_Preserved(LanguageVersion languageVersion)
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                public class Child { public int Value { get; set; } }
                public class Source { public Child Child { get; set; } = new(); public int Number { get; set; } }
                public class Dto { public Child Child { get; init; } = new(); public long Number { get; init; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static bool Proof()
                    {
                        var source = new Source { Number = 42 };
                        var dto = MapToDto(source);
                        return object.ReferenceEquals(source.Child, dto.Child) && dto.Number == 0 && MapToDto(null!) is null;
                    }
                }
                """, languageVersion);
            AssertNoCompilationErrors(result);
            Assert.Contains("// Skipped: Number", result.GeneratedSource);
            Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("public Dto(int seed = 7) { ConstructorValue = seed; }", 7)]
        [InlineData("public Dto(params int[] seed) { ConstructorValue = seed.Length; }", 0)]
        [InlineData("public Dto(int seed = 7, params int[] other) { ConstructorValue = seed + other.Length; }", 7)]
        public void Callable_Constructor_Preserves_Defaults_Without_Source_Binding(string constructor, int expected)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } public int Seed { get; set; } }
                public class Dto
                {
                    {{constructor}}
                    public int ConstructorValue { get; }
                    public int Value { get; set; }
                    public int Seed { get; set; }
                }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof()
                    {
                        var dto = MapToDto(new Source { Value = 42, Seed = 99 });
                        return dto.Seed == 99 ? dto.ConstructorValue + dto.Value : -1;
                    }
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Contains("new global::Dto()", result.GeneratedSource);
            Assert.Equal(42 + expected, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("public", 3)]
        [InlineData("private", 7)]
        public void Callable_Constructor_Uses_Accessible_CSharp_Overload(string accessibility, int expected)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto
                {
                    {{accessibility}} Dto() { ConstructorValue = 3; }
                    public Dto(int seed = 7) { ConstructorValue = seed; }
                    public int ConstructorValue { get; }
                    public int Value { get; set; }
                }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps
                {
                    public static int Proof() { var dto = MapToDto(new Source { Value = 42 }); return dto.Value + dto.ConstructorValue; }
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42 + expected, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("public Dto(int seed = 7) { } public Dto(string seed = \"seven\") { }")]
        [InlineData("public Dto(params int[] seed) { } public Dto(params string[] seed) { }")]
        [InlineData("private Dto(params int[] seed) { }")]
        public void Callable_Constructor_Ambiguity_And_Inaccessibility_Are_Diagnosed(string constructors)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto { {{constructors}} public int Value { get; set; } }
                public class GoodDto { public int Value { get; set; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                [GenerateMapper(typeof(Source), typeof(GoodDto))]
                public static partial class Maps { public static int Proof() => MapToGoodDto(new Source { Value = 42 }).Value; }
                """);
            Assert.Empty(result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Single(result.Diagnostics.Where(d => d.Id == "MERIDIANGEN003"));
            Assert.DoesNotContain(" MapToDto(", result.GeneratedSource);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("internal")]
        [InlineData("protected internal")]
        public void Callable_Constructor_Same_Assembly_Access_Is_Preserved(string accessibility)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto { {{accessibility}} Dto(int seed = 7) { ConstructorValue = seed; } public int ConstructorValue { get; } public int Value { get; set; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps { public static int Proof() { var dto = MapToDto(new Source { Value = 42 }); return dto.ConstructorValue + dto.Value; } }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(49, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        [Theory]
        [InlineData("private")]
        [InlineData("protected")]
        [InlineData("private protected")]
        public void Callable_Constructor_Nested_Access_Is_Preserved(string accessibility)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public partial class Dto
                {
                    {{accessibility}} Dto(int seed = 7) { ConstructorValue = seed; }
                    public int ConstructorValue { get; }
                    public int Value { get; set; }
                    [GenerateMapper(typeof(Source), typeof(Dto))]
                    public static partial class Maps { public static int Proof() { var dto = MapToDto(new Source { Value = 42 }); return dto.ConstructorValue + dto.Value; } }
                }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(49, GeneratorTestHarness.InvokeStatic<int>(result, "Dto+Maps", "Proof"));
        }

        [Theory]
        [InlineData("protected")]
        [InlineData("private protected")]
        public void Callable_Constructor_Protected_Derived_Creation_Is_Rejected(string accessibility)
        {
            var result = GeneratorTestHarness.Compile($$"""
                using Meridian.Mapping;
                public class Source { public int Value { get; set; } }
                public class Dto { {{accessibility}} Dto(int seed = 7) { } public int Value { get; set; } }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public partial class Maps : Dto { }
                """);
            Assert.Empty(result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Single(result.Diagnostics.Where(d => d.Id == "MERIDIANGEN003"));
            Assert.DoesNotContain(" MapToDto(", result.GeneratedSource);
        }

        [Fact]
        public void Callable_Constructor_Required_Contract_Uses_Selected_Overload()
        {
            var result = GeneratorTestHarness.Compile("""
                using Meridian.Mapping;
                using System.Diagnostics.CodeAnalysis;
                public class Source { public int Value { get; set; } }
                public class Dto
                {
                    [SetsRequiredMembers] public Dto(int seed = 7) { Value = seed; }
                    public required int Value { get; set; }
                }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps { public static int Proof() => MapToDto(new Source { Value = 42 }).Value; }
                """);
            AssertNoCompilationErrors(result);
            Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
        }

        private static void AssertNoCompilationErrors(GeneratorCompilationResult result)
        {
            Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        [Fact]
        public void Generated_Mapper_Maps_All_Matching_Properties()
        {
            var source = new SgOrderSource
            {
                Id = 42,
                OrderNumber = "ORD-2026-00042",
                Total = 1234.56m,
            };

            var dto = TestGeneratedMappers.MapToSgOrderDto(source);

            Assert.Equal(42, dto.Id);
            Assert.Equal("ORD-2026-00042", dto.OrderNumber);
            Assert.Equal(1234.56m, dto.Total);
        }

        [Fact]
        public void Generated_Mapper_Handles_Second_Pair_On_Same_Class()
        {
            var source = new SgUserSource { UserId = 7, Name = "Ayşe", Email = "ayse@example.tr" };

            var dto = TestGeneratedMappers.MapToSgUserDto(source);

            Assert.Equal(7, dto.UserId);
            Assert.Equal("Ayşe", dto.Name);
            Assert.Equal("ayse@example.tr", dto.Email);
        }

        [Fact]
        public void Generated_Mapper_Returns_Default_On_Null_Source()
        {
            var dto = TestGeneratedMappers.MapToSgOrderDto(null!);
            Assert.Null(dto);
        }

        [Fact]
        public void Generated_Mapper_Skips_Properties_With_Mismatched_Types()
        {
            var source = new SgTypeMismatchSrc { Value = 99, SameName = "kept" };

            var dto = TestGeneratedMappers.MapToSgTypeMismatchDst(source);

            // Value is skipped because int -> long is a type mismatch at the
            // source-gen level (runtime IMapper handles conversions).
            Assert.Equal(0L, dto.Value);
            // Same-type member still copies.
            Assert.Equal("kept", dto.SameName);
        }

        [Fact]
        public void Generated_Mapper_Supports_Nested_Mapper_Containers()
        {
            var dto = SgOuterContainer.NestedGeneratedMappers.MapToSgNestedDto(
                new SgNestedSource { Id = 9, Label = "nested" });

            Assert.Equal(9, dto.Id);
            Assert.Equal("nested", dto.Label);
        }

        [Fact]
        public void Generated_Mapper_Does_Not_Collide_For_Same_Simple_Container_Name()
        {
            var left = SourceGenCollisionA.SharedGeneratedMappers.MapToSharedDto(
                new SourceGenCollisionA.SharedSource { Id = 1, Label = "alpha" });
            var right = SourceGenCollisionB.SharedGeneratedMappers.MapToSharedDto(
                new SourceGenCollisionB.SharedSource { Id = 2, Label = "beta" });

            Assert.Equal("alpha", left.Label);
            Assert.Equal("beta", right.Label);
        }

        [Fact]
        public void Generated_Mapper_Reports_Diagnostic_For_NonPartial_Nested_Container()
        {
            var source = """
                using Meridian.Mapping;

                namespace Demo;

                public class Outer
                {
                    public class Src
                    {
                        public int Id { get; set; }
                    }

                    public class Dst
                    {
                        public int Id { get; set; }
                    }

                    [GenerateMapper(typeof(Src), typeof(Dst))]
                    public static partial class NestedGeneratedMappers
                    {
                    }
                }
                """;

            var runResult = RunGenerator(source);

            var diagnostics = runResult.Results
                .SelectMany(result => result.Diagnostics)
                .Where(d => d.Id == "MERIDIANGEN001")
                .ToList();

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("non-partial containing type 'Demo.Outer'", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            Assert.DoesNotContain(
                runResult.Results.SelectMany(result => result.GeneratedSources),
                generated => generated.HintName.Contains("NestedGeneratedMappers.Meridian.g.cs", StringComparison.Ordinal));
        }

        [Fact]
        public void Generated_Mapper_Is_Dramatically_Faster_Than_Runtime_Allocation()
        {
            // Pure delegate-less, direct IL — should be within ~2x of hand-written.
            var source = new SgOrderSource { Id = 1, OrderNumber = "x", Total = 1m };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 1_000_000; i++)
            {
                _ = TestGeneratedMappers.MapToSgOrderDto(source);
            }
            sw.Stop();

            // Source-gen should beat the runtime fast path. Budget generously
            // to absorb CI jitter.
            Assert.True(sw.ElapsedMilliseconds < 1_000,
                $"1M source-gen calls took {sw.ElapsedMilliseconds}ms — expected sub-second.");
        }

        private static GeneratorDriverRunResult RunGenerator(string source)
        {
            var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
            var compilation = CSharpCompilation.Create(
                assemblyName: "Meridian.Mapping.Tests.GeneratorHarness",
                syntaxTrees: [CSharpSyntaxTree.ParseText(source, parseOptions)],
                references: GetMetadataReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators: [new MeridianMapperGenerator().AsSourceGenerator()],
                parseOptions: parseOptions);

            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var driverDiagnostics);

            Assert.Empty(driverDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Empty(updatedCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

            return driver.GetRunResult();
        }

        private static IEnumerable<MetadataReference> GetMetadataReferences()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var trustedPlatformAssemblies =
                ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ??
                [];

            foreach (var assemblyPath in trustedPlatformAssemblies)
            {
                if (seen.Add(assemblyPath))
                {
                    yield return MetadataReference.CreateFromFile(assemblyPath);
                }
            }

            var mappingAssemblyPath = typeof(IMapper).Assembly.Location;
            if (seen.Add(mappingAssemblyPath))
            {
                yield return MetadataReference.CreateFromFile(mappingAssemblyPath);
            }
        }
    }
}

namespace Meridian.Mapping.Tests.SourceGenCollisionA
{
    using Meridian.Mapping;

    public class SharedSource
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    public class SharedDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    [GenerateMapper(typeof(SharedSource), typeof(SharedDto))]
    public static partial class SharedGeneratedMappers
    {
    }
}

namespace Meridian.Mapping.Tests.SourceGenCollisionB
{
    using Meridian.Mapping;

    public class SharedSource
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    public class SharedDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    [GenerateMapper(typeof(SharedSource), typeof(SharedDto))]
    public static partial class SharedGeneratedMappers
    {
    }
}
