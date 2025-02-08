using System.Runtime.CompilerServices;
using Gtk;
using Microsoft.Win32;
using Newtonsoft.Json;

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
            Thread installThread = new Thread(Install);
            installThread.Start();
        }

        public void EnableButtons()
        {
            installButton.Visible = false;
            cancelButton.Name = "mainButtons";
            cancelButton.Label = "Close";
            cancelButton.Sensitive = true;
        }

        public void Install()
        {
            Thread.Sleep(300);

            // Disable buttons
            Application.Invoke(delegate
            {
                installButton.Sensitive = false;
                cancelButton.Sensitive = false;
            });

            // Set variables for installation
            string roamingAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appName = "VRPCApp";
            string appNamePath = System.IO.Path.Combine(roamingAppDataPath, appName);

            string sourceFilePath = System.AppContext.BaseDirectory;
            string destinationFilePath = System.IO.Path.Combine(appNamePath, "VRPC.exe");

            // Create folder
            Application.Invoke(delegate { this.description.Text = description.Text + $"\nCreating folder {appNamePath}"; });

            try
            {
                if (!Directory.Exists(appNamePath))
                {
                    Directory.CreateDirectory(appNamePath);
                }
            }
            catch
            {
                Application.Invoke(delegate { description.Text = description.Text + $"\n\nError: Couldn't create folder. Installation cannot proceed. Check your available storage space. Press close to exit."; });
                EnableButtons();
                return;
            }

            // Check storage requirements
            Application.Invoke(delegate { description.Text = description.Text + "\nChecking available free space."; });
            try
            {
                string? rootPath = System.IO.Path.GetPathRoot(roamingAppDataPath);
                if (rootPath == null)
                {
                    Application.Invoke(delegate { description.Text = description.Text + "\n\nUnable to determine the root path to check storage space. Check your available storage space. Press close to exit."; });
                    EnableButtons();
                    return;
                }

                DriveInfo driveInfo = new DriveInfo(rootPath);
                if (driveInfo.IsReady)
                {
                    long installSpace = 50 * 1024 * 1024; // 50 MB
                    long availableFreeSpace = driveInfo.AvailableFreeSpace;

                    if (availableFreeSpace < installSpace)
                    {
                        Application.Invoke(delegate { description.Text = description.Text + "\n\nNot enough free space found. Please free up some storage and try again. No changes were done. Press close to exit."; });
                        EnableButtons();
                        return;
                    }
                }
            }
            catch
            {
                Application.Invoke(delegate { description.Text = description.Text + "\n\nError: Couldn't check storage space. Installation cannot proceed. Press close to exit."; });
                EnableButtons();
                return;
            }

            // Check directory of program - Checks if there are more files than the installer should ever have and if the file sizes are greater than the installer should have.
            Application.Invoke(delegate { description.Text = description.Text + $"\nChecking {sourceFilePath}..."; });
            string[] filesInDirectory = Directory.GetFiles(sourceFilePath);

            if (filesInDirectory.Length > 270)
            {
                Application.Invoke(delegate { description.Text = description.Text + $"\n\nSomething seems off... If not already extract the installer into a separate directory. Aborting..."; });
                EnableButtons();
                return;
            }

            if (filesInDirectory.Length > 1) // This check is made in case the application is built on single file compile.
            {
                foreach (string file in filesInDirectory)
                {
                    FileInfo fileInfo = new FileInfo(file);

                    if (fileInfo.Length > 20 * 1024 * 1024)
                    {
                        Application.Invoke(delegate { description.Text = description.Text + $"\n\nSomething seems off... If not already extract the installer into a separate directory. Aborting..."; });
                        EnableButtons();
                        return;
                    }
                }
            }

            // Copy program to Application Data
            Application.Invoke(delegate { description.Text = description.Text + $"\nCopying {sourceFilePath} to {destinationFilePath}"; });

            try
            {
                foreach (string file in filesInDirectory)
                {
                    string fileName = System.IO.Path.GetFileName(file);

                    if (fileName.Contains("VRPCInstall")) { fileName = "VRPC.exe"; }
                    string destFile = System.IO.Path.Combine(appNamePath, fileName);
                    File.Copy(file, destFile, true);
                }
            }
            catch
            {
                Application.Invoke(delegate { description.Text = description.Text + "\n\nError: Couldn't copy file. Installation cannot proceed. Press close to exit."; });
                EnableButtons();
                return;
            }

            // Create manifest file
            Application.Invoke(delegate { description.Text = description.Text + $"\nCreating manifest file at {appNamePath}"; });
            ManifestFileData manifestFileData = new ManifestFileData($"{appNamePath}\\VRPC.exe");

            string manifestFileDataJson = JsonConvert.SerializeObject(manifestFileData, Formatting.Indented);
            File.WriteAllText(System.IO.Path.Combine(appNamePath, "vrpc.json"), manifestFileDataJson);

            // Create registry
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    string currentKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VRPCApp";
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating uninstallation registry keys at {currentKey}"; });
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
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue("InstallPath", $"{appNamePath}");
                        key.SetValue("ManifestPath", $"{appNamePath}\\vrpc.json");
                    }

                    currentKey = @"Software\Mozilla\NativeMessagingHosts\vrpc";
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating registry keys for Firefox Native Messaging support at {currentKey}"; });
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue("(Default)", $"{appNamePath}\\vrpc.json");
                    }

                    currentKey = @"Software\Chrome\NativeMessagingHosts\vrpc";
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating registry keys for Chrome Native Messaging support at {currentKey}"; });
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue("(Default)", $"{appNamePath}\\vrpc.json");
                    }

                    currentKey = @"Software\Microsoft\Edge\NativeMessagingHosts\vrpc";
                    Application.Invoke(delegate { description.Text = description.Text + $"\nCreating registry keys for Edge Native Messaging support at {currentKey}"; });
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(currentKey))
                    {
                        key.SetValue("(Default)", $"{appNamePath}\\vrpc.json");
                    }
                }
                catch
                {
                    Application.Invoke(delegate { description.Text = description.Text + "\n\nError: Couldn't create registry keys for the application & Native Messaging support. Installation cannot proceed. Press close to exit."; });
                    EnableButtons();
                    return;
                }
            }
            Application.Invoke(delegate { description.Text = description.Text + $"\n\nInstalled. You may now close the installer."; });
            EnableButtons();
        }
    }
}