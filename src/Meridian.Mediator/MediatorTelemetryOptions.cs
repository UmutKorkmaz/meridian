namespace Meridian.Mediator;

/// <summary>
/// Controls which exception details are emitted to mediator activities.
/// </summary>
public sealed record MediatorTelemetryOptions
{
    /// <summary>
    /// Shared default options instance used when no override is registered.
    /// </summary>
    public static MediatorTelemetryOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether exception messages are written to
    /// activity status descriptions and <c>exception.message</c>.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool RecordExceptionMessage { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether full exception stack traces are
    /// written to <c>exception.stacktrace</c>. Defaults to <c>false</c>.
    /// </summary>
    public bool RecordExceptionStackTrace { get; init; }
}
