using System.Diagnostics;
using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Abstract logging provider. Users implement for their logger (Serilog, NLog, etc.).
/// </summary>
public interface IMediatorLogger
{
    /// <summary>
    /// Logs an informational message.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="args">The message arguments.</param>
    void LogInformation(string message, params object[] args);

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="args">The message arguments.</param>
    void LogWarning(string message, params object[] args);

    /// <summary>
    /// Logs an error message with an associated exception.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="message">The message template.</param>
    /// <param name="args">The message arguments.</param>
    void LogError(Exception exception, string message, params object[] args);
}

/// <summary>
/// Pipeline behavior that logs request execution with timing information.
/// Logs the start, successful completion (with elapsed time), and any errors for every request.
/// Uses <see cref="IMediatorLogger"/> so users can plug in their preferred logging framework.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IMediatorLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoggingBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="logger">The mediator logger.</param>
    public LoggingBehavior(IMediatorLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        _logger.LogInformation("Handling {RequestName}", requestName);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next();
            stopwatch.Stop();

            _logger.LogInformation("Handled {RequestName} in {ElapsedMilliseconds}ms", requestName, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(ex, "Error handling {RequestName} after {ElapsedMilliseconds}ms", requestName, stopwatch.ElapsedMilliseconds);

            throw;
        }
    }
}
