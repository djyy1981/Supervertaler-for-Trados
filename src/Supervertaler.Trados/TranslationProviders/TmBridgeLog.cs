using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Supervertaler.Trados.TranslationProviders
{
    /// <summary>
    /// Lightweight file logger for the Supervertaler TM bridge. Writes to
    /// <c>%TEMP%\supervertaler-tm-bridge.log</c>. Used to diagnose
    /// NullReferenceException and language-pair-mismatch issues that
    /// Trados surfaces only as generic "An error has occurred while using
    /// the translation provider" messages – the actual stack trace is
    /// otherwise invisible.
    ///
    /// Append-only, line-per-event, plain text. Caps the file at ~256 KB
    /// by rotating to a .1 sibling on overflow so a runaway loop can't
    /// fill the disk. Thread-safe via a lock; the underlying File.Append
    /// is the bottleneck regardless.
    ///
    /// Safe to call from any code path – every call is wrapped in a
    /// try/catch so a failed log write never breaks a translation lookup.
    /// </summary>
    internal static class TmBridgeLog
    {
        private static readonly object _gate = new object();
        private const long MaxBytes = 256 * 1024;

        private static string _logPath;

        private static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    try
                    {
                        _logPath = Path.Combine(
                            Path.GetTempPath(),
                            "supervertaler-tm-bridge.log");
                    }
                    catch
                    {
                        _logPath = string.Empty;
                    }
                }
                return _logPath;
            }
        }

        public static void Info(string message)  => Write("INFO ", message, null);
        public static void Warn(string message)  => Write("WARN ", message, null);
        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                var path = LogPath;
                if (string.IsNullOrEmpty(path)) return;

                lock (_gate)
                {
                    RotateIfTooLarge(path);

                    var sb = new StringBuilder(256);
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    sb.Append(' ');
                    sb.Append('[');
                    sb.Append(Thread.CurrentThread.ManagedThreadId.ToString("D2"));
                    sb.Append("] ");
                    sb.Append(level);
                    sb.Append(' ');
                    sb.Append(message ?? string.Empty);
                    if (ex != null)
                    {
                        sb.Append(" :: ");
                        sb.Append(ex.GetType().Name);
                        sb.Append(": ");
                        sb.Append(ex.Message ?? string.Empty);
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            sb.Append('\n');
                            sb.Append(ex.StackTrace);
                        }
                    }
                    sb.Append('\n');

                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // A log-write failure must never break the actual lookup.
            }
        }

        private static void RotateIfTooLarge(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length < MaxBytes) return;

                var rotated = path + ".1";
                if (File.Exists(rotated)) File.Delete(rotated);
                File.Move(path, rotated);
            }
            catch
            {
                // Best-effort rotation – ignore any IO hiccups.
            }
        }
    }
}
