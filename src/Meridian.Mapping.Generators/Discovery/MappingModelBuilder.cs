using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Meridian.Mapping.Generators.Models;

namespace Meridian.Mapping.Generators.Discovery;

internal static class MappingModelBuilder
{
    private const string DiagnosticCategory = "Meridian.Mapping.Generators";
    private static readonly DiagnosticDescriptor NestedContainerMustBePartial = new(
        id: "MERIDIANGEN001",
        title: "Nested mapper containers require partial containing types",
        messageFormat: "Meridian mapper container '{0}' is nested inside non-partial containing type '{1}'. Mark every containing type partial or move the mapper container to namespace scope.",
        category: DiagnosticCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedConstruction = new(
        id: "MERIDIANGEN003",
        title: "Destination construction is not supported",
        messageFormat: "Meridian cannot generate a mapper for destination '{0}': {1}. Use a concrete destination with an accessible constructor callable without arguments that satisfies required members, or use runtime IMapper for constructor mapping.",
        category: DiagnosticCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);


    internal static MapperModel Build(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;
        var pairs = ImmutableArray.CreateBuilder<MapPairModel>();
        foreach (var attribute in context.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.ConstructorArguments.Length != 2 ||
                attribute.ConstructorArguments[0].Value is not ITypeSymbol source ||
                attribute.ConstructorArguments[1].Value is not ITypeSymbol destination) continue;

            var failure = GetConstructionFailure(destination, context.SemanticModel, classSymbol,
                ((ClassDeclarationSyntax)context.TargetNode).CloseBraceToken.SpanStart);
            var members = ImmutableArray.CreateBuilder<MemberModel>();
            var sourceProperties = GetWritableNameLookup(source, forWriting: false);
            foreach (var property in GetWritableNameLookup(destination, forWriting: true).Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sourceProperties.TryGetValue(property.Name, out var sourceProperty)) continue;
                members.Add(new MemberModel(sourceProperty.Name, property.Name,
                    sourceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    new ConversionModel(SymbolEqualityComparer.Default.Equals(sourceProperty.Type, property.Type),
                        sourceProperty.Type.ToDisplayString(), property.Type.ToDisplayString())));
            }
            pairs.Add(new MapPairModel(source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), source.Name, destination.Name,
                NamespaceOf(source), NamespaceOf(destination), members.ToImmutable(),
                new ConstructorModel(failure is null), new GeneratedOptions()));
        }
        var containingTypes = GetContainingTypes(classSymbol).Select(CreateTypeDeclarationInfo).ToImmutableArray();
        var nonPartial = containingTypes.FirstOrDefault(type => !type.IsPartial);
        return new MapperModel(NamespaceOf(classSymbol), containingTypes, CreateTypeDeclarationInfo(classSymbol),
            BuildMetadataPath(classSymbol), pairs.ToImmutable(), nonPartial is null);
    }

    // Diagnostics retain current source-tree identity for #pragma, #line and suppression.
    // They travel through a separate short-lived pipeline, never through MapperModel equality.
    internal static ImmutableArray<Diagnostic> BuildDiagnostics(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;
        if (!context.Attributes.Any(attribute => attribute.ConstructorArguments.Length == 2 &&
            attribute.ConstructorArguments[0].Value is ITypeSymbol &&
            attribute.ConstructorArguments[1].Value is ITypeSymbol))
            return ImmutableArray<Diagnostic>.Empty;
        var nonPartial = GetContainingTypes(classSymbol).FirstOrDefault(type => !IsPartial(type));
        if (nonPartial is not null)
            return ImmutableArray.Create(Diagnostic.Create(NestedContainerMustBePartial,
                classSymbol.Locations.FirstOrDefault(),
                classSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                nonPartial.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var attribute in context.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.ConstructorArguments.Length != 2 ||
                attribute.ConstructorArguments[0].Value is not ITypeSymbol ||
                attribute.ConstructorArguments[1].Value is not ITypeSymbol destination) continue;
            var failure = GetConstructionFailure(destination, context.SemanticModel, classSymbol,
                ((ClassDeclarationSyntax)context.TargetNode).CloseBraceToken.SpanStart);
            if (failure is not null)
                diagnostics.Add(Diagnostic.Create(UnsupportedConstruction,
                    attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation(),
                    destination.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), failure));
        }
        return diagnostics.ToImmutable();
    }

    private static string? NamespaceOf(ITypeSymbol type) => type.ContainingNamespace is { IsGlobalNamespace: false } ns
        ? ns.ToDisplayString() : null;

    private static Dictionary<string, IPropertySymbol> GetWritableNameLookup(ITypeSymbol type, bool forWriting)
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
