using System.Globalization;
using Microsoft.Win32;
using VRPC.Application;

namespace VRPC.Install
{
    // Add/Remove Programs metadata for the per-user installation.
    public static class ArpRegistration
    {
        public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Viktir.VRPC";
        public const string LegacyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VRPCApp";
        private const string UninstallArguments = "--uninstall";

        // Writes version, icon, size and the quoted uninstall command.
        public static void Write(string version, string uninstallerPath, string appExe)
        {
            if (!OperatingSystem.IsWindows()) { return; }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
                ?? throw new InvalidOperationException($"Could not create the ARP key {KeyPath}.");

            key.SetValue("DisplayName", "VRPC", RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("Publisher", "Viktir", RegistryValueKind.String);
            key.SetValue("DisplayIcon", $"\"{appExe}\",0", RegistryValueKind.String);
            key.SetValue("InstallLocation", AppPaths.Root, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{uninstallerPath}\" {UninstallArguments}", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" {UninstallArguments} --quiet", RegistryValueKind.String);
            key.SetValue("EstimatedSize", EstimateSizeKb(), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("URLInfoAbout", "https://viktir.com/vrpc", RegistryValueKind.String);

            string? installDate = key.GetValue("InstallDate") as string;
            if (string.IsNullOrWhiteSpace(installDate))
            {
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }
        }

        public static void Remove()
        {
            if (!OperatingSystem.IsWindows()) { return; }
            try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); } catch { }
        }

        public static void RemoveLegacy()
        {
            if (!OperatingSystem.IsWindows()) { return; }
            try { Registry.CurrentUser.DeleteSubKeyTree(LegacyKeyPath, throwOnMissingSubKey: false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Viktir\vrpc", throwOnMissingSubKey: false); } catch { }
        }

        public static int EstimateSizeKb()
        {
            long total = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(AppPaths.App, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
            }
            catch { }

            long kb = total / 1024;
            if (kb < 1) { kb = 1; }
            if (kb > int.MaxValue) { kb = int.MaxValue; }
            return (int)kb;
        }
    }
}
