using Microsoft.CodeAnalysis;
namespace Meridian.Mediator.Generators.Diagnostics;
internal static class MediatorDiagnostics
{
    public static readonly DiagnosticDescriptor Missing = new("MERIDIANMED001", "Missing explicit request handler", "Registry '{0}' declares expected request '{1}', but its handlerTypes contain no matching request handler", "Meridian.Mediator", DiagnosticSeverity.Error, true);
    public static readonly DiagnosticDescriptor Duplicate = new("MERIDIANMED002", "Duplicate explicit handler", "Registry '{0}' declares multiple handlers for '{1}'", "Meridian.Mediator", DiagnosticSeverity.Error, true);
    public static readonly DiagnosticDescriptor Invalid = new("MERIDIANMED003", "Invalid declared registration", "Registry '{0}' has invalid declared registration '{1}': {2}", "Meridian.Mediator", DiagnosticSeverity.Error, true);
    // 004 is reserved for visible declared lifetime evidence; arbitrary startup lifetimes are not inferred.
    public static readonly DiagnosticDescriptor Unsupported = new("MERIDIANMED005", "Unsupported generated construction", "Registry '{0}' cannot generate '{1}': {2}", "Meridian.Mediator", DiagnosticSeverity.Error, true);
}
