using System.Diagnostics;
using VRPC.Application;

namespace VRPC.Deployment.Install
{
    // Version checks used to validate a commit (or a recovered backup) before any
    // backup, marker or install state is removed.
    public static class InstallVerification
    {
        // The installed application does not contain payload.json (only the
        // extracted package does), so an installed folder is identified by the
        // executable's own product version. A package directory still has the
        // manifest, and that one is preferred when it is present.
        public static bool TryReadAppVersion(string appDirectory, out string version)
        {
            version = "";

            string manifestPath = Path.Combine(appDirectory, AppPaths.PayloadManifestName);
            if (File.Exists(manifestPath))
            {
                try
                {
                    PayloadManifest manifest = PayloadManifest.Load(appDirectory);
                    if (!string.IsNullOrWhiteSpace(manifest.Version))
                    {
                        version = manifest.Version;
                        return true;
                    }
                }
                catch
                {
                }
            }

            try
            {
                string exe = Path.Combine(appDirectory, AppPaths.AppExeName);
                if (!File.Exists(exe)) { return false; }

                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                string? value = info.ProductVersion ?? info.FileVersion;
                if (string.IsNullOrWhiteSpace(value)) { return false; }

                version = value.Split('+')[0].Trim();
                return version.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool VersionMatches(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) { return false; }
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) { return true; }

            return ProductInfo.TryParseVersion(left, out Version leftVersion)
                && ProductInfo.TryParseVersion(right, out Version rightVersion)
                && leftVersion == rightVersion;
        }

        // True when the directory is a package whose payload version is the expected one.
        // A null expected version (possible for a schema 1 marker) only requires a readable payload.
        public static bool DirectoryHasVersion(string directory, string? expectedVersion, out string version)
        {
            version = "";
            if (!Directory.Exists(directory)) { return false; }
            if (!TryReadAppVersion(directory, out version)) { return false; }
            if (string.IsNullOrWhiteSpace(expectedVersion)) { return true; }
            return VersionMatches(version, expectedVersion);
        }

        public static IReadOnlyList<string> VerifyApp(string appDirectory, string expectedVersion)
        {
            List<string> errors = new();
            if (!Directory.Exists(appDirectory))
            {
                errors.Add($"the application folder {appDirectory} is missing");
                return errors;
            }

            if (!TryReadAppVersion(appDirectory, out string version))
            {
                errors.Add("the installed version could not be determined from payload.json or VRPC.exe");
            }
            else if (!VersionMatches(version, expectedVersion))
            {
                errors.Add($"the installed version is {version}, but {expectedVersion} was expected");
            }

            if (!File.Exists(Path.Combine(appDirectory, AppPaths.AppExeName)))
            {
                errors.Add("VRPC.exe is missing from the application folder");
            }

            return errors;
        }

        public static IReadOnlyList<string> VerifyInstallState(string expectedVersion)
        {
            List<string> errors = new();
            InstallState? state = InstallStateStore.Load();
            if (state is null)
            {
                errors.Add("the install state is missing or unreadable");
                return errors;
            }

            if (!VersionMatches(state.Version, expectedVersion))
            {
                errors.Add($"the install state reports version {state.Version}, but {expectedVersion} was expected");
            }

            return errors;
        }
    }
}
