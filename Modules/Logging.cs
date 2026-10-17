using System.Text;
using VRPC.Application;
using VRPC.Configuration;

namespace VRPC.Logging
{
    // Thread-safe runtime log under state\logs with size rotation.
    class Log
    {
        private const long MaxLogBytes = 5 * 1024 * 1024;
        private const int KeepRotatedFiles = 5;
        private static readonly object Gate = new();
        private readonly string filePath = VRPCSettings.LogPath;

        public void Clear()
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.Logs);
                    File.WriteAllText(filePath, string.Empty, new UTF8Encoding(false));
                    foreach (string rotated in RotatedPaths())
                    {
                        try { File.Delete(rotated); } catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Clearing logs failed: {e.Message}");
            }
        }

        public void Write(string? content)
        {
            if (VRPCSettings.settingsData.LoggingWriteEnabled == false) { return; }
            Append(content);
        }

        public void Error(string? content) => Append("[ERROR] " + content);

        public void Warn(string? content) => Append("[WARNING] " + content);

        public void Info(string? content) => Append("[INFO] " + content);

        private void Append(string? content)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.Logs);
                    RotateIfNeeded();
                    string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}";
                    File.AppendAllText(filePath, logEntry + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Logging failed: {e.Message}. Data to log: {content}");
            }
        }

        private void RotateIfNeeded()
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length < MaxLogBytes) { return; }

            for (int index = KeepRotatedFiles - 1; index >= 1; index--)
            {
                string source = $"{filePath}.{index}";
                string destination = $"{filePath}.{index + 1}";
                try
                {
                    if (File.Exists(destination)) { File.Delete(destination); }
                    if (File.Exists(source)) { File.Move(source, destination); }
                }
                catch { }
            }

            try { File.Move(filePath, filePath + ".1", overwrite: true); } catch { }
        }

        private IEnumerable<string> RotatedPaths()
        {
            for (int index = 1; index <= KeepRotatedFiles; index++)
            {
                yield return $"{filePath}.{index}";
            }
        }
    }
}
