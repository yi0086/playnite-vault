using System;
using System.IO;
using System.Text;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 插件日志。写到插件私有数据目录，排错时直接看这个文件。
    /// </summary>
    public static class VaultLog
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static string logPath;

        public static string LogPath
        {
            get { return logPath; }
        }

        public static void Init(string dataPath)
        {
            lock (Gate)
            {
                logPath = Path.Combine(dataPath, "playnite-vault.log");
            }
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
        }

        public static void Warn(string message)
        {
            Write("WARN ", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            var line = message;
            if (ex != null)
            {
                line += " | " + ex.GetType().Name + ": " + ex.Message;
            }
            Write("ERROR", line);
        }

        private static void Write(string level, string message)
        {
            if (logPath == null)
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > MaxBytes)
                    {
                        File.Delete(logPath);
                    }

                    var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                        DateTime.Now, level, message, Environment.NewLine);
                    File.AppendAllText(logPath, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志失败绝不能影响主流程
            }
        }
    }
}
