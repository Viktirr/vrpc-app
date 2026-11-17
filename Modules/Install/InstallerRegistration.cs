using Microsoft.Win32;
using VRPC.Application;

namespace VRPC.Install
{
    // The registration surface the engine and recovery code use. The real
    // implementation writes registry keys and manifests; tests replace it so no
    // per-user browser registration has to be touched.
    public interface IInstallerRegistration
    {
        bool Enabled { get; }
        RegistrationResult Apply(string appExe, RegistrationOptions options, string version);
        void Remove(InstallState? state);
        void RemoveArp();
        void RemoveLegacy();
        void RemoveAllKeys();
        void Verify(string appExe, IReadOnlyList<RegisteredManifest> manifests);
    }

    public sealed class InstallerRegistration : IInstallerRegistration
    {
        public bool Enabled => !string.Equals(Environment.GetEnvironmentVariable("VRPC_NO_REGISTRY"), "1", StringComparison.Ordinal);

        public RegistrationResult Apply(string appExe, RegistrationOptions options, string version)
        {
            if (!Enabled) { return new RegistrationResult(); }
            RegistrationResult result = RegistrationManager.Apply(appExe, options);
            ArpRegistration.Write(version, appExe, appExe);
            return result;
        }

        public void Remove(InstallState? state)
        {
            if (Enabled) { RegistrationManager.Remove(state); }
        }

        public void RemoveArp()
        {
            if (Enabled) { ArpRegistration.Remove(); }
        }

        public void RemoveLegacy()
        {
            if (Enabled) { ArpRegistration.RemoveLegacy(); }
        }

        public void RemoveAllKeys()
        {
            if (Enabled) { RegistrationManager.Remove(null); }
        }

        // Throws RegistrationException when the recorded manifests or the registry
        // keys do not point at the executable that is now installed.
        public void Verify(string appExe, IReadOnlyList<RegisteredManifest> manifests)
        {
            if (!Enabled) { return; }
            if (manifests.Count == 0)
            {
                throw new RegistrationException("The installation did not record any native-messaging manifests.");
            }

            foreach (RegisteredManifest manifest in manifests)
            {
                if (!File.Exists(manifest.Path))
                {
                    throw new RegistrationException($"The native-messaging manifest {manifest.Path} is missing.");
                }

                string actual = PayloadManifest.HashFile(manifest.Path);
                if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RegistrationException($"The native-messaging manifest {manifest.Path} was modified after it was written.");
                }

                RegistrationManager.VerifyManifest(manifest.Path, appExe);
            }

            if (!OperatingSystem.IsWindows()) { return; }

            foreach (BrowserTarget target in BrowserCatalog.Targets)
            {
                string expected = Path.Combine(
                    AppPaths.Manifests,
                    target.Family == BrowserFamily.Firefox ? BrowserCatalog.FirefoxManifestName : BrowserCatalog.ChromiumManifestName);
                if (!File.Exists(expected))
                {
                    throw new RegistrationException($"The native-messaging manifest {expected} is missing.");
                }

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(target.RegistryKey);
                string? value = key?.GetValue(null) as string;
                if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RegistrationException($"The {target.Name} native-messaging registration does not point at the installed manifest.");
                }
            }
        }
    }
}
