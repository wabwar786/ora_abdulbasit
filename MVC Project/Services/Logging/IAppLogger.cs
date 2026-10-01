namespace Orapmshms.Services.Logging;

/// <summary>
/// Central application logger. Inject this service anywhere instead of creating
/// file-specific logging helper methods.
/// </summary>
public interface IAppLogger
{
    void Info(string message, params object?[] args);
    void Warning(string message, params object?[] args);
    void Error(Exception exception, string message, params object?[] args);
    void Debug(Exception exception, string message, params object?[] args);
}
