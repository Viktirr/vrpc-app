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
            // The log normally lives inside the installation, but a failure to
            // create that folder is exactly the case where the log matters most.
            // %TEMP% covers a permission problem; the package folder covers a
            // process that may only write next to itself (low integrity), where
            // %TEMP% is out of reach too.
            _path = TryCreate(AppPaths.Logs)
                    ?? TryCreate(System.IO.Path.GetTempPath())
                    ?? TryCreate(AppContext.BaseDirectory)
                    ?? "";
            Write($"VRPC setup {ProductInfo.Version}");
            if (_path.Length > 0 && !AppPaths.IsUnderRoot(_path))
            {
                Write($"The install folder {AppPaths.Root} is not usable; this log was written outside it.");
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

        private static string? TryCreate(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                return System.IO.Path.Combine(directory, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            }
            catch
            {
                return null;
            }
        }
    }
}
