using VRPC.Application;

namespace VRPC.Deployment.Install
{
    // Chooses which extension IDs a setup operation must end up with. Explicit
    // command-line IDs win; otherwise an update or repair has to derive them from
    // the existing install state instead of silently falling back to the official
    // defaults and losing a custom registration.
    public static class RegistrationPolicy
    {
        public static RegistrationOptions ResolveTarget(InstallState? state, RegistrationOptions requested, bool registrationEnabled)
        {
            if (!registrationEnabled || state is null) { return requested.Clone(); }
            if (state.RegistrationMigrationFailed) { throw Inconsistent(state); }

            List<string> chromium = requested.ChromiumExplicit ? CleanIds(requested.ChromiumExtensionIds) : CleanIds(state.ChromiumExtensionIds);
            List<string> firefox = requested.FirefoxExplicit ? CleanIds(requested.FirefoxExtensionIds) : CleanIds(state.FirefoxExtensionIds);

            if (chromium.Count == 0 || firefox.Count == 0)
            {
                throw new InstallException(
                    "The existing installation did not record its native-messaging extension IDs, so the update cannot preserve them. " +
                    "Run setup again with --extension-id and --firefox-extension-id, or reinstall VRPC.",
                    SetupExitCodes.Failed);
            }

            return RegistrationOptions.FromLists(chromium, firefox);
        }

        // IDs of the currently installed version, used when a failed update is rolled back.
        public static RegistrationOptions ResolvePrevious(InstallState? state, RegistrationOptions requested, bool registrationEnabled)
        {
            if (!registrationEnabled || state is null) { return requested.Clone(); }
            if (state.RegistrationMigrationFailed) { throw Inconsistent(state); }

            List<string> chromium = CleanIds(state.ChromiumExtensionIds);
            List<string> firefox = CleanIds(state.FirefoxExtensionIds);
            if (chromium.Count == 0 || firefox.Count == 0) { throw Inconsistent(state); }

            return RegistrationOptions.FromLists(chromium, firefox);
        }

        private static InstallException Inconsistent(InstallState state)
        {
            string version = string.IsNullOrWhiteSpace(state.Version) ? "the installed version" : state.Version;
            return new InstallException(
                $"The install state for {version} does not contain readable Chromium and Firefox extension IDs, so the update cannot preserve the browser registration. " +
                "Reinstall VRPC or pass --extension-id and --firefox-extension-id explicitly.",
                SetupExitCodes.Failed);
        }

        private static List<string> CleanIds(IEnumerable<string> ids)
        {
            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
