namespace Meridian.Mediator;

/// <summary>
/// Represents a void type, since <see cref="System.Void"/> is not a valid return type in C#.
/// </summary>
public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
{
    private static readonly Unit _value = new();

    /// <summary>
    /// Default and only value of the <see cref="Unit"/> type.
    /// </summary>
    public static ref readonly Unit Value => ref _value;

    /// <summary>
    /// Task returning the default <see cref="Unit"/> value.
    /// </summary>
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(_value);

    /// <inheritdoc/>
    public int CompareTo(Unit other) => 0;

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj) => 0;

    /// <inheritdoc/>
    public override int GetHashCode() => 0;

    /// <inheritdoc/>
    public bool Equals(Unit other) => true;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Unit;

    /// <inheritdoc/>
    public override string ToString() => "()";

    /// <summary>
    /// Determines whether two <see cref="Unit"/> instances are equal.
    /// </summary>
    public static bool operator ==(Unit first, Unit second) => true;

    /// <summary>
    /// Determines whether two <see cref="Unit"/> instances are not equal.
    /// </summary>
    public static bool operator !=(Unit first, Unit second) => false;
}
