using System.IO;

namespace VRPC.Logging
{
    class Log
    {
        private string _filePath = "placeholder\\Log.txt";

        public void Write(string? content)
        {
            try
            {
                // Format the log message with the current timestamp
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}";

                // Append the log entry to the file
                File.AppendAllText(_filePath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Logging failed with exception {ex.Data}.");
            }
        }
    }
}