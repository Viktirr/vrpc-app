using System.Text;
using VRPC.Application;

namespace VRPC.Install
{
    // Setup/update log written under state/logs.
    public sealed class SetupLog
    {
        private readonly object _gate = new();
        private readonly string _path;

        public SetupLog()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                _path = System.IO.Path.Combine(AppPaths.Logs, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                Write($"VRPC setup {ProductInfo.Version}");
            }
            catch
            {
                _path = "";
            }
        }

        public string Path => _path;

        public void Write(string message)
        {
            if (string.IsNullOrEmpty(_path)) { return; }
            try
            {
                lock (_gate)
                {
                    File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public void Error(string message) => Write("[ERROR] " + message);
    }
}
