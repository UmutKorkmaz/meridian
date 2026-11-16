namespace Meridian.Mediator;

/// <summary>
/// Opt-in settings for the <c>Meridian.Mediator</c> <see cref="System.Diagnostics.Metrics.Meter"/>.
/// Metrics are disabled by default: a disabled provider creates no meter, no instruments and no admission table.
/// </summary>
/// <remarks>
/// The options are an immutable snapshot taken when the provider-owned
/// <see cref="Diagnostics.MediatorMetrics"/> component is created. Nothing here installs an exporter or a
/// periodic monitor; listeners (OpenTelemetry, <c>dotnet-counters</c>, <see cref="System.Diagnostics.Metrics.MeterListener"/>)
/// are attached by the application.
/// </remarks>
public sealed record MediatorMetricsOptions
{
    /// <summary>Shared default options instance (metrics disabled).</summary>
    public static MediatorMetricsOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the mediator publishes metrics. Defaults to <c>false</c>.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets or sets the maximum number of distinct message types that receive their own
    /// <c>request.type</c> tag value per provider. Every other type shares the single constant tag value
    /// <c>overflow</c>. Defaults to 100. Zero admits no types (overflow only); a negative value is a startup
    /// configuration error.
    /// </summary>
    /// <remarks>
    /// Admission is decided once at startup from the closed request, notification and stream types known to the
    /// registration (generated registry manifests and closed handler registrations present when
    /// <c>AddMeridianMediator</c> ran), ordered by their canonical name. It is never decided by first use at run time.
    /// </remarks>
    public int MaximumRequestTypes { get; init; } = 100;
}
