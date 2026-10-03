using Microsoft.CodeAnalysis;

namespace Meridian.Mapping.Generators.Diagnostics;

internal static class MapperDiagnostics
{
    private const string Category = "Meridian.Mapping.Generators";
    internal static readonly DiagnosticDescriptor NestedContainerMustBePartial = new(
        "MERIDIANGEN001", "Nested mapper containers require partial containing types",
        "Meridian mapper container '{0}' is nested inside non-partial containing type '{1}'. Mark every containing type partial or move the mapper container to namespace scope.",
        Category, DiagnosticSeverity.Warning, true);
    internal static readonly DiagnosticDescriptor LegacyUnmapped = new(
        "MERIDIANGEN002", "Destination member is not mapped", "Destination member '{0}' is not mapped: {1}",
        Category, DiagnosticSeverity.Info, true);
    internal static readonly DiagnosticDescriptor StructuralUnmapped = new(
        "MERIDIANGEN002", "Destination member is not mapped", "Destination member '{0}' is not mapped: {1}",
        Category, DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor UnsupportedConstruction = new(
        "MERIDIANGEN003", "Destination construction is not supported",
        "Meridian cannot generate a mapper for destination '{0}': {1}. Use a concrete destination with an accessible constructor callable without arguments that satisfies required members, or use runtime IMapper for constructor mapping.",
        Category, DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor MethodCollision = new(
        "MERIDIANGEN004", "Generated mapping method collision", "Meridian mapping method '{0}' conflicts: {1}",
        Category, DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor UnsupportedStructural = new(
        "MERIDIANGEN005", "Generated mapping capability is not supported", "Meridian cannot generate this mapping: {0}",
        Category, DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor InvalidConfiguration = new(
        "MERIDIANGEN006", "Invalid generated mapping configuration", "Meridian mapping configuration is invalid: {0}",
        Category, DiagnosticSeverity.Error, true);
}
