using System.IO;
using VRPC.Configuration;
using VRPC.NativeMessasing;

namespace VRPC.Logging
{
    class Log
    {
        private string filePath = VRPCSettings.LogPath;

        public void Write(string? content)
        {
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
    }
}