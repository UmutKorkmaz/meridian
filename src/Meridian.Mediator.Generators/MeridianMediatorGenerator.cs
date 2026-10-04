using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Meridian.Mediator.Generators.Diagnostics;
using Meridian.Mediator.Generators.Models;
namespace Meridian.Mediator.Generators;

[Generator]
public sealed class MeridianMediatorGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Meridian.Mediator.Generated.GenerateMediatorRegistryAttribute";
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(c => c.AddSource("GenerateMediatorRegistryAttribute.g.cs", AttributeSource));
        var registries = context.SyntaxProvider.ForAttributeWithMetadataName(AttributeName,
            (node, _) => node is TypeDeclarationSyntax,
            (ctx, _) => Build(ctx));
        context.RegisterSourceOutput(registries, (ctx, model) =>
        {
            foreach (var diagnostic in model.Diagnostics) ctx.ReportDiagnostic(diagnostic);
            if (model.Source.Length != 0) ctx.AddSource(model.HintName, model.Source);
        });
    }
    private static RegistryModel Build(GeneratorAttributeSyntaxContext context)
    {
        var registry = (INamedTypeSymbol)context.TargetSymbol;
        var declaration = (TypeDeclarationSyntax)context.TargetNode;
        var attribute = context.Attributes[0];
        var syntax = (AttributeSyntax)attribute.ApplicationSyntaxReference!.GetSyntax();
        var errors = ImmutableArray.CreateBuilder<Diagnostic>();
        var name = registry.ToDisplayString();
        void Error(DiagnosticDescriptor descriptor, Location location, string type, string? reason = null)
        {
            errors.Add(reason == null ? Diagnostic.Create(descriptor, location, name, type) : Diagnostic.Create(descriptor, location, name, type, reason));
        }
        if (declaration is not ClassDeclarationSyntax || registry.IsFileLocal || registry.ContainingType != null || registry.Arity != 0 || registry.IsStatic || registry.IsAbstract || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword) || registry.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared))
        {
            Error(MediatorDiagnostics.Invalid, declaration.Identifier.GetLocation(), name, "use a top-level non-generic non-abstract partial class without declared constructors");
            return new RegistryModel(name + ".g.cs", "", errors.ToImmutable());
        }
        var selected = new List<INamedTypeSymbol>();
        var expectations = new List<(INamedTypeSymbol Type, Location Location)>();
        var classified = true;
        var includesStandard = false;
        var pipelineOptions = new Dictionary<string, List<INamedTypeSymbol>>();
        var handlerArray = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0] : default;
        if (handlerArray.IsNull || handlerArray.Kind != TypedConstantKind.Array)
        { classified = false; Error(MediatorDiagnostics.Invalid, syntax.GetLocation(), "handlerTypes", "a non-null explicit type array is required"); }
        else foreach (var value in handlerArray.Values)
        {
            if (value.Value is not INamedTypeSymbol type || type.TypeKind == TypeKind.Error)
            { classified = false; Error(MediatorDiagnostics.Invalid, syntax.GetLocation(), "handlerTypes", "each entry must be a resolved handler type"); }
            else if (!selected.Any(t => SymbolEqualityComparer.Default.Equals(t, type))) selected.Add(type);
        }
        foreach (var option in attribute.NamedArguments)
        {
            var arg = syntax.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == option.Key);
            if (option.Key == "IncludeStandardPipeline") { includesStandard = option.Value.Value is true; continue; }
            if (option.Key != "ExpectedRequestTypes")
            {
                if (option.Value.IsNull) Error(MediatorDiagnostics.Invalid, arg?.GetLocation() ?? syntax.GetLocation(), option.Key, "array must not be null");
                else if (option.Value.Kind != TypedConstantKind.Array) Error(MediatorDiagnostics.Invalid, arg?.GetLocation() ?? syntax.GetLocation(), option.Key, "array is required");
                else
                {
                    var list = new List<INamedTypeSymbol>();
                    foreach (var value in option.Value.Values)
                        if (value.Value is INamedTypeSymbol type && type.TypeKind != TypeKind.Error) { if (!list.Any(t => SymbolEqualityComparer.Default.Equals(t,type))) list.Add(type); }
                        else Error(MediatorDiagnostics.Invalid,arg?.GetLocation() ?? syntax.GetLocation(),option.Key,"each entry must resolve to a type");
                    pipelineOptions.Add(option.Key,list);
                }
                continue;
            }
            if (option.Value.IsNull || option.Value.Kind != TypedConstantKind.Array) { Error(MediatorDiagnostics.Invalid, arg?.GetLocation() ?? syntax.GetLocation(), option.Key, "array must not be null"); continue; }
            var elements = arg?.Expression.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>().ToArray() ?? Array.Empty<TypeOfExpressionSyntax>();
            for (var i = 0; i < option.Value.Values.Length; i++)
            {
                var value = option.Value.Values[i];
                var location = i < elements.Length ? elements[i].GetLocation() : arg?.GetLocation() ?? syntax.GetLocation();
                if (value.Value is not INamedTypeSymbol type || !Closed(type) || type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || !type.AllInterfaces.Any(t => Is(t, "Meridian.Mediator.IRequest`1")))
                    Error(MediatorDiagnostics.Invalid, location, value.Value?.ToString() ?? "null", "expected entries must be closed concrete ordinary request types");
                else if (!expectations.Any(t => SymbolEqualityComparer.Default.Equals(t.Type, type))) expectations.Add((type, location));
            }
        }
        var entries = new List<Entry>();
        foreach (var handler in selected.OrderBy(TypeName, StringComparer.Ordinal))
        {
            if (!Closed(handler)) { classified = false; Error(MediatorDiagnostics.Unsupported, syntax.GetLocation(), TypeName(handler), "open handlers require an explicit concrete closed type"); continue; }
            var contracts = handler.AllInterfaces.Where(IsHandler).OrderBy(TypeName, StringComparer.Ordinal).ToArray();
            if (contracts.Any(c => c.TypeArguments[0] is INamedTypeSymbol request && request.AllInterfaces.Count(i => Is(i, "Meridian.Mediator.IRequest`1")) > 1))
            { classified = false; Error(MediatorDiagnostics.Unsupported, syntax.GetLocation(), TypeName(handler), "a request with incompatible response contracts has no unique dispatch slot"); continue; }
            if (contracts.Any(c => Is(c, "Meridian.Mediator.Streaming.IStreamRequestHandler`2") &&
                c.TypeArguments[0].AllInterfaces.Count(i => Is(i, "Meridian.Mediator.Streaming.IStreamRequest`1")) > 1))
            { classified = false; Error(MediatorDiagnostics.Unsupported, syntax.GetLocation(), TypeName(handler), "a stream request with incompatible response contracts has no unique dispatch slot"); continue; }
            if (contracts.Length == 0) { classified = false; Error(MediatorDiagnostics.Invalid, syntax.GetLocation(), TypeName(handler), "type must implement a supported request, notification or stream handler contract"); continue; }
            // Match declared contracts before constructor validation to avoid false missing-handler cascades.
            var constructor = Constructor(context.SemanticModel.Compilation, registry, handler, out var reason);
            if (constructor == null) Error(MediatorDiagnostics.Invalid, syntax.GetLocation(), TypeName(handler), reason);
            foreach (var contract in contracts)
            {
                var category = Is(contract, "Meridian.Mediator.INotificationHandler`1") ? "NotificationHandler" : Is(contract, "Meridian.Mediator.Streaming.IStreamRequestHandler`2") ? "StreamHandler" : "RequestHandler";
                entries.Add(new Entry(handler, contract, category, constructor));
            }
        }
        foreach (var category in entries.Where(e => e.Category != "NotificationHandler").GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        foreach (var group in category.GroupBy(e => (ISymbol)e.Contract.TypeArguments[0], SymbolEqualityComparer.Default).OrderBy(g => TypeName(g.Key!), StringComparer.Ordinal))
            if (group.Select(e => (ISymbol)e.Handler).Distinct(SymbolEqualityComparer.Default).Count() > 1)
                Error(MediatorDiagnostics.Duplicate, syntax.GetLocation(), TypeName(group.Key!));
        if (classified) foreach (var expected in expectations.OrderBy(e => TypeName(e.Type), StringComparer.Ordinal))
            if (!entries.Any(e => e.Category == "RequestHandler" && SymbolEqualityComparer.Default.Equals(e.Contract.TypeArguments[0], expected.Type)))
                Error(MediatorDiagnostics.Missing, expected.Location, expected.Type.ToDisplayString());
        if (errors.Count != 0) return new RegistryModel(name + ".g.cs", "", errors.ToImmutable());
        var capabilities = Emission.BehaviorRegistryEmitter.Build(context.SemanticModel.Compilation, registry, entries, pipelineOptions, Error, syntax.GetLocation());
        if (errors.Count != 0) return new RegistryModel(name + ".g.cs", "", errors.ToImmutable());
        return new RegistryModel(name + ".g.cs", Emit(registry, entries, includesStandard, capabilities), errors.ToImmutable());
    }
    internal static bool Is(INamedTypeSymbol type, string metadataName) => type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == metadataName || MetadataName(type.OriginalDefinition) == metadataName;
    private static string MetadataName(INamedTypeSymbol type) => type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;
    private static bool IsHandler(INamedTypeSymbol type) => Is(type, "Meridian.Mediator.IRequestHandler`1") || Is(type, "Meridian.Mediator.IRequestHandler`2") || Is(type, "Meridian.Mediator.INotificationHandler`1") || Is(type, "Meridian.Mediator.Streaming.IStreamRequestHandler`2");
    internal static bool Closed(ITypeSymbol type) => type.TypeKind != TypeKind.TypeParameter && (type is not IArrayTypeSymbol array || Closed(array.ElementType)) && (type is not INamedTypeSymbol n || (!n.IsUnboundGenericType && n.TypeArguments.All(Closed) && (n.ContainingType == null || Closed(n.ContainingType))));
    internal static string TypeName(ISymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    internal static string? Constructor(Compilation compilation, INamedTypeSymbol registry, INamedTypeSymbol handler, out string reason)
    {
        reason = "type and a unique constructor must be accessible from the registry";
        if (handler.IsAbstract || handler.TypeKind != TypeKind.Class || !compilation.IsSymbolAccessibleWithin(handler, registry)) return null;
        var constructors = handler.InstanceConstructors.Where(c => compilation.IsSymbolAccessibleWithin(c, registry)).ToArray();
        if (handler.InstanceConstructors.Count(c => c.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute")) > 1)
        { reason = "multiple constructors are marked ActivatorUtilitiesConstructor"; return null; }
        var marked = constructors.Where(c => c.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute")).ToArray();
        var constructor = marked.Length == 1 ? marked[0] : marked.Length == 0 && constructors.Length == 1 ? constructors[0] : null;
        if (constructor == null) { reason = "select exactly one accessible constructor or mark exactly one with ActivatorUtilitiesConstructor"; return null; }
        for (var type = handler; type != null; type = type.BaseType)
            if (type.GetMembers().Any(m => m is IPropertySymbol { IsRequired: true } || m is IFieldSymbol { IsRequired: true }) && !constructor.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
            { reason = "required members need a SetsRequiredMembers constructor"; return null; }
        var arguments = new List<string>();
        foreach (var parameter in constructor.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer || parameter.Type.IsRefLikeType || !compilation.IsSymbolAccessibleWithin(parameter.Type, registry) || parameter.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute"))
            { reason = "constructor has an unsupported ref, pointer, inaccessible, keyed or byref-like parameter"; return null; }
            var type = TypeName(parameter.Type);
            if (parameter.Type.SpecialType == SpecialType.None && parameter.Type.ToDisplayString() == "System.IServiceProvider") arguments.Add("sp");
            else if (parameter.HasExplicitDefaultValue)
            {
                var constant = Constant(parameter.ExplicitDefaultValue, parameter.Type);
                if (constant == null) { reason = "optional parameter default cannot be emitted"; return null; }
                arguments.Add("(" + type + ")(sp.GetService(typeof(" + type + ")) ?? (object?)(" + type + ")(" + constant + "))!");
            }
            else if (parameter.Type is INamedTypeSymbol enumerable && Is(enumerable, "System.Collections.Generic.IEnumerable`1"))
                arguments.Add("global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<" + TypeName(enumerable.TypeArguments[0]) + ">(sp)");
            else arguments.Add("global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<" + type + ">(sp)");
        }
        return "new " + TypeName(handler) + "(" + string.Join(", ", arguments) + ")";
    }
    private static string? Constant(object? value, ITypeSymbol type)
    {
        if (value == null) return "default(" + TypeName(type) + ")";
        if (type.TypeKind == TypeKind.Enum) return "(" + TypeName(type) + ")" + Convert.ToString(value, CultureInfo.InvariantCulture);
        if (value is string s) return SymbolDisplay.FormatLiteral(s, true);
        if (value is char c) return SymbolDisplay.FormatLiteral(c, true);
        if (value is bool b) return b ? "true" : "false";
        if (value is float f) return float.IsNaN(f) ? "float.NaN" : float.IsPositiveInfinity(f) ? "float.PositiveInfinity" : float.IsNegativeInfinity(f) ? "float.NegativeInfinity" : f.ToString("R", CultureInfo.InvariantCulture) + "F";
        if (value is double d) return double.IsNaN(d) ? "double.NaN" : double.IsPositiveInfinity(d) ? "double.PositiveInfinity" : double.IsNegativeInfinity(d) ? "double.NegativeInfinity" : d.ToString("R", CultureInfo.InvariantCulture) + "D";
        if (value is decimal m) return m.ToString(CultureInfo.InvariantCulture) + "M";
        if (value is long l) return l.ToString(CultureInfo.InvariantCulture) + "L";
        if (value is ulong ul) return ul.ToString(CultureInfo.InvariantCulture) + "UL";
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
    private static string Emit(INamedTypeSymbol registry, List<Entry> entries, bool standard, List<Emission.BehaviorRegistryEmitter.Capability> capabilities)
    {
        const string g = "global::Meridian.Mediator.Generated.";
        var b = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!registry.ContainingNamespace.IsGlobalNamespace) b.Append("namespace ").Append(registry.ContainingNamespace.ToDisplayString()).Append(" {\n");
        b.Append("partial class @").Append(registry.Name).Append(" : ").Append(g).Append("IGeneratedMediatorRegistry, ").Append(g).Append("IGeneratedMediatorContributions, ").Append(g).Append("IGeneratedMediatorBehaviorCapabilities {\npublic @").Append(registry.Name).Append("() {}\npublic string RegistryId => ").Append(SymbolDisplay.FormatLiteral(registry.ContainingAssembly.Identity.Name + ":" + registry.ToDisplayString(), true)).Append(";\n");
        foreach (var pair in new[] { ("RequestTypes", "RequestHandler"), ("NotificationTypes", "NotificationHandler"), ("StreamTypes", "StreamHandler") })
            b.Append("public global::System.Collections.Generic.IReadOnlyCollection<global::System.Type> ").Append(pair.Item1).Append(" { get; } = global::System.Array.AsReadOnly(new global::System.Type[] { ").Append(string.Join(", ", entries.Where(e => e.Category == pair.Item2).Select(e => TypeName(e.Contract.TypeArguments[0])).Distinct().OrderBy(t => t, StringComparer.Ordinal).Select(t => "typeof(" + t + ")"))).Append(" });\n");
        Emission.BehaviorRegistryEmitter.EmitCapabilities(b, capabilities);
        b.Append("public bool IncludesStandardPipeline => ").Append(standard ? "true" : "false").Append(";\npublic global::System.Collections.Generic.IReadOnlyList<").Append(g).Append("GeneratedMediatorContribution> Contributions { get; } = global::System.Array.AsReadOnly(new ").Append(g).Append("GeneratedMediatorContribution[] {\n");
        var ordinal = 0;
        foreach (var entry in entries)
            b.Append("new ").Append(g).Append("GeneratedMediatorContribution(typeof(").Append(TypeName(entry.Contract)).Append("), typeof(").Append(TypeName(entry.Handler)).Append("), ").Append(entry.Definition == null ? "null" : "typeof(" + TypeName(entry.Definition.ConstructUnboundGenericType()) + ")").Append(", ").Append(g).Append("GeneratedMediatorCategory.").Append(entry.Category).Append(", ").Append(g).Append("GeneratedMediatorPhase.").Append(entry.Phase).Append(", ").Append(entry.Order).Append(", ").Append(ordinal++).Append(", ").Append(g).Append("GeneratedMediatorLifetime.").Append(entry.Lifetime).Append(", ").Append(g).Append("GeneratedMediatorActivation.").Append(entry.Activation).Append(", static sp => ").Append(entry.Constructor).Append("),\n");
        b.Append("});\npublic void RegisterServices(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services, global::Microsoft.Extensions.DependencyInjection.ServiceLifetime handlerLifetime) { ").Append(g).Append("GeneratedMediatorRegistration.RegisterServices(services, Contributions, handlerLifetime, IncludesStandardPipeline); }\n");
        Emission.RequestRegistryEmitter.Emit(b, entries);
        Emission.NotificationRegistryEmitter.Emit(b, entries);
        Emission.StreamRegistryEmitter.Emit(b, entries);
        b.Append("}\n");
        if (!registry.ContainingNamespace.IsGlobalNamespace) b.Append("}\n");
        return b.ToString();
    }
    internal sealed class Entry
    {
        public Entry(INamedTypeSymbol handler, INamedTypeSymbol contract, string category, string? constructor) { Handler = handler; Contract = contract; Category = category; Constructor = constructor; }
        public INamedTypeSymbol Handler { get; }
        public INamedTypeSymbol Contract { get; }
        public string Category { get; }
        public string? Constructor { get; }
        public string Phase { get; set; } = "Handler";
        public string Lifetime { get; set; } = "HandlerLifetime";
        public string Activation { get; set; } = "Declared";
        public int Order { get; set; }
        public INamedTypeSymbol? Definition { get; set; }
    }
    private const string AttributeSource = @"// <auto-generated/>
#nullable enable
namespace Meridian.Mediator.Generated {
[global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class GenerateMediatorRegistryAttribute : global::System.Attribute {
public GenerateMediatorRegistryAttribute(params global::System.Type[] handlerTypes) { HandlerTypes = handlerTypes; }
public global::System.Type[] HandlerTypes { get; }
public global::System.Type[] ExpectedRequestTypes { get; set; } = new global::System.Type[0];
public global::System.Type[] BehaviorTypes { get; set; } = new global::System.Type[0];
public global::System.Type[] StreamBehaviorTypes { get; set; } = new global::System.Type[0];
public global::System.Type[] ValidatorTypes { get; set; } = new global::System.Type[0];
public global::System.Type[] PreProcessorTypes { get; set; } = new global::System.Type[0];
public global::System.Type[] PostProcessorTypes { get; set; } = new global::System.Type[0];
public global::System.Type[] ExceptionHandlerTypes { get; set; } = new global::System.Type[0];
public global::System.Type[] ExceptionActionTypes { get; set; } = new global::System.Type[0];
public bool IncludeStandardPipeline { get; set; }
}}
";
}
