using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orapmshms.Services.AvailabilityJobs;

/// <summary>
/// Small ASP.NET Core-safe version of the legacy utility PerformanceLogger.
/// It keeps the same API used by the migrated background availability utilities,
/// without System.Web/HttpContext dependencies.
/// </summary>
internal static class PerformanceLogger
{
    private static readonly object FileLock = new();
    public const long DefaultSlowStepMs = 3000;
    public const long DefaultVerySlowTotalMs = 10000;
    public const long DefaultMinimumLogMs = 0;

    public static PerformanceSession Start(
        string operationName,
        string hotelId = "",
        string hotelName = "",
        string userId = "",
        string dateRange = "",
        string eventName = "",
        string additionalInfo = "",
        long minimumLogMs = DefaultMinimumLogMs,
        long slowStepMs = DefaultSlowStepMs,
        long verySlowTotalMs = DefaultVerySlowTotalMs,
        [CallerFilePath] string callerFile = "",
        [CallerMemberName] string callerMember = "",
        [CallerLineNumber] int callerLine = 0)
        => new(operationName, hotelId, hotelName, userId, dateRange, eventName, additionalInfo,
            minimumLogMs, slowStepMs, verySlowTotalMs, callerFile, callerMember, callerLine);

    internal sealed class PerformanceSession
    {
        private readonly Stopwatch _totalWatch = Stopwatch.StartNew();
        private readonly StringBuilder _steps = new();
        private readonly string _operationName, _hotelId, _hotelName, _userId, _dateRange,
            _eventName, _additionalInfo, _sourceFile, _callerMember;
        private readonly long _minimumLogMs, _slowStepMs, _verySlowTotalMs;
        private readonly int _callerLine;
        private bool _completed;

        internal PerformanceSession(string operationName, string hotelId, string hotelName, string userId,
            string dateRange, string eventName, string additionalInfo, long minimumLogMs,
            long slowStepMs, long verySlowTotalMs, string callerFile, string callerMember, int callerLine)
        {
            _operationName = Clean(operationName); _hotelId = Clean(hotelId); _hotelName = Clean(hotelName);
            _userId = Clean(userId); _dateRange = Clean(dateRange); _eventName = Clean(eventName);
            _additionalInfo = Clean(additionalInfo); _minimumLogMs = minimumLogMs;
            _slowStepMs = slowStepMs; _verySlowTotalMs = verySlowTotalMs;
            _sourceFile = string.IsNullOrWhiteSpace(callerFile) ? "" : Path.GetFileName(callerFile);
            _callerMember = Clean(callerMember); _callerLine = callerLine;
        }

        public void Measure(string stepName, Action action)
        {
            if (action == null) return;
            var sw = Stopwatch.StartNew();
            try { action(); }
            finally { sw.Stop(); AddStep(stepName, sw.ElapsedMilliseconds); }
        }

        public T Measure<T>(string stepName, Func<T> action)
        {
            if (action == null) return default!;
            var sw = Stopwatch.StartNew();
            try { return action(); }
            finally { sw.Stop(); AddStep(stepName, sw.ElapsedMilliseconds); }
        }

        public void AddStep(string stepName, long milliseconds)
        {
            _steps.Append(" | ").Append(Clean(stepName)).Append('=').Append(milliseconds).Append("ms");
            if (milliseconds >= _slowStepMs) _steps.Append("[SLOW]");
        }

        public void AddInfo(string name, string value) =>
            _steps.Append(" | ").Append(Clean(name)).Append('=').Append(Clean(value));

        public void Complete(bool success = true, Exception? exception = null)
        {
            if (_completed) return;
            _completed = true;
            _totalWatch.Stop();
            if (_totalWatch.ElapsedMilliseconds < _minimumLogMs) return;

            try
            {
                var log = new StringBuilder();
                log.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                log.Append(" | Operation=").Append(_operationName);
                if (_sourceFile.Length > 0) log.Append(" | SourceFile=").Append(_sourceFile);
                if (_callerMember.Length > 0) log.Append(" | Caller=").Append(_callerMember);
                if (_callerLine > 0) log.Append(" | Line=").Append(_callerLine);
                if (_hotelId.Length > 0) log.Append(" | HotelId=").Append(_hotelId);
                if (_hotelName.Length > 0) log.Append(" | HotelName=").Append(_hotelName);
                if (_userId.Length > 0) log.Append(" | User=").Append(_userId);
                if (_dateRange.Length > 0) log.Append(" | Range=").Append(_dateRange);
                if (_eventName.Length > 0) log.Append(" | Event=").Append(_eventName);
                if (_additionalInfo.Length > 0) log.Append(" | Info=").Append(_additionalInfo);
                log.Append(_steps);
                log.Append(" | TOTAL=").Append(_totalWatch.ElapsedMilliseconds).Append("ms");
                if (_totalWatch.ElapsedMilliseconds >= _verySlowTotalMs) log.Append("[VERY SLOW]");
                log.Append(" | Status=").Append(success ? "SUCCESS" : "FAILED");
                if (exception != null) log.Append(" | Error=").Append(Clean(exception.Message));
                log.AppendLine();

                var folder = Path.Combine(AppContext.BaseDirectory, "App_Data", "PerformanceLogs");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "PMSPerformance_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                lock (FileLock) File.AppendAllText(file, log.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value)
        ? "" : value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
}
