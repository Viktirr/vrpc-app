using System.Runtime.CompilerServices;
using Gtk;
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

    public class InstallWindow : Window
    {

        Button installButton;
        Button cancelButton;

        Label description;

        public InstallWindow() : base("VRPC Installer")
        {
            SetDefaultSize(800, 400);
            Resizable = false;
            DeleteEvent += (o, args) => Application.Quit();

            var mainBox = new Box(Orientation.Vertical, 0);
            Add(mainBox);

            var header = new Label("<span size='x-large' weight='bold'>VRPC Installer</span>");
            header.UseMarkup = true;
            mainBox.PackStart(header, false, false, 20);

            description = new Label("VPRC is an application that allows communication between the extension and client to provide Rich Presence data to Discord alongside other features.\n\nBy clicking Install below, you agree to the License, Terms of Use and Privacy Policy of this application listed over at https://viktir.com/vrpc\n\nIf you do not agree with any of the previous notices, refrain from installing this software by clicking the Cancel button or exiting the program.\n\nBy selecting install the following will occur:\nThe files for this application will extract at %appdata%\\VRPCApp\nA few registry keys will be created to allow for Native Messaging permissions to work with the extension and to create an entry for uninstalling the application in the Settings app.")
            {
                LineWrap = true,
                Justify = Justification.Center
            };
            mainBox.PackStart(description, false, false, 20);

            var buttonBox = new Box(Orientation.Horizontal, 10)
            {
                BorderWidth = 20
            };
            mainBox.PackEnd(buttonBox, false, true, 0);

            buttonBox.PackStart(new Label(), true, true, 0);

            installButton = new Button("Install") { Name = "mainButtons" };
            cancelButton = new Button("Cancel") { Name = "altButtons" };
            buttonBox.PackStart(installButton, false, false, 0);
            buttonBox.PackStart(cancelButton, false, false, 0);

            KeyPressEvent += (o, args) =>
            {
                if (args.Event.Key == Gdk.Key.r)
                {
                    Destroy();
                    new InstallWindow().ShowAll();
                }
            };

            installButton.Clicked += OnInstallButtonClicked;
            cancelButton.Clicked += (o, args) => Application.Quit();

            var cssProvider = new CssProvider();
            cssProvider.LoadFromData(PackagingGlobals.cssString);
            StyleContext.AddProviderForScreen(
                Gdk.Screen.Default,
                cssProvider,
                StyleProviderPriority.Application
            );

            ShowAll();
        }

        public void OnInstallButtonClicked(object? sender, EventArgs e)
        {
            description.Text = "Installing...";
            installButton.Sensitive = false;
            cancelButton.Sensitive = false;

            Thread installThread = new Thread(Install);
            installThread.Start();
        }

        public void EnableButtons()
        {
            Application.Invoke(delegate
            {
                installButton.Visible = false;
                cancelButton.Name = "mainButtons";
                cancelButton.Label = "Close";
                cancelButton.Sensitive = true;
            });
        }

        public void Install()
        {
            Console.WriteLine("User started installation.");

            Thread.Sleep(200);

            // Set variables for installation
            string roamingAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appNamePath = System.IO.Path.Combine(roamingAppDataPath, VRPCGlobalData.appName);

            string sourceFilePath = AppContext.BaseDirectory;
            string destinationFilePath = System.IO.Path.Combine(appNamePath, "VRPC.exe");

            // Create folder
            Application.Invoke(delegate { description.Text = description.Text + $"\nCreating folder {appNamePath}"; });
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
                Application.Invoke(delegate { description.Text = description.Text + $"\n\nError: Couldn't create folder. Installation cannot proceed. Check your available storage space. Press close to exit."; });
                Console.WriteLine($"Error: Couldn't create folder {appNamePath}.");
                EnableButtons();
                return;
            }

            // Check storage requirements
            Application.Invoke(delegate { description.Text = description.Text + "\nChecking available free space."; });
            Console.WriteLine($"Checking available free space...");
            try
            {
                string? rootPath = System.IO.Path.GetPathRoot(roamingAppDataPath);
                Console.WriteLine($"Checking {rootPath} for free space...");
                if (rootPath == null)
                {
                    Application.Invoke(delegate { description.Text = description.Text + "\n\nUnable to determine the root path to check storage space. Check your available storage space. Press close to exit."; });
                    Console.WriteLine($"Unable to determine {rootPath} to determine free space.");
                    EnableButtons();
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
                        Application.Invoke(delegate { description.Text = description.Text + "\n\nNot enough free space found. Please free up some storage and try again. No changes were done. Press close to exit."; });
                        Console.WriteLine($"Error: Not enough free space..");
                        EnableButtons();
                        return;
                    }
                }
            }
            catch
            {
                Application.Invoke(delegate { description.Text = description.Text + "\n\nError: Couldn't check storage space. Installation cannot proceed. Press close to exit."; });
                Console.WriteLine($"Error: Couldn't check storage space. Aborting.");
                EnableButtons();
                return;
            }

            // Check directory of program - Checks if there are more files than the installer should ever have and if the file sizes are greater than the installer should have.
            Application.Invoke(delegate { description.Text = description.Text + $"\nChecking {sourceFilePath}..."; });
            Console.WriteLine($"Initialising check from the installer {sourceFilePath}");
            string[] filesInDirectory = Directory.GetFiles(sourceFilePath);

            int maxFilesInDirectory = 270;
            if (filesInDirectory.Length > maxFilesInDirectory)
            {
                Application.Invoke(delegate { description.Text = description.Text + $"\n\nSomething seems off... If not already extract the installer into a separate directory. Aborting..."; });
                Console.WriteLine($"There are more files than there should be ({filesInDirectory.Length}/{maxFilesInDirectory}) in the installer directory. Aborting.");
                EnableButtons();
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
                        Application.Invoke(delegate { description.Text = description.Text + $"\n\nSomething seems off... If not already extract the installer into a separate directory. Aborting..."; });
                        Console.WriteLine($"One of the files is larger than 20 MiB or the total amount of files exceed 150 MiB, assuming these are not the files from the program. Aborting.");
                        EnableButtons();
                        return;
                    }
                }
            }

            // Copy program to Application Data
            Application.Invoke(delegate { description.Text = description.Text + $"\nCopying {sourceFilePath} to {destinationFilePath}"; });
            Console.WriteLine($"Initialising file copy");

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
                Application.Invoke(delegate { description.Text = description.Text + "\n\nError: Couldn't copy file. Installation cannot proceed. Press close to exit."; });
                Console.WriteLine($"Error: Couldn't copy file. Aborting.");
                EnableButtons();
                return;
            }

            // Create manifest file
            Application.Invoke(delegate { description.Text = description.Text + $"\nCreating manifest file at {appNamePath}"; });
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
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating uninstallation registry keys at {currentKey}"; });
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
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating registry keys for the application at {currentKey}"; });
                    Console.WriteLine($"Creating registry key for the application at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue("InstallPath", $"{appNamePath}");
                        key.SetValue("ManifestPath", $"{appNamePath}\\vrpc.json");
                        key.SetValue("ManifestChromePath", $"{appNamePath}\\vrpc-chrome.json");
                    }

                    currentKey = @"Software\Mozilla\NativeMessagingHosts\vrpc";
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating registry keys for Firefox Native Messaging support at {currentKey}"; });
                    Console.WriteLine($"Creating registry key for Firefox Native Messaging support at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue(null, $"{appNamePath}\\vrpc.json");
                    }

                    currentKey = @"Software\Google\Chrome\NativeMessagingHosts\vrpc";
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating registry keys for Chrome Native Messaging support at {currentKey}"; });
                    Console.WriteLine($"Creating registry key for Chrome Native Messaging support at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue(null, $"{appNamePath}\\vrpc-chrome.json");
                    }

                    currentKey = @"Software\Microsoft\Edge\NativeMessagingHosts\vrpc";
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating registry keys for Edge Native Messaging support at {currentKey}"; });
                    Console.WriteLine($"Creating registry key for Edge Native Messaging support at {currentKey}");
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue(null, $"{appNamePath}\\vrpc-chrome.json");
                    }
                }
                catch
                {
                    Application.Invoke(delegate { description.Text = description.Text + "\n\nError: Couldn't create registry keys for the application & Native Messaging support. Installation cannot proceed. Press close to exit."; });
                    Console.WriteLine($"Creating registry keys failed. Aborting.");
                    EnableButtons();
                    return;
                }
            }
            Application.Invoke(delegate { description.Text = description.Text + $"\n\nInstalled. You may now close the installer."; });
            Console.WriteLine($"The application is now installed.");
            EnableButtons();
        }
    }
}