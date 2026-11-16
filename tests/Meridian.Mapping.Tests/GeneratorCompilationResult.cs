using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Tests;

internal sealed record GeneratorCompilationResult(
    CSharpCompilation Compilation,
    ImmutableArray<Diagnostic> Diagnostics,
    string GeneratedSource);
