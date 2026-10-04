using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Meridian.Mediator.Generators.Diagnostics;
using static Meridian.Mediator.Generators.MeridianMediatorGenerator;

namespace Meridian.Mediator.Generators.Emission;

internal static class BehaviorRegistryEmitter
{
    internal sealed class Capability(INamedTypeSymbol request, ITypeSymbol response, INamedTypeSymbol definition, bool applies)
    {
        internal INamedTypeSymbol Request { get; } = request;
        internal ITypeSymbol Response { get; } = response;
        internal INamedTypeSymbol Definition { get; } = definition;
        internal bool Applies { get; } = applies;
    }

    internal static List<Capability> Build(Compilation compilation, INamedTypeSymbol registry,
        List<Entry> entries, Dictionary<string, List<INamedTypeSymbol>> options,
        Action<DiagnosticDescriptor, Location, string, string?> error, Location location)
    {
        var capabilities = new List<Capability>();
        var requests = entries.Where(e => e.Category == "RequestHandler")
            .Select(e => (INamedTypeSymbol)e.Contract.TypeArguments[0]).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToArray();
        var pairs = requests.Select(q => (Q: q, R: q.AllInterfaces.Single(i => Is(i, "Meridian.Mediator.IRequest`1")).TypeArguments[0])).ToArray();
        INamedTypeSymbol Close(string metadata, params ITypeSymbol[] types) => compilation.GetTypeByMetadataName(metadata)!.Construct(types);
        void Add(INamedTypeSymbol implementation, INamedTypeSymbol contract, string category, string phase = "Handler", string activation = "Declared", int order = 0, INamedTypeSymbol? definition = null, string? factory = null)
        {
            var constructor = factory ?? Constructor(compilation, registry, implementation, out _);
            if (constructor == null)
            {
                Constructor(compilation, registry, implementation, out var reason);
                error(MediatorDiagnostics.Invalid, location, TypeName(implementation), reason);
                return;
            }
            if (entries.Any(e => SymbolEqualityComparer.Default.Equals(e.Handler, implementation) && SymbolEqualityComparer.Default.Equals(e.Contract, contract))) return;
            entries.Add(new Entry(implementation, contract, category, constructor) { Phase = phase, Activation = activation, Order = order, Definition = definition, Lifetime = "Transient" });
        }
        foreach (var option in options)
        {
            var metadata = option.Key switch
            {
                "BehaviorTypes" => "Meridian.Mediator.Pipeline.IPipelineBehavior`2",
                "ValidatorTypes" => "Meridian.Mediator.Behaviors.IValidator`1",
                "PreProcessorTypes" => "Meridian.Mediator.Pipeline.IRequestPreProcessor`1",
                "PostProcessorTypes" => "Meridian.Mediator.Pipeline.IRequestPostProcessor`2",
                "ExceptionHandlerTypes" => "Meridian.Mediator.Pipeline.IRequestExceptionHandler`3",
                "ExceptionActionTypes" => "Meridian.Mediator.Pipeline.IRequestExceptionAction`2",
                _ => ""
            };
            foreach (var selected in option.Value)
            {
                if (metadata.Length == 0) { error(MediatorDiagnostics.Unsupported, location, option.Key, "stream behaviors require the D3 stream capability"); continue; }
                var definition = selected.OriginalDefinition;
                var contracts = definition.AllInterfaces.Where(i => Is(i, metadata)).ToArray();
                if (contracts.Length == 0) { error(MediatorDiagnostics.Invalid, location, TypeName(selected), "type does not implement the declared contribution contract"); continue; }
                var category = option.Key.Substring(0, option.Key.Length - "Types".Length);
                if (Closed(selected))
                {
                    foreach (var contract in selected.AllInterfaces.Where(i => Is(i, metadata)))
                        AddSelected(selected, contract, null);
                    continue;
                }
                if (option.Key != "BehaviorTypes") { error(MediatorDiagnostics.Unsupported, location, TypeName(selected), "only pipeline behaviors may be open; supply explicit closed types"); continue; }
                if (definition.Arity != 2 || definition.ContainingType is { Arity: > 0 } || contracts.Length != 1 ||
                    contracts[0].TypeArguments.Any(t => t is not ITypeParameterSymbol) ||
                    SymbolEqualityComparer.Default.Equals(contracts[0].TypeArguments[0], contracts[0].TypeArguments[1]))
                { error(MediatorDiagnostics.Unsupported, location, TypeName(selected), "supported open behaviors map two distinct type parameters directly to request and response"); continue; }
                foreach (var pair in pairs)
                {
                    var arguments = definition.TypeParameters.Select(p => SymbolEqualityComparer.Default.Equals(p, contracts[0].TypeArguments[0]) ? pair.Q : pair.R).ToArray();
                    var closed = definition.Construct(arguments);
                    var applies = SatisfiesConstraints(compilation, registry, closed, out var constraintError);
                    if (constraintError != null) { error(MediatorDiagnostics.Unsupported, location, TypeName(closed), constraintError); continue; }
                    capabilities.Add(new Capability(pair.Q, pair.R, definition, applies));
                    if (applies) AddSelected(closed, Close(metadata, pair.Q, pair.R), definition);
                }

                void AddSelected(INamedTypeSymbol implementation, INamedTypeSymbol contract, INamedTypeSymbol? open)
                {
                    if (Is(implementation, "Meridian.Mediator.Pipeline.RequestExceptionProcessorBehavior`2"))
                    {
                        var q = contract.TypeArguments[0]; var r = contract.TypeArguments[1];
                        var declared = options.Where(o => o.Key is "ExceptionHandlerTypes" or "ExceptionActionTypes")
                            .SelectMany(o => o.Value).Any(t => t.AllInterfaces.Any(i => (Is(i,"Meridian.Mediator.Pipeline.IRequestExceptionHandler`3") || Is(i,"Meridian.Mediator.Pipeline.IRequestExceptionAction`2")) && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0],q)));
                        if (!declared) { error(MediatorDiagnostics.Unsupported, location, TypeName(implementation), "declare finite exception handlers or actions for this request before selecting the exception driver"); return; }
                        Add(Close("Meridian.Mediator.Generated.GeneratedRequestExceptionProcessorBehavior`2", q,r), contract, "Behavior", "Request", definition: implementation.OriginalDefinition, factory: "new global::Meridian.Mediator.Generated.GeneratedRequestExceptionProcessorBehavior<"+TypeName(q)+", "+TypeName(r)+">(sp)");
                    }
                    else Add(implementation, contract, category, category == "Behavior" ? "Request" : "Handler", definition: open,
                        factory: Is(implementation, "Meridian.Mediator.Behaviors.AuditBehavior`2") ? AuditFactory(implementation) : null);
                }
            }
        }
        // Standard factories are finite and dormant unless explicitly activated.
        foreach (var pair in pairs)
        {
            var pipeline = Close("Meridian.Mediator.Pipeline.IPipelineBehavior`2", pair.Q, pair.R);
            foreach (var standard in new[] { (Name: "CorrelationIdBehavior", Order: -100), (Name: "AuditBehavior", Order: -50), (Name: "LocalizedValidationBehavior", Order: 0) })
            {
                var implementation = Close("Meridian.Mediator.Behaviors." + standard.Name + "`2", pair.Q, pair.R);
                Add(implementation,pipeline,"Behavior","Request","StandardOnly",standard.Order,implementation.OriginalDefinition,
                    standard.Name == "AuditBehavior" ? AuditFactory(implementation) : null);
                capabilities.Add(new Capability(pair.Q,pair.R,implementation.OriginalDefinition,true));
            }
        }
        foreach (var processor in entries.Where(e => e.Category is "PreProcessor" or "PostProcessor").ToArray())
        {
            var q = processor.Contract.TypeArguments[0];
            var response = OrdinaryResponse(q, processor.Category == "PostProcessor" ? processor.Contract.TypeArguments[1] : null);
            if (response == null) continue;
            var driver = processor.Category == "PreProcessor" ? "RequestPreProcessorBehavior" : "RequestPostProcessorBehavior";
            Add(Close("Meridian.Mediator.Pipeline." + driver + "`2", q, response),
                Close("Meridian.Mediator.Pipeline.IPipelineBehavior`2", q, response), "Behavior", "AutomaticProcessor");
        }
        foreach (var exception in entries.Where(e => e.Category is "ExceptionHandler" or "ExceptionAction").ToArray())
        {
            var args = exception.Contract.TypeArguments;
            var q = args[0]; var isHandler = exception.Category == "ExceptionHandler";
            var exceptionType = (INamedTypeSymbol)args[isHandler ? 2 : 1];
            var depth = 0;
            for (var current = exceptionType; current != null; current = current.BaseType) depth++;
            var adapter = isHandler ? Close("Meridian.Mediator.Generated.GeneratedExceptionHandlerInvoker`3",args.ToArray()) : Close("Meridian.Mediator.Generated.GeneratedExceptionActionInvoker`2",args.ToArray());
            var contract = isHandler ? Close("Meridian.Mediator.Generated.GeneratedExceptionHandlerInvoker`2",args[0],args[1]) : Close("Meridian.Mediator.Generated.GeneratedExceptionActionInvoker`1",q);
            Add(adapter,contract,"Infrastructure",factory:"new "+TypeName(adapter)+"("+depth+")");
            var response = isHandler ? args[1] : OrdinaryResponse(q, null);
            var responses = response == null ? Array.Empty<ITypeSymbol>() : new[] { response };
            foreach(var r in responses)
                Add(Close("Meridian.Mediator.Generated.GeneratedRequestExceptionProcessorBehavior`2",q,r),Close("Meridian.Mediator.Pipeline.IPipelineBehavior`2",q,r),"Behavior","Request", definition: compilation.GetTypeByMetadataName("Meridian.Mediator.Pipeline.RequestExceptionProcessorBehavior`2"), factory:"new global::Meridian.Mediator.Generated.GeneratedRequestExceptionProcessorBehavior<"+TypeName(q)+", "+TypeName(r)+">(sp)");
        }
        ITypeSymbol? OrdinaryResponse(ITypeSymbol request, ITypeSymbol? declaredResponse)
        {
            var responses = request.AllInterfaces.Where(i => Is(i, "Meridian.Mediator.IRequest`1"))
                .Select(i => i.TypeArguments[0]).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToArray();
            if (responses.Length != 1 || !Closed(request) ||
                declaredResponse != null && !SymbolEqualityComparer.Default.Equals(responses[0], declaredResponse))
            {
                error(MediatorDiagnostics.Unsupported, location, TypeName(request),
                    "automatic processor/action dispatch requires one exact closed ordinary IRequest<TResponse> contract matching the declared response");
                return null;
            }
            return responses[0];
        }
        return capabilities;
    }

    private static string AuditFactory(INamedTypeSymbol implementation) => "new " + TypeName(implementation) + "(global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Meridian.Mediator.Behaviors.IAuditSink>(sp), global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<global::Meridian.Mediator.MediatorTelemetryOptions>(sp) ?? global::Meridian.Mediator.MediatorTelemetryOptions.Default)";

    private static bool SatisfiesConstraints(Compilation compilation, INamedTypeSymbol registry, INamedTypeSymbol type, out string? error)
    {
        // Compile in the registry's actual accessibility context. Only known constraint
        // diagnostics prove inapplicability; every other error is an unsupported input.
        var member = "__MeridianConstraintProbe";
        while (registry.GetMembers(member).Length != 0) member += "_";
        var ns = registry.ContainingNamespace.IsGlobalNamespace ? "" : "namespace " + registry.ContainingNamespace.ToDisplayString() + " { ";
        var source = "#nullable enable\n" + ns + "partial class @" + registry.Name + " { private " + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)) + " " + member + "() => default!; }" + (ns.Length == 0 ? "" : " }");
        var tree = CSharpSyntaxTree.ParseText(source, (CSharpParseOptions)compilation.SyntaxTrees.First().Options);
        var candidate = compilation.AddSyntaxTrees(tree);
        var diagnostics = candidate.GetSemanticModel(tree).GetDiagnostics();
        var constraintIds = new[] { "CS0310", "CS0311", "CS0314", "CS0315", "CS0452", "CS0453", "CS8377", "CS8714", "CS8631" };
        var unsupported = diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error && !constraintIds.Contains(d.Id));
        error = unsupported == null ? null : "constraint validation could not establish a valid closure: " + unsupported.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        return error == null && !diagnostics.Any(d => constraintIds.Contains(d.Id));
    }

    internal static void EmitCapabilities(StringBuilder b, List<Capability> capabilities)
    {
        const string g = "global::Meridian.Mediator.Generated.";
        b.Append("public global::System.Collections.Generic.IReadOnlyList<").Append(g).Append("GeneratedMediatorBehaviorCapability> BehaviorCapabilities { get; } = global::System.Array.AsReadOnly(new ").Append(g).Append("GeneratedMediatorBehaviorCapability[] {\n");
        foreach (var c in capabilities)
            b.Append("new ").Append(g).Append("GeneratedMediatorBehaviorCapability(typeof(").Append(TypeName(c.Request)).Append("), typeof(").Append(TypeName(c.Response)).Append("), typeof(").Append(TypeName(c.Definition.ConstructUnboundGenericType())).Append("), ").Append(g).Append("GeneratedMediatorCategory.Behavior, ").Append(c.Applies ? "true" : "false").Append("),\n");
        b.Append("});\n");
    }
}
