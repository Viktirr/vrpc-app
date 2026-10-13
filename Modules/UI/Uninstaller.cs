using Microsoft.Win32;

namespace VRPC.Packaging
{
    public class UninstallWindow
    {

        public void Uninstall()
        {
            // Keeping the user's data is deliberate: Data/ holds the settings and the listening
            // history, and the uninstaller tells the user where it stays.
            bool removeData = false;
            Console.WriteLine($"You may uninstall the VRPC application by selecting Uninstall. Your settings and listening data will be kept. \n\nYou ran the uninstaller, meaning by typing Y below the application will uninstall. {PackagingGlobals.uninstallString}");

            Console.Write("Proceed with uninstallation? [N/y]: ");
            string? userInput = Console.ReadLine();
            if (userInput != null)
            {
                userInput = userInput.ToLower();
            }

            if (!(userInput == "y" || userInput == "yes" || userInput == "uninstall"))
            {
                Console.WriteLine("User likely denied uninstall. Exiting.");
                Environment.Exit(0);
            }
            Console.WriteLine("User started uninstallation.");

            Thread.Sleep(100);

            // Check installation directory
            Console.WriteLine("Selecting default installation folder.");
            string installPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRPCApp");
            Console.WriteLine($"The following installation folder was selected: {installPath}");

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                Console.WriteLine($"Checking registry key for installation folder at Software\\Viktir\\vrpc");
                try
                {
                    RegistryKey? currentUserKey = Registry.CurrentUser.OpenSubKey(@"Software\Viktir\vrpc");

                    if (currentUserKey != null)
                    {
                        string? newInstallPath = currentUserKey.GetValue("InstallPath") as string;

                        if (!string.IsNullOrEmpty(newInstallPath))
                        {
                            Console.WriteLine($"InstallPath found: {newInstallPath}");
                            Console.WriteLine($"Now using {newInstallPath} for uninstallation");
                            installPath = newInstallPath;
                        }
                        else
                        {
                            Console.WriteLine("InstallPath does not exist.");
                        }

                        currentUserKey.Close();
                    }
                    else
                    {
                        Console.WriteLine("Registry key for installation path does not exist.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An error occurred while looking for registry key installation path: {ex.Message}. Likely means the key doesn't exist, uninstallation can proceed.");
                }

                // Delete registry keys for Native Messaging & installation path
                Console.WriteLine($"Deleting registry keys for Native Messaging and Installation folder.");
                string[] registryKeys = new string[]
                {
                    @"Software\Mozilla\NativeMessagingHosts\vrpc",
                    @"Software\Google\Chrome\NativeMessagingHosts\vrpc",
                    @"Software\Microsoft\Edge\NativeMessagingHosts\vrpc",
                    @"Software\Viktir\vrpc",
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VRPCApp"
                };

                foreach (string key in registryKeys)
                {
                    try
                    {
                        using (RegistryKey? currentKey = Registry.CurrentUser.OpenSubKey(key, true))
                        {
                            if (currentKey != null)
                            {
                                string keyName = currentKey.Name;
                                Console.WriteLine($"Deleting registry: {keyName}");
                                currentKey.DeleteSubKeyTree("");
                            }
                            else
                            {
                                Console.WriteLine($"The key {currentKey} does not exist.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting {key}: {ex.Message}");
                    }
                }
            }

            // Delete contents of installation folder. Only the application files are removed, the
            // user's data stays where it is.
            string binPath = Path.Combine(installPath, "bin");
            string dataPath = Path.Combine(installPath, "Data");
            Console.WriteLine($"Deleting contents at {installPath}");
            if (Directory.Exists(installPath))
            {
                try
                {
                    string[] files;
                    if (removeData)
                    {
                        files = Directory.GetFiles(installPath, "*", SearchOption.AllDirectories);
                    }
                    else
                    {
                        files = Directory.GetFiles(binPath);
                    }
                    Console.WriteLine($"There are {files.Length} files in {(removeData ? installPath : binPath)}");

                    int filesUpperThreshold = 400;
                    if (files.Length > filesUpperThreshold)
                    {
                        Console.WriteLine($"The directory contains more than {filesUpperThreshold} files. Deletion process is canceled.");
                        Console.ReadLine();
                        Environment.Exit(1);
                        return;
                    }
                    else if (removeData)
                    {
                        Console.WriteLine($"Deleting {installPath}");
                        Directory.Delete(installPath, true);
                        Console.WriteLine("All files and directories have been deleted.");
                    }
                    else
                    {
                        Console.WriteLine($"Deleting {binPath}");
                        Directory.Delete(binPath, true);
                        Console.WriteLine($"Application files deleted. Your settings and listening data were kept in {dataPath}.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An error occurred while trying to delete files. Please delete the files manually, they should be located at {installPath}. Exception: {ex.Message}");
                    Console.WriteLine($"Press any key to exit.");
                    Console.ReadLine();
                    Environment.Exit(1);
                }
            }
            else
            {
                Console.WriteLine("The specified directory does not exist.");
            }

            Console.WriteLine($"\n\nUninstalled. Press any key to finish.");
            Console.ReadLine();

            // Additional cleanup from %temp%
            string appDir = AppContext.BaseDirectory;
            string tempDir = System.IO.Path.GetTempPath();

            if (appDir.Contains(tempDir))
            {
                Console.WriteLine($"Attempting partial cleanup from {appDir}");

                string[] files = Directory.GetFiles(appDir);

                foreach (string file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        Console.WriteLine($"Failed to delete {file}.");
                    }
                    Console.WriteLine($"Deleted {file}.");
                }
            }
        }
    }
}