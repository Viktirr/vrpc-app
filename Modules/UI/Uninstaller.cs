using Gtk;
using Microsoft.Win32;

namespace VRPC.Packaging
{
    public class UninstallWindow : Window
    {
        Label description = null!;

        Button uninstallButton = null!;
        Button cancelButton = null!;

        public UninstallWindow() : base("VRPC Uninstaller")
        {
            SetDefaultSize(800, 400);
            Resizable = false;
            DeleteEvent += (o, args) => Application.Quit();

            var mainBox = new Box(Orientation.Vertical, 0);
            Add(mainBox);

            var header = new Label("<span size='x-large' weight='bold'>VRPC Installer</span>");
            header.UseMarkup = true;
            mainBox.PackStart(header, false, false, 20);

            description = new Label($"You may uninstall the VRPC application by selecting Uninstall. Your app activity (Listening Data) and preferences will be deleted.{PackagingGlobals.sameFolderString}{PackagingGlobals.uninstallString}")
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

            cancelButton = new Button("Cancel") { Name = "altButtons" };
            uninstallButton = new Button("Uninstall") { Name = "mainButtons" };
            buttonBox.PackStart(uninstallButton, false, false, 0);
            buttonBox.PackStart(cancelButton, false, false, 0);

            KeyPressEvent += (o, args) =>
            {
                if (args.Event.Key == Gdk.Key.r)
                {
                    Destroy();
                    new UninstallWindow().ShowAll();
                }
            };

            uninstallButton.Clicked += OnUninstallButtonClicked;
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

        public void OnUninstallButtonClicked(object? sender, EventArgs e)
        {
            description.Text = "Uninstalling...";
            Thread uninstallThread = new Thread(Uninstall);
            uninstallThread.Start();
        }

        public void EnableButtons()
        {
            uninstallButton.Visible = false;
            cancelButton.Name = "mainButtons";
            cancelButton.Label = "Close";
            cancelButton.Sensitive = true;
        }

        public void Uninstall()
        {
            Thread.Sleep(300);
            // Disable buttons
            Application.Invoke(delegate
            {
                uninstallButton.Sensitive = false;
                cancelButton.Sensitive = false;
            });

            // Check installation directory
            Application.Invoke(delegate { description.Text = description.Text + "\nSelecting default installation folder."; });
            string installPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRPCApp");

            Application.Invoke(delegate { description.Text = description.Text + $"\nChecking registry key for installation folder at Software\\Viktir\\vrpc."; });
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    RegistryKey? currentUserKey = Registry.CurrentUser.OpenSubKey(@"Software\Viktir\vrpc");

                    if (currentUserKey != null)
                    {
                        string? newInstallPath = currentUserKey.GetValue("InstallPath") as string;

                        if (!string.IsNullOrEmpty(newInstallPath)) { Console.WriteLine($"InstallPath found: {installPath}"); installPath = newInstallPath; }
                        else { Console.WriteLine("InstallPath does not exist."); }

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
                Application.Invoke(delegate { description.Text = description.Text + "\nDeleting registry keys for Native Messaging and Installation Folder:"; });
                string[] registryKeys = new string[]
                {
                    @"Software\Mozilla\NativeMessagingHosts\vrpc",
                    @"Software\Chrome\NativeMessagingHosts\vrpc",
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
                                Application.Invoke(delegate { description.Text = description.Text + $"\nDeleting registry {keyName}"; });
                                currentKey.DeleteSubKeyTree("");
                            }
                            else
                            {
                                Application.Invoke(delegate { description.Text = description.Text + $"\nThe key {key} does not exist."; });
                                Console.WriteLine($"The key {currentKey} does not exist.");
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Error deleting {key}:{ex.Message}"); Application.Invoke(delegate { description.Text = description.Text + $"\nError deleting {key}: {ex.Message}"; }); }
                }
            }

            // Delete contents of installation folder
            Application.Invoke(delegate { description.Text = description.Text + $"\nDeleting contents at {installPath}."; });
            if (Directory.Exists(installPath))
            {
                try
                {
                    string[] files = Directory.GetFiles(installPath);
                    Console.WriteLine($"There are {files.Length} files in {installPath}");

                    if (files.Length > 270)
                    {
                        Console.WriteLine("The directory contains more than 270 files. Deletion process is canceled.");
                        Application.Invoke(delegate { description.Text = description.Text + $"\n\nThere seem to be more files than necessary for deletion, please delete them yourself at {installPath}. You may now close the uninstaller."; });
                        EnableButtons();
                        return;
                    }
                    else
                    {
                        Application.Invoke(delegate { description.Text = description.Text + $"\nDeleting {installPath}"; });
                        Directory.Delete(installPath, true);
                        Console.WriteLine("All files and directories have been deleted.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An error occurred: {ex.Message}");
                    Application.Invoke(delegate { description.Text = description.Text + $"\n\nAn error occured while trying to delete files. Please delete the files manually, they should be located at {installPath}. You may now close the uninstaller."; });
                    EnableButtons();
                    return;
                }
            }
            else
            {
                Console.WriteLine("The specified directory does not exist.");
            }
            Application.Invoke(delegate { description.Text = description.Text + $"\n\nUninstalled. You may now close the uninstaller."; });
            EnableButtons();
        }
    }
}