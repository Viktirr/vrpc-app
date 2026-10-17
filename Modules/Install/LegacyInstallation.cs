using System.Diagnostics;
using VRPC.Application;

namespace VRPC.Install
{
    // Detects the 1.0.x %APPDATA%\VRPCApp layout and its version.
    public sealed class LegacyInstallation
    {
        public string Root { get; init; } = AppPaths.LegacyRoot;
        public string AppExe { get; init; } = Path.Combine(AppPaths.LegacyApp, AppPaths.AppExeName);
        public string DataDir { get; init; } = AppPaths.LegacyData;
        public string Version { get; init; } = "0.0.0";

        public bool Exists => File.Exists(AppExe) || Directory.Exists(DataDir);

        public static LegacyInstallation? Detect()
        {
            string exe = Path.Combine(AppPaths.LegacyApp, AppPaths.AppExeName);
            string data = AppPaths.LegacyData;
            if (!File.Exists(exe) && !Directory.Exists(data)) { return null; }

            string version = "0.0.0";
            try
            {
                if (File.Exists(exe))
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                    version = Normalize(info.ProductVersion) ?? Normalize(info.FileVersion) ?? "0.0.0";
                }
            }
            catch { }

            return new LegacyInstallation { Version = version };
        }

        private static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return null; }
            string normalized = value.Split('+')[0].Trim();
            return ProductInfo.TryParseVersion(normalized, out _) ? normalized : null;
        }
    }

    // Finds processes by executable path, not just name.
    public static class ProcessHelper
    {
        public static bool IsRunning(string exePath)
        {
            foreach (Process process in FindByPath(exePath))
            {
                process.Dispose();
                return true;
            }
            return false;
        }

        public static bool WaitForExit(string exePath, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsRunning(exePath)) { return true; }
                Thread.Sleep(200);
            }
            return !IsRunning(exePath);
        }

        public static bool Kill(string exePath)
        {
            bool killed = false;
            foreach (Process process in FindByPath(exePath))
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(5000);
                    killed = true;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return killed;
        }

        public static IEnumerable<Process> FindByPath(string exePath)
        {
            string expected = Path.GetFullPath(exePath);
            string processName = Path.GetFileNameWithoutExtension(exePath);
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { yield break; }

            foreach (Process process in processes)
            {
                bool matches = false;
                try
                {
                    string? path = process.MainModule?.FileName;
                    matches = path is not null && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                finally
                {
                    if (!matches) { process.Dispose(); }
                }

                if (matches) { yield return process; }
            }
        }

        public static int? ReadPid(string pidPath)
        {
            try
            {
                if (!File.Exists(pidPath)) { return null; }
                string text = File.ReadAllText(pidPath).Trim();
                return int.TryParse(text, out int pid) ? pid : null;
            }
            catch { return null; }
        }
    }
}
