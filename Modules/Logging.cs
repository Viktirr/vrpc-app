using System.IO;
using VRPC.Configuration;
using VRPC.NativeMessasing;

namespace VRPC.Logging
{
    class Log
    {
        private bool writeEnabled = VRPCSettings.LoggingWriteEnabled;
        private string filePath = VRPCSettings.LogPath;

        public void Clear()
        {
            try
            {
                File.WriteAllText(filePath, string.Empty);
            }
            catch (Exception e)
            {
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"Clearing logs failed with exception {e.Data}."));
            }
        }

        public void Write(string? content)
        {
            if (!writeEnabled) { return; }
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}";

                File.AppendAllText(filePath, logEntry + Environment.NewLine);
            }
            catch (Exception e)
            {
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"Logging failed with exception {e.Data}. Data to log: {content}"));
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
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"Logging (type: error) failed with exception {e.Data}. Data to log: {content}"));
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
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"Logging (type: warning) failed with exception {e.Data}. Data to log: {content}"));
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
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"Logging (type: info) failed with exception {e.Data}. Data to log: {content}"));
            }
        }
    }
}