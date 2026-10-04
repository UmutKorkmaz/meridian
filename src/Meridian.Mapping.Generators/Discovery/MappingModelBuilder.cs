using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Meridian.Mapping.Generators.Diagnostics;
using Meridian.Mapping.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Meridian.Mapping.Generators.Discovery;

internal static class MappingModelBuilder
{
    internal static MapperModel Build(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken) =>
        Analyze(context, cancellationToken).Model;

    // Current-tree diagnostics never enter the immutable model or its emission cache.
    internal static ImmutableArray<Diagnostic> BuildDiagnostics(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken) =>
        Analyze(context, cancellationToken).Diagnostics;

    private static (MapperModel Model, ImmutableArray<Diagnostic> Diagnostics) Analyze(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = (INamedTypeSymbol)context.TargetSymbol;
        var pairs = ImmutableArray.CreateBuilder<MapPairModel>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var declarations = container.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == "Meridian.Mapping.GenerateMapperAttribute" && a.ConstructorArguments.Length == 2 &&
            a.ConstructorArguments[0].Value is ITypeSymbol && a.ConstructorArguments[1].Value is ITypeSymbol).ToArray();
        var containingTypes = GetContainingTypes(container).Select(CreateTypeDeclarationInfo).ToImmutableArray();
        // One model per container, including attributes on every partial declaration.
        var owner = declarations.FirstOrDefault()?.ApplicationSyntaxReference?.GetSyntax(cancellationToken)
            .FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (owner is not null && (owner.SyntaxTree != context.TargetNode.SyntaxTree || owner.Span != context.TargetNode.Span))
            return (new MapperModel(NamespaceOf(container), containingTypes, CreateTypeDeclarationInfo(container),
                BuildMetadataPath(container), ImmutableArray<MapPairModel>.Empty, false), ImmutableArray<Diagnostic>.Empty);
        var nonPartial = containingTypes.FirstOrDefault(type => !type.IsPartial);
        if (declarations.Length > 0 && nonPartial is not null)
            diagnostics.Add(Diagnostic.Create(MapperDiagnostics.NestedContainerMustBePartial,
                container.Locations.FirstOrDefault(), container.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                nonPartial.DisplayName));

        foreach (var attribute in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = (ITypeSymbol)attribute.ConstructorArguments[0].Value!;
            var destination = (ITypeSymbol)attribute.ConstructorArguments[1].Value!;
            var options = ReadOptions(attribute);
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation();
            var supported = true;
            void Report(DiagnosticDescriptor descriptor, params object[] arguments)
            {
                if (descriptor.DefaultSeverity == DiagnosticSeverity.Error) supported = false;
                if (nonPartial is null) diagnostics.Add(Diagnostic.Create(descriptor, location, arguments));
            }

            if (options.Mode is not (0 or 1))
                Report(MapperDiagnostics.InvalidConfiguration, "Mode must be Legacy or Structural");
            if (options.MaxDepth <= 0 || options.MaxCollectionItems <= 0)
                Report(MapperDiagnostics.InvalidConfiguration, "MaxDepth and MaxCollectionItems must be positive");
            if (options.MethodName is not null && (!SyntaxFacts.IsValidIdentifier(options.MethodName) ||
                SyntaxFacts.GetKeywordKind(options.MethodName) != SyntaxKind.None ||
                Enumerable.Range(0, options.MethodName.Length).Any(index =>
                    CharUnicodeInfo.GetUnicodeCategory(options.MethodName, index) == UnicodeCategory.Format)))
                Report(MapperDiagnostics.InvalidConfiguration, "MethodName must be a valid C# identifier");
            if (!IsUsableType(source, context.SemanticModel.Compilation, container) ||
                !IsUsableType(destination, context.SemanticModel.Compilation, container) ||
                !HasAccessibleSignature(source, destination, context, cancellationToken))
                Report(MapperDiagnostics.InvalidConfiguration, "source and destination must be accessible, closed, nameable types usable in a public mapping method");

            var method = options.MethodName ?? "MapTo" + destination.Name;
            if (method == container.Name || container.GetMembers(method).Any(member =>
                options.MethodName is not null || member is not IMethodSymbol existing ||
                existing.Arity == 0 && existing.Parameters.Length == 1 && existing.Parameters[0].RefKind == RefKind.None &&
                HaveSameSignatureType(existing.Parameters[0].Type, source)))
                Report(MapperDiagnostics.MethodCollision, method, "an existing container member uses this name or signature");
            foreach (var other in declarations)
            {
                if (ReferenceEquals(attribute, other)) continue;
                var otherSource = (ITypeSymbol)other.ConstructorArguments[0].Value!;
                var otherDestination = (ITypeSymbol)other.ConstructorArguments[1].Value!;
                var otherOptions = ReadOptions(other);
                if (HaveSameSignatureType(source, otherSource) &&
                    SymbolEqualityComparer.Default.Equals(destination, otherDestination))
                {
                    Report(MapperDiagnostics.MethodCollision, method, "duplicate source/destination pair");
                    break;
                }
                if (method == (otherOptions.MethodName ?? "MapTo" + otherDestination.Name) &&
                    (options.MethodName is not null || otherOptions.MethodName is not null ||
                     HaveSameSignatureType(source, otherSource)))
                {
                    Report(MapperDiagnostics.MethodCollision, method, "another mapping uses this method name; choose a unique MethodName");
                    break;
                }
            }

            var failure = options.Mode == 1 ? null : GetConstructionFailure(destination, context.SemanticModel, container,
                ((ClassDeclarationSyntax)context.TargetNode).CloseBraceToken.SpanStart);
            if (failure is not null)
                Report(MapperDiagnostics.UnsupportedConstruction, destination.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), failure);
            if (options.GenerateResolver)
                Report(MapperDiagnostics.UnsupportedStructural, "GenerateResolver is not implemented yet");
            if (options.Mode == 1 && (options.MaxDepth != 64 || options.MaxCollectionItems != 10000 ||
                options.PreserveReferences || options.AllowUndefinedEnumValues || options.AllowNullCollections || !options.AllowNullDestinationValues) &&
                options.MaxDepth > 0 && options.MaxCollectionItems > 0)
                Report(MapperDiagnostics.UnsupportedStructural, "non-default structural traversal, enum and null policies are not implemented yet");

            var members = MemberConfigurationBuilder.Build(container, source, destination,
                context.SemanticModel.Compilation, options.Mode == 1, declarations, Report);
            var constructor = options.Mode == 1
                ? destination is INamedTypeSymbol namedDestination
                    ? ConstructorModelBuilder.Select(namedDestination, members)
                    : new ConstructorModel(false, Failure: "the destination is not a concrete class or struct")
                : new ConstructorModel(true, SetsRequiredMembers: failure is null);
            if (!constructor.IsSupported)
                Report(MapperDiagnostics.UnsupportedConstruction, destination.ToDisplayString(), constructor.Failure!);
            if (!constructor.Arguments.IsDefault && constructor.Arguments.Any(argument => argument.RequiresStructuralSupport))
                Report(MapperDiagnostics.UnsupportedStructural, "constructor parameters require nullable, collection, enum or nested mapping support that is not implemented yet");
            var finalMembers = ImmutableArray.CreateBuilder<MemberModel>();
            var destinationProperties = GetWritableNameLookup(destination, true);
            if (options.Mode == 1)
            {
                for (var current = destination as INamedTypeSymbol; current is not null; current = current.BaseType)
                    foreach (var member in current.GetMembers())
                        if (member is IFieldSymbol { IsRequired: true } ||
                            member is IPropertySymbol { IsRequired: true } required &&
                            (!destinationProperties.TryGetValue(required.Name, out var writable) ||
                             !SymbolEqualityComparer.Default.Equals(writable, required)))
                            Report(MapperDiagnostics.UnsupportedStructural, "required destination member '" + member.Name + "' is not a supported public writable property");
            }
            foreach (var member in members)
            {
                if (!member.IsInitializer) continue;
                var consumed = !constructor.Arguments.IsDefault && constructor.Arguments.Any(a =>
                    string.Equals(a.DestinationMember, member.DestinationName, StringComparison.OrdinalIgnoreCase));
                if (consumed && (!member.IsRequired || constructor.SetsRequiredMembers)) continue;
                if (member.IsIgnored)
                {
                    if (member.IsRequired && !constructor.SetsRequiredMembers)
                        Report(MapperDiagnostics.UnsupportedConstruction, destination.ToDisplayString(), "ignored required member '" + member.DestinationName + "' is not satisfied by the constructor");
                    continue;
                }
                if (member.Failure is not null)
                    Report(options.Mode == 1 ? MapperDiagnostics.StructuralUnmapped : MapperDiagnostics.LegacyUnmapped,
                        member.DestinationName, member.Failure);
                else if (member.RequiresStructuralSupport)
                    Report(MapperDiagnostics.UnsupportedStructural, "member '" + member.DestinationName + "' requires nullable, collection, enum or nested mapping support that is not implemented yet");
                finalMembers.Add(member);
            }
            pairs.Add(new MapPairModel(source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), source.Name, destination.Name,
                NamespaceOf(source), NamespaceOf(destination), finalMembers.ToImmutable(), constructor with { IsSupported = supported && constructor.IsSupported }, options));
        }
        return (new MapperModel(NamespaceOf(container), containingTypes, CreateTypeDeclarationInfo(container),
            BuildMetadataPath(container), pairs.ToImmutable(), nonPartial is null), diagnostics.ToImmutable());
    }

    private static bool HaveSameSignatureType(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right)) return true;
        // C# treats dynamic as object for overload signatures, including occurrences
        // inside arrays, generic arguments and constructed containing types.
        if ((left.TypeKind == TypeKind.Dynamic || left.SpecialType == SpecialType.System_Object) &&
            (right.TypeKind == TypeKind.Dynamic || right.SpecialType == SpecialType.System_Object)) return true;
        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
            return leftArray.Rank == rightArray.Rank &&
                HaveSameSignatureType(leftArray.ElementType, rightArray.ElementType);
        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed &&
            SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition))
        {
            if (leftNamed.ContainingType is not null && rightNamed.ContainingType is not null &&
                !HaveSameSignatureType(leftNamed.ContainingType, rightNamed.ContainingType)) return false;
            return leftNamed.TypeArguments.Length == rightNamed.TypeArguments.Length &&
                leftNamed.TypeArguments.Zip(rightNamed.TypeArguments, HaveSameSignatureType).All(equal => equal);
        }
        return false;
    }

    private static GeneratedOptions ReadOptions(AttributeData attribute)
    {
        var result = new GeneratedOptions();
        foreach (var option in attribute.NamedArguments)
        {
            // Invalid attribute constants already have compiler diagnostics; avoid a
            // secondary generator crash while the user edits their configuration.
            if (option.Value.Kind == TypedConstantKind.Error || option.Value.IsNull && option.Key != "MethodName") continue;
            result = option.Key switch
            {
                "Mode" => result with { Mode = (int)option.Value.Value! },
                "MaxDepth" => result with { MaxDepth = (int)option.Value.Value! },
                "MaxCollectionItems" => result with { MaxCollectionItems = (int)option.Value.Value! },
                "PreserveReferences" => result with { PreserveReferences = (bool)option.Value.Value! },
                "AllowUndefinedEnumValues" => result with { AllowUndefinedEnumValues = (bool)option.Value.Value! },
                "AllowNullCollections" => result with { AllowNullCollections = (bool)option.Value.Value! },
                "AllowNullDestinationValues" => result with { AllowNullDestinationValues = (bool)option.Value.Value! },
                "GenerateResolver" => result with { GenerateResolver = (bool)option.Value.Value! },
                "MethodName" => result with { MethodName = (string?)option.Value.Value },
                _ => result,
            };
        }
        return result;
    }

    private static bool IsUsableType(ITypeSymbol type, Compilation compilation, INamedTypeSymbol container)
    {
        if (type.TypeKind is TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer || type.IsRefLikeType ||
            type is INamedTypeSymbol { IsUnboundGenericType: true } or { IsStatic: true } ||
            type.SpecialType == SpecialType.System_Void ||
            !compilation.IsSymbolAccessibleWithin(type, container)) return false;
        if (type is IArrayTypeSymbol array) return IsUsableType(array.ElementType, compilation, container);
        if (type is INamedTypeSymbol named)
            return !named.IsFileLocal && named.TypeArguments.All(argument => IsUsableType(argument, compilation, container));
        return true;
    }

    private static bool HasAccessibleSignature(ITypeSymbol source, ITypeSymbol destination,
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        // Ask the compiler about effective visibility (including private/protected nested
        // containers). This temporary tree never enters the emission value model.
        var container = (INamedTypeSymbol)context.TargetSymbol;
        var name = "__MeridianSignatureProbe";
        while (container.GetMembers(name).Length != 0) name += "_";
        var annotation = new SyntaxAnnotation();
        var method = SyntaxFactory.ParseMemberDeclaration("public static " +
            destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " " + name + "(" +
            source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " source) => throw null!;")!
            .WithAdditionalAnnotations(annotation);
        var tree = context.TargetNode.SyntaxTree;
        var root = tree.GetRoot(cancellationToken);
        var declaration = (ClassDeclarationSyntax)context.TargetNode;
        var replacement = root.ReplaceNode(declaration, declaration.AddMembers(method));
        var probeTree = tree.WithRootAndOptions(replacement, tree.Options);
        var probe = probeTree.GetRoot(cancellationToken).GetAnnotatedNodes(annotation).Single();
        var compilation = context.SemanticModel.Compilation.ReplaceSyntaxTree(tree, probeTree);
        return !compilation.GetSemanticModel(probeTree).GetDiagnostics(probe.Span, cancellationToken)
            .Any(diagnostic => diagnostic.Id is "CS0050" or "CS0051");
    }

    private static string? NamespaceOf(ITypeSymbol type) => type.ContainingNamespace is { IsGlobalNamespace: false } ns
        ? ns.ToDisplayString() : null;

    internal static Dictionary<string, IPropertySymbol> GetWritableNameLookup(ITypeSymbol type, bool forWriting)
    {
        var result = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                // Reserve the most-derived name before filtering: an inaccessible,
                // static or non-property declaration can hide an eligible base property.
                if (!seenNames.Add(member.Name)) continue;
                if (member is not IPropertySymbol prop) continue;
                if (prop.IsIndexer || prop.IsStatic) continue;
                if (prop.DeclaredAccessibility != Accessibility.Public) continue;
                var accessor = forWriting ? prop.SetMethod : prop.GetMethod;
                if (accessor?.DeclaredAccessibility != Accessibility.Public) continue;

                result.Add(prop.Name, prop);
            }
        }

        return result;
    }

    private static string EscapeIdentifier(string name)
    {
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ||
            SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    }

    private static string? GetConstructionFailure(
        ITypeSymbol destination, SemanticModel semanticModel, INamedTypeSymbol mapperContainer, int position)
    {
        if (destination is not INamedTypeSymbol named ||
            named.TypeKind is not (TypeKind.Class or TypeKind.Struct) || named.IsAbstract || named.IsStatic)
            return "the destination is not a concrete class or struct";

        // Bind the exact legacy construction expression in the mapper's scope. Roslyn
        // resolves optional/params overloads and ambiguity; selecting an arbitrary
        // accessible constructor could disagree with what emitted new Dto() calls.
        var creation = SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName(named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
            .WithArgumentList(SyntaxFactory.ArgumentList());
        var constructor = semanticModel.GetSpeculativeSymbolInfo(
            position, creation, SpeculativeBindingOption.BindAsExpression).Symbol as IMethodSymbol;
        if (constructor is null ||
            !IsConstructorAccessibleForCreation(constructor, semanticModel.Compilation, mapperContainer))
            return "no accessible unambiguous constructor can be called without arguments";

        var satisfiesRequiredMembers = constructor.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
        if (!satisfiesRequiredMembers)
        {
            for (var current = named; current is not null; current = current.BaseType)
            {
                if (current.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true } ||
                    member is IFieldSymbol { IsRequired: true }))
                    return "required destination members are not satisfied by the selected constructor";
            }
        }

        return null;
    }

    private static bool IsConstructorAccessibleForCreation(
        IMethodSymbol constructor, Compilation compilation, INamedTypeSymbol mapperContainer)
    {
        if (!compilation.IsSymbolAccessibleWithin(constructor, mapperContainer))
            return false;

        if (constructor.DeclaredAccessibility == Accessibility.ProtectedOrInternal &&
            compilation.IsSymbolAccessibleWithin(constructor, mapperContainer.ContainingAssembly))
            return true;

        if (constructor.DeclaredAccessibility is not (Accessibility.Protected or
            Accessibility.ProtectedAndInternal or Accessibility.ProtectedOrInternal))
            return true;

        // Protected access from a derived type permits base constructor chaining,
        // but not new Base(). Object creation is legal within the destination itself
        // (including its nested types); internal access was handled separately above.
        for (var current = mapperContainer; current is not null; current = current.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(
                current.OriginalDefinition, constructor.ContainingType.OriginalDefinition))
                return true;
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetContainingTypes(INamedTypeSymbol symbol)
    {
        var stack = new Stack<INamedTypeSymbol>();
        for (var current = symbol.ContainingType; current is not null; current = current.ContainingType)
        {
            stack.Push(current);
        }

        return stack;
    }

    private static TypeDeclarationInfo CreateTypeDeclarationInfo(INamedTypeSymbol typeSymbol)
    {
        return new TypeDeclarationInfo(
            Name: typeSymbol.Name,
            DisplayName: typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            Accessibility: GetAccessibility(typeSymbol),
            Keyword: GetTypeKeyword(typeSymbol),
            Modifiers: GetTypeModifiers(typeSymbol),
            TypeParameters: BuildTypeParameters(typeSymbol),
            ConstraintClauses: BuildConstraintClauses(typeSymbol),
            IsPartial: IsPartial(typeSymbol));
    }

    private static string BuildMetadataPath(INamedTypeSymbol symbol)
    {
        var parts = new Stack<string>();
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            parts.Push(current.MetadataName);
        }

        if (!symbol.ContainingNamespace.IsGlobalNamespace)
        {
            parts.Push(symbol.ContainingNamespace.ToDisplayString());
        }

        return string.Join(".", parts);
    }

    private static bool IsPartial(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax typeDeclaration &&
            typeDeclaration.Modifiers.Any(modifier =>
                modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)));
    }

    private static string GetAccessibility(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "internal",
        };
    }

    private static string GetTypeKeyword(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.IsRecord)
        {
            return typeSymbol.TypeKind == TypeKind.Struct ? "record struct" : "record class";
        }

        return typeSymbol.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class",
        };
    }

    private static string GetTypeModifiers(INamedTypeSymbol typeSymbol)
    {
        var modifiers = new List<string>();

        if (typeSymbol.IsStatic)
        {
            modifiers.Add("static");
        }
        else
        {
            if (typeSymbol.TypeKind == TypeKind.Struct && typeSymbol.IsRefLikeType)
            {
                modifiers.Add("ref");
            }

            if (typeSymbol.TypeKind == TypeKind.Struct && typeSymbol.IsReadOnly)
            {
                modifiers.Add("readonly");
            }

            if (typeSymbol.TypeKind == TypeKind.Class && typeSymbol.IsAbstract && !typeSymbol.IsSealed)
            {
                modifiers.Add("abstract");
            }

            if (typeSymbol.TypeKind == TypeKind.Class && typeSymbol.IsSealed)
            {
                modifiers.Add("sealed");
            }
        }

        return string.Join(" ", modifiers);
    }

    private static string BuildTypeParameters(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", typeSymbol.TypeParameters.Select(tp => EscapeIdentifier(tp.Name))) + ">";
    }

    private static ImmutableArray<string> BuildConstraintClauses(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeParameters.Length == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var typeParameter in typeSymbol.TypeParameters)
        {
            var parts = new List<string>();

            if (typeParameter.HasReferenceTypeConstraint)
            {
                parts.Add(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                    ? "class?"
                    : "class");
            }

            if (typeParameter.HasNotNullConstraint)
            {
                parts.Add("notnull");
            }

            if (typeParameter.HasUnmanagedTypeConstraint)
            {
                parts.Add("unmanaged");
            }
            else if (typeParameter.HasValueTypeConstraint)
            {
                parts.Add("struct");
            }

            foreach (var constraintType in typeParameter.ConstraintTypes)
            {
                parts.Add(constraintType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }

            if (typeParameter.HasConstructorConstraint)
            {
                parts.Add("new()");
            }

            if (parts.Count > 0)
            {
                builder.Add($"{EscapeIdentifier(typeParameter.Name)} : {string.Join(", ", parts)}");
            }
        }

        return builder.ToImmutable();
    }

}
