using System.Diagnostics;
using VRPC.Globals;

namespace VRPC.Packaging
{
    static class CreateUninstaller
    {
        public static void CreateUninstall()
        {
            // Create file in %temp%, run it and close this instance.
            string appDir = AppContext.BaseDirectory;
            string tempDirName = Path.GetRandomFileName();
            string tempDir = Path.Combine(Path.GetTempPath(), tempDirName);

            try
            {
                Directory.CreateDirectory(tempDir);
                foreach (string dirPath in Directory.GetDirectories(appDir, "*", SearchOption.AllDirectories))
                {
                    string fileName = dirPath.Replace(appDir, "");
                    string finalDir = Path.Combine(tempDir, fileName);

                    if (fileName == "Data") { continue; }
                    Directory.CreateDirectory(finalDir);
                }
                
                foreach (string filePath in Directory.GetFiles(appDir, "*.*", SearchOption.AllDirectories))
                {
                    string fileName = filePath.Replace(appDir, "");
                    string finalDir = Path.Combine(tempDir, fileName);

                    if (fileName.Contains("Data\\")) { continue; }
                    File.Copy(filePath, finalDir, true);
                }

                string tempExePath = Path.Combine(tempDir, VRPCGlobalData.exeName);
                ProcessStartInfo processStartInfo = new ProcessStartInfo
                {
                    FileName = tempExePath,
                    Arguments = "--uninstall-temp",
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(processStartInfo);

                Environment.Exit(0);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating uninstaller in temp: {e.Data}. Check your disk space.");
                Environment.Exit(1);
            }
        }
    }
}