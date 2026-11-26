using System.Diagnostics;
using VRPC.Application;
using VRPC.Deployment.Install;

namespace VRPC.Deployment.Setup
{
    // Copies the app to %TEMP% so the original app/ can be deleted.
    public static class UninstallLauncher
    {
        // Passes the original root and the caller's choices to the temp
        // uninstaller. Every flag has to be forwarded: the relaunched process is
        // a fresh one, and --yes in particular was being dropped, so an
        // unattended uninstall stopped at a prompt it could not answer.
        //
        // The copy and the handoff happen under the setup-operation lock so a
        // concurrent update cannot swap app/ while it is being copied; the child
        // acquires the same lock independently before it mutates anything.
        internal static int Launch(SetupOptions options)
        {
            SetupOperationLock operationLock;
            try
            {
                operationLock = SetupOperationLock.Acquire();
            }
            catch (InstallException e)
            {
                Console.Error.WriteLine(e.Message);
                return e.ExitCode;
            }

            try
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
                    if (options.Quiet) { arguments.Add("--quiet"); }
                    if (options.Yes) { arguments.Add("--yes"); }
                    if (options.PurgeData) { arguments.Add("--purge"); }
                    if (options.Force) { arguments.Add("--force"); }
                    if (options.UseConsole) { arguments.Add("--console"); }
                    if (options.UsePanel) { arguments.Add("--panel"); }

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
            finally
            {
                operationLock.Dispose();
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
