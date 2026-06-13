using VRPC.Configuration;

namespace VRPC.Logging
{
    class Log
    {
        private string filePath = VRPCSettings.LogPath;

        public void Clear()
        {
            try
            {
                File.WriteAllText(filePath, string.Empty);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Clearing logs failed: {e.Message}");
            }
        }

        public void Write(string? content)
        {
            if (VRPCSettings.settingsData.LoggingWriteEnabled == false) { return; }
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}";

                File.AppendAllText(filePath, logEntry + Environment.NewLine);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Logging failed: {e.Message}. Data to log: {content}");
            }
        }

        public void Error(string? content)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {content}";

                File.AppendAllText(filePath, logEntry + Environment.NewLine);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Logging (type: error) failed: {e.Message}. Data to log: {content}");
            }
        }

        public void Warn(string? content)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [WARNING] {content}";

                File.AppendAllText(filePath, logEntry + Environment.NewLine);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Logging (type: warning) failed: {e.Message}. Data to log: {content}");
            }
        }

        public void Info(string? content)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] {content}";

                File.AppendAllText(filePath, logEntry + Environment.NewLine);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Logging (type: info) failed: {e.Message}. Data to log: {content}");
            }
        }
    }
}