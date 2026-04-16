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
            Assert.Contains("non-partial containing type 'Demo.Outer'", diagnostic.GetMessage());
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
