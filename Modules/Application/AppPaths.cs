using System.Runtime.InteropServices;

namespace VRPC.Application
{
    // Resolves the per-user app/data/state layout and the legacy 1.0.x paths.
    public static class AppPaths
    {
        public const string ProductName = "VRPC";
        public const string AppFolderName = "VRPCApp";
        public const string VendorName = "Viktir";
        public const string AppExeName = "VRPC.exe";
        public const string PayloadManifestName = "payload.json";
        public const string InstallStateName = "install.json";
        public const string UpdateMarkerName = "update.json";
        public const string MigrationMarkerName = "migration.json";
        public const string ControlPipeName = "vrpc-control";

        private static string _root = GetDefaultRoot();

        public static string Root => _root;
        public static string App => Path.Combine(Root, "app");
        public static string Data => Path.Combine(Root, "data");
        public static string State => Path.Combine(Root, "state");
        public static string Manifests => Path.Combine(Root, "manifests");
        public static string Uninstall => Path.Combine(Root, "uninstall");
        public static string Logs => Path.Combine(State, "logs");
        public static string AppExe => Path.Combine(App, AppExeName);
        public static string InstallStatePath => Path.Combine(State, InstallStateName);
        public static string UpdateMarkerPath => Path.Combine(State, UpdateMarkerName);
        public static string MigrationMarkerPath => Path.Combine(State, MigrationMarkerName);
        public static string PidPath => Path.Combine(State, "host.pid");
        public static string ControlPipe => ControlPipeName;

        // Legacy 1.0.x root; only used by migration and uninstall.
        public static string LegacyRoot
        {
            get
            {
                string? overrideRoot = Environment.GetEnvironmentVariable("VRPC_LEGACY_ROOT");
                if (!string.IsNullOrWhiteSpace(overrideRoot)) { return Path.GetFullPath(overrideRoot); }

                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(roaming, AppFolderName);
            }
        }

        public static string LegacyApp => Path.Combine(LegacyRoot, "bin");
        public static string LegacyData => Path.Combine(LegacyRoot, "Data");
        public static string LegacyLock => Path.Combine(LegacyRoot, ".lock");

        public static void OverrideRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) { throw new ArgumentException("A root path is required.", nameof(root)); }
            _root = Path.GetFullPath(root);
        }

        public static void ResetRoot()
        {
            _root = GetDefaultRoot();
        }

        public static string GetDefaultRoot()
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("VRPC_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideRoot)) { return Path.GetFullPath(overrideRoot); }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, VendorName, AppFolderName);
        }

        // app/ is created by installation, not by this helper.
        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(State);
            Directory.CreateDirectory(Manifests);
            Directory.CreateDirectory(Logs);
        }

        // Guards deletions against paths outside the install root.
        public static bool IsUnderRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { return false; }
            string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsUnderApp(string path)
        {
            string app = Path.GetFullPath(App).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return candidate.Equals(app, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static string ControlPipeNameForCurrentUser()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { return ControlPipeName; }
            return $"{ControlPipeName}-{Environment.UserName}";
        }
    }
}
