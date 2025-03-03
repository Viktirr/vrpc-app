using Microsoft.Win32;
using Newtonsoft.Json;
using VRPC.Globals;

namespace VRPC.Packaging
{
    public class ManifestFileData
    {
        public string name { get; set; }
        public string description { get; set; }
        public string path { get; set; }
        public string type { get; set; }
        public string[] allowed_extensions { get; set; }

        public ManifestFileData(string _path)
        {
            name = "vrpc";
            description = "Transfers Rich Presence data to Discord and saves Listening Data locally.";
            path = _path;
            type = "stdio";
            allowed_extensions = new string[] { "vrpc@viktir.com" };
        }
    }

    public class ManifestFileDataChrome
    {
        public string name { get; set; }
        public string description { get; set; }
        public string path { get; set; }
        public string type { get; set; }
        public string[] allowed_origins { get; set; }

        public ManifestFileDataChrome(string _path)
        {
            name = "vrpc";
            description = "Transfers Rich Presence data to Discord and saves Listening Data locally.";
            path = _path;
            type = "stdio";
            allowed_origins = new string[] { "chrome-extension://dmfcnhgakihhigbkpefjgjkbbnkjagpb/" };
        }
    }

    public class InstallWindow
    {
        public void Install()
        {
            Console.WriteLine("VPRC is an application that allows communication between the extension and client to provide Rich Presence data to Discord alongside other features.\n\nBy clicking Install below, you agree to the License, Terms of Use and Privacy Policy of this application listed over at https://viktir.com/vrpc\n\nIf you do not agree with any of the previous notices, refrain from installing this software by clicking the Cancel button or exiting the program.\n\nBy selecting install the following will occur:\nThe files for this application will extract at %appdata%\\VRPCApp\nA few registry keys will be created to allow for Native Messaging permissions to work with the extension and to create an entry for uninstalling the application in the Settings app.");

            Console.Write("Proceed with installation? [N/y]: ");
            string? userInput = Console.ReadLine();
            if (userInput != null)
            {
                userInput = userInput.ToLower();
            }

            if (!(userInput == "y" || userInput == "yes" || userInput == "install"))
            {
                Console.WriteLine("User likely denied install. Exiting.");
                Environment.Exit(0);
            }

            Console.WriteLine("User started installation.");

            Thread.Sleep(200);

            // Set variables for installation
            string roamingAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appNamePath = System.IO.Path.Combine(roamingAppDataPath, VRPCGlobalData.appName);

            string sourceFilePath = AppContext.BaseDirectory;
            string destinationFilePath = System.IO.Path.Combine(appNamePath, "VRPC.exe");

            // Create folder
            Console.WriteLine($"Creating folder {appNamePath}");

            try
            {
                if (!Directory.Exists(appNamePath))
                {
                    Directory.CreateDirectory(appNamePath);
                    Console.WriteLine($"Folder {appNamePath} created successfully");
                }
            }
            catch
            {
                Console.WriteLine($"Error: Couldn't create folder {appNamePath}. Installation cannot proceed. Check your available storage space. Press close to exit.");
                Console.WriteLine($"Press any key to exit.");
                Console.ReadLine();
                Environment.Exit(1);
                return;
            }

            // Check storage requirements
            Console.WriteLine($"Checking available free space...");
            try
            {
                string? rootPath = System.IO.Path.GetPathRoot(roamingAppDataPath);
                Console.WriteLine($"Checking {rootPath} for free space...");
                if (rootPath == null)
                {
                    Console.WriteLine($"Unable to determine the root path (attempt: {rootPath}) to determine free space. Check your available storage space.");
                    Console.WriteLine($"Press any key to exit.");
                    Console.ReadLine();
                    Environment.Exit(1);
                    return;
                }

                DriveInfo driveInfo = new DriveInfo(rootPath);
                Console.WriteLine($"Unable to determine {rootPath} to determine free space.");
                if (driveInfo.IsReady)
                {
                    long installSpace = 100 * 1024 * 1024; // 100 MiB
                    long availableFreeSpace = driveInfo.AvailableFreeSpace;

                    if (availableFreeSpace < installSpace)
                    {
                        Console.WriteLine($"Error: Not enough free space. Please free up some storage and try again. No changes were done.");
                        Console.WriteLine($"Press any key to exit.");
                        Console.ReadLine();
                        Environment.Exit(1);
                        return;
                    }
                }
            }
            catch
            {
                Console.WriteLine($"Error: Couldn't check storage space. Installation cannot proceed. Aborting.");
                Console.WriteLine($"Press any key to exit.");
                Console.ReadLine();
                Environment.Exit(1);
                return;
            }

            // Check directory of program - Checks if there are more files than the installer should ever have and if the file sizes are greater than the installer should have.
            Console.WriteLine($"Initialising check from the installer {sourceFilePath}");
            string[] filesInDirectory = Directory.GetFiles(sourceFilePath);

            int maxFilesInDirectory = 270;
            if (filesInDirectory.Length > maxFilesInDirectory)
            {
                Console.WriteLine($"There are more files than there should be ({filesInDirectory.Length}/{maxFilesInDirectory}) in the installer directory. Aborting.");
                Console.WriteLine($"Press any key to exit.");
                Console.ReadLine();
                Environment.Exit(1);
                return;
            }

            if (filesInDirectory.Length > 1) // This check is made in case the application is built on single file compile.
            {
                long totalSize = 0;
                foreach (string file in filesInDirectory)
                {
                    FileInfo fileInfo = new FileInfo(file);
                    totalSize += fileInfo.Length;

                    if (fileInfo.Length > 20 * 1024 * 1024 || totalSize > 150 * 1024 * 1024)
                    {
                        Console.WriteLine($"One of the files is larger than 20 MiB or the total amount of files exceed 150 MiB, assuming these are not the files from the program. If you didn't do so already, extract the files to a folder. Aborting.");
                        Console.WriteLine($"Press any key to exit.");
                        Console.ReadLine();
                        Environment.Exit(1);
                        return;
                    }
                }
            }

            // Copy program to Application Data
            Console.WriteLine($"Initialising file copy from {sourceFilePath} to {destinationFilePath}.");

            try
            {
                foreach (string file in filesInDirectory)
                {
                    string fileName = System.IO.Path.GetFileName(file);

                    if (fileName.Contains("VRPCInstall")) { fileName = "VRPC.exe"; }
                    string destFile = System.IO.Path.Combine(appNamePath, fileName);
                    File.Copy(file, destFile, true);
                    Console.WriteLine($"Copied {file} to {destFile}");
                }
            }
            catch
            {
                Console.WriteLine($"Error: Couldn't copy file. Aborting.");
                Console.WriteLine($"Press any key to exit.");
                Console.ReadLine();
                Environment.Exit(1);
                return;
            }

            // Create manifest file
            Console.WriteLine($"Creating manifest file at {appNamePath}");

            ManifestFileData manifestFileData = new ManifestFileData($"{appNamePath}\\VRPC.exe");
            ManifestFileDataChrome manifestFileDataChrome = new ManifestFileDataChrome($"{appNamePath}\\VRPC.exe");

            string manifestFileDataJson = JsonConvert.SerializeObject(manifestFileData, Formatting.Indented);
            File.WriteAllText(System.IO.Path.Combine(appNamePath, "vrpc.json"), manifestFileDataJson);
            Console.WriteLine($"Manifest file {System.IO.Path.Combine(appNamePath, "vrpc.json")} created.");

            string manifestFileDataChromeJson = JsonConvert.SerializeObject(manifestFileDataChrome, Formatting.Indented);
            File.WriteAllText(System.IO.Path.Combine(appNamePath, "vrpc-chrome.json"), manifestFileDataChromeJson);
            Console.WriteLine($"Manifest file {System.IO.Path.Combine(appNamePath, "vrpc-chrome.json")} created.");

            // Create registry
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                Console.WriteLine($"Initialising registry key creation.");
                try
                {
                    string currentKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VRPCApp";
                    Console.WriteLine($"Creating registry key for uninstallation at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue("DisplayName", "VRPC Application");
                        key.SetValue("DisplayIcon", destinationFilePath);
                        key.SetValue("Publisher", "Viktir");
                        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                        key.SetValue("InstallLocation", appNamePath);
                        key.SetValue("UninstallString", $"{destinationFilePath} --uninstall");
                        key.SetValue("NoModify", 1);
                        key.SetValue("NoRepair", 1);
                    }

                    currentKey = @"Software\Viktir\vrpc";
                    Console.WriteLine($"Creating registry key for the application at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue("InstallPath", $"{appNamePath}");
                        key.SetValue("ManifestPath", $"{appNamePath}\\vrpc.json");
                        key.SetValue("ManifestChromePath", $"{appNamePath}\\vrpc-chrome.json");
                    }

                    currentKey = @"Software\Mozilla\NativeMessagingHosts\vrpc";
                    Console.WriteLine($"Creating registry key for Firefox Native Messaging support at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue(null, $"{appNamePath}\\vrpc.json");
                    }

                    currentKey = @"Software\Google\Chrome\NativeMessagingHosts\vrpc";
                    Console.WriteLine($"Creating registry key for Chrome Native Messaging support at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue(null, $"{appNamePath}\\vrpc-chrome.json");
                    }

                    currentKey = @"Software\Microsoft\Edge\NativeMessagingHosts\vrpc";
                    Console.WriteLine($"Creating registry key for Edge Native Messaging support at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue(null, $"{appNamePath}\\vrpc-chrome.json");
                    }
                }
                catch
                {
                    Console.WriteLine($"Error: Couldn't create registry keys for the application & Native Messaging support. Installation cannot proceed. Aborting.");
                    Console.WriteLine($"Press any key to exit.");
                    Console.ReadLine();
                    Environment.Exit(1);
                    return;
                }
            }
            Console.WriteLine($"The application is now installed. Press any key to exit.");
            Console.ReadLine();
        }
    }
}