using System.Diagnostics;
using VRPC.Application;

namespace VRPC.Setup
{
    // Copies the app to %TEMP% so the original app/ can be deleted.
    public static class UninstallLauncher
    {
        // Passes the original root to the temp uninstaller.
        public static int Launch(bool quiet, bool purge, bool force)
        {
            if (!Directory.Exists(AppPaths.App) || !File.Exists(AppPaths.AppExe))
            {
                Console.Error.WriteLine("The installed application folder was not found.");
                return 1;
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "VRPC-uninstall-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(AppPaths.App, tempRoot);
                string exe = Path.Combine(tempRoot, AppPaths.AppExeName);
                List<string> arguments = new()
                {
                    "--uninstall-temp",
                    "--root", AppPaths.Root
                };
                if (quiet) { arguments.Add("--quiet"); }
                if (purge) { arguments.Add("--purge"); }
                if (force) { arguments.Add("--force"); }

                ProcessStartInfo startInfo = new()
                {
                    FileName = exe,
                    Arguments = string.Join(" ", arguments.Select(Quote)),
                    UseShellExecute = false,
                    WorkingDirectory = tempRoot
                };

                if (Process.Start(startInfo) is null)
                {
                    Console.Error.WriteLine("The temporary uninstaller did not start.");
                    return 1;
                }

                Console.WriteLine("The uninstaller was started in a temporary folder.");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Could not create the temporary uninstaller: " + e.Message);
                return 1;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static string Quote(string value)
        {
            return value.Contains(' ') ? $"\"{value}\"" : value;
        }
    }
}
