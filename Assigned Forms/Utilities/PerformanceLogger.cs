using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Web;

namespace hotelsoftware.Utilities
{
    public static class PerformanceLogger
    {
        private static readonly object FileLock = new object();

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
        {
            return new PerformanceSession(
                operationName,
                hotelId,
                hotelName,
                userId,
                dateRange,
                eventName,
                additionalInfo,
                minimumLogMs,
                slowStepMs,
                verySlowTotalMs,
                callerFile,
                callerMember,
                callerLine);
        }
        public sealed class PerformanceSession
        {
            private readonly Stopwatch _totalWatch = Stopwatch.StartNew();
            private readonly StringBuilder _steps = new StringBuilder();
            private readonly string _operationName;
            private readonly string _hotelId;
            private readonly string _hotelName;
            private readonly string _userId;
            private readonly string _dateRange;
            private readonly string _eventName;
            private readonly string _additionalInfo;
            private readonly long _minimumLogMs;
            private readonly long _slowStepMs;
            private readonly long _verySlowTotalMs;
            private readonly string _sourceFile;
            private readonly string _callerMember;
            private readonly int _callerLine;
            private readonly string _pageName;
            private readonly string _requestUrl;
            private readonly string _logId;
            private bool _completed;
            internal PerformanceSession(
                string operationName,
                string hotelId,
                string hotelName,
                string userId,
                string dateRange,
                string eventName,
                string additionalInfo,
                long minimumLogMs,
                long slowStepMs,
                long verySlowTotalMs,
                string callerFile,
                string callerMember,
                int callerLine)
            {
                _operationName = Clean(operationName);
                _hotelId = Clean(hotelId);
                _hotelName = Clean(hotelName);
                _userId = Clean(userId);
                _dateRange = Clean(dateRange);
                _eventName = Clean(eventName);
                _additionalInfo = Clean(additionalInfo);
                _minimumLogMs = minimumLogMs;
                _slowStepMs = slowStepMs;
                _verySlowTotalMs = verySlowTotalMs;
                _sourceFile = string.IsNullOrWhiteSpace(callerFile) ? "" : Path.GetFileName(callerFile);
                _callerMember = Clean(callerMember);
                _callerLine = callerLine;
                _logId = Guid.NewGuid().ToString("N").Substring(0, 8);

                HttpContext context = HttpContext.Current;
                if (context != null && context.Request != null)
                {
                    _pageName = context.Request.AppRelativeCurrentExecutionFilePath ?? "";
                    _requestUrl = context.Request.Url != null ? context.Request.Url.PathAndQuery : "";
                }
                else
                {
                    _pageName = "BACKGROUND";
                    _requestUrl = "";
                }
            }
            public void Measure(string stepName, Action action)
            {
                if (action == null) return;
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    action();
                }
                finally
                {
                    watch.Stop();
                    AddStep(stepName, watch.ElapsedMilliseconds);
                }
            }

            public T Measure<T>(string stepName, Func<T> action)
            {
                if (action == null) return default(T);
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    return action();
                }
                finally
                {
                    watch.Stop();
                    AddStep(stepName, watch.ElapsedMilliseconds);
                }
            }

            public void AddStep(string stepName, long milliseconds)
            {
                _steps.Append(" | ");
                _steps.Append(Clean(stepName));
                _steps.Append("=");
                _steps.Append(milliseconds);
                _steps.Append("ms");
                if (milliseconds >= _slowStepMs)
                    _steps.Append("[SLOW]");
            }

            public void AddInfo(string name, string value)
            {
                _steps.Append(" | ");
                _steps.Append(Clean(name));
                _steps.Append("=");
                _steps.Append(Clean(value));
            }

            public void Complete(bool success = true, Exception exception = null)
            {
                if (_completed) return;
                _completed = true;
                _totalWatch.Stop();

                long totalMilliseconds = _totalWatch.ElapsedMilliseconds;
                if (totalMilliseconds < _minimumLogMs) return;

                try
                {
                    StringBuilder log = new StringBuilder();
                    log.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    log.Append(" | LogId=").Append(_logId);
                    log.Append(" | Operation=").Append(_operationName);
                    if (!string.IsNullOrWhiteSpace(_pageName)) log.Append(" | Page=").Append(_pageName);
                    if (!string.IsNullOrWhiteSpace(_sourceFile)) log.Append(" | SourceFile=").Append(_sourceFile);
                    if (!string.IsNullOrWhiteSpace(_callerMember)) log.Append(" | Caller=").Append(_callerMember);
                    if (_callerLine > 0) log.Append(" | Line=").Append(_callerLine);
                    if (!string.IsNullOrWhiteSpace(_hotelId)) log.Append(" | HotelId=").Append(_hotelId);
                    if (!string.IsNullOrWhiteSpace(_hotelName)) log.Append(" | HotelName=").Append(_hotelName);
                    if (!string.IsNullOrWhiteSpace(_userId)) log.Append(" | User=").Append(_userId);
                    if (!string.IsNullOrWhiteSpace(_dateRange)) log.Append(" | Range=").Append(_dateRange);
                    if (!string.IsNullOrWhiteSpace(_eventName)) log.Append(" | Event=").Append(_eventName);
                    if (!string.IsNullOrWhiteSpace(_requestUrl)) log.Append(" | Url=").Append(_requestUrl);
                    log.Append(" | Server=").Append(Environment.MachineName);
                    log.Append(" | PID=").Append(Process.GetCurrentProcess().Id);
                    log.Append(" | Thread=").Append(Thread.CurrentThread.ManagedThreadId);
                    if (!string.IsNullOrWhiteSpace(_additionalInfo)) log.Append(" | Info=").Append(_additionalInfo);
                    log.Append(_steps.ToString());
                    log.Append(" | TOTAL=").Append(totalMilliseconds).Append("ms");
                    if (totalMilliseconds >= _verySlowTotalMs) log.Append("[VERY SLOW]");
                    else if (totalMilliseconds >= _slowStepMs) log.Append("[SLOW]");
                    log.Append(" | Status=").Append(success ? "SUCCESS" : "FAILED");
                    if (exception != null)
                    {
                        log.Append(" | ErrorType=").Append(exception.GetType().Name);
                        log.Append(" | Error=").Append(Clean(exception.Message));
                    }
                    log.Append(Environment.NewLine);
                    WriteToFile(log.ToString());
                }
                catch
                {
                }
            }
        }
        private static void WriteToFile(string message)
        {
            try
            {
                HttpContext context = HttpContext.Current;
                string folder = context != null
                    ? context.Server.MapPath("~/App_Data/PerformanceLogs")
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "PerformanceLogs");

                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                string filePath = Path.Combine(
                    folder,
                    "PMSPerformance_" + DateTime.Now.ToString("yyyyMMdd") + ".log");

                lock (FileLock)
                {
                    File.AppendAllText(filePath, message, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
        }
    }
}
