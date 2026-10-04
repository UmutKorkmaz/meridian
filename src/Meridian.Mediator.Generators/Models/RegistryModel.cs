using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
namespace Meridian.Mediator.Generators.Models;
// Semantic symbols stay in the transform. The incremental output is immutable and value-comparable.
internal sealed class RegistryModel : IEquatable<RegistryModel>
{
    public RegistryModel(string hintName, string source, ImmutableArray<Diagnostic> diagnostics)
    { HintName = hintName; Source = source; Diagnostics = diagnostics; }
    public string HintName { get; }
    public string Source { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool Equals(RegistryModel? other) => other != null && HintName == other.HintName && Source == other.Source && Diagnostics.Length == other.Diagnostics.Length && Diagnostics.Zip(other.Diagnostics, (a, b) => a.Id == b.Id && a.GetMessage(System.Globalization.CultureInfo.InvariantCulture) == b.GetMessage(System.Globalization.CultureInfo.InvariantCulture) && a.Location.Equals(b.Location)).All(x => x);
    public override bool Equals(object? obj) => Equals(obj as RegistryModel);
    public override int GetHashCode() => HintName.GetHashCode() ^ Source.GetHashCode();
}
