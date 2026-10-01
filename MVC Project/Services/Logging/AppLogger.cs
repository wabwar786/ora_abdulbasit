namespace Orapmshms.Services.Logging;

/// <summary>
/// Shared logging implementation used throughout the MVC application.
/// </summary>
public sealed class AppLogger : IAppLogger
{
    private readonly ILogger<AppLogger> _logger;

    public AppLogger(ILogger<AppLogger> logger)
    {
        _logger = logger;
    }

    public void Info(string message, params object?[] args)
        => _logger.LogInformation(message, args);

    public void Warning(string message, params object?[] args)
        => _logger.LogWarning(message, args);

    public void Error(Exception exception, string message, params object?[] args)
        => _logger.LogError(exception, message, args);

    public void Debug(Exception exception, string message, params object?[] args)
        => _logger.LogDebug(exception, message, args);
}
