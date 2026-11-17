using VRPC.Application;

namespace VRPC.Install
{
    // Outcome of running the interrupted-update recovery before a new operation.
    public sealed class RecoveryResult
    {
        public bool ShouldContinue { get; init; } = true;
        public bool Recovered { get; init; }
        public int ExitCode { get; init; }
        public string Message { get; init; } = "";

        public static RecoveryResult Clean => new();
        public static RecoveryResult ContinueAfterRecovery => new() { Recovered = true };

        public static RecoveryResult Stop(int exitCode, string message)
        {
            return new RecoveryResult { ShouldContinue = false, Recovered = true, ExitCode = exitCode, Message = message };
        }
    }

    // Conservative recovery of an interrupted install/update transaction. Runs
    // under the setup-operation lock, before any new mutation. When the state on
    // disk is inconclusive the marker and any usable backup are left in place so
    // nothing is silently destroyed.
    public static class UpdateRecovery
    {
        public static RecoveryResult Run(SetupLog log, IInstallerRegistration? registration = null)
        {
            registration ??= new InstallerRegistration();

            TransactionLoadResult loaded = InstallStateStore.LoadTransactionResult();
            if (loaded.Corrupt) { return RecoverFromCorruptMarker(loaded, log); }

            UpdateTransaction? marker = loaded.Transaction;
            if (marker is null) { return RecoverWithoutMarker(log, registration); }

            try
            {
                log.Write($"Recovery: phase={marker.Phase} target={marker.TargetVersion} previous={marker.PreviousVersion} hadApp={marker.HadAppBefore} registrationRemoved={marker.RegistrationRemoved} schema={marker.Schema}");

                if (!IsUsablePath(marker.StagingDir, allowEmpty: true, out string stagingProblem))
                {
                    return RecoveryResult.Stop(SetupExitCodes.Failed,
                        $"The update marker points outside the installation root ({stagingProblem}). The update files were kept for manual recovery.");
                }

                string backup = ResolveBackupPath(marker, out string backupProblem);
                if (backupProblem.Length > 0)
                {
                    return RecoveryResult.Stop(SetupExitCodes.Failed,
                        $"The update marker points at an invalid backup path ({backupProblem}). The update files were kept for manual recovery.");
                }

                string staging = marker.StagingDir;
                bool appPresent = Directory.Exists(AppPaths.App);
                bool backupPresent = Directory.Exists(backup);

                if (appPresent && !backupPresent) { return RecoverAppWithoutBackup(marker, staging, registration, log); }
                if (!appPresent && backupPresent) { return RecoverFromBackup(marker, staging, backup, registration, log); }
                if (appPresent && backupPresent) { return RecoverBothPresent(marker, staging, backup, registration, log); }
                return RecoverNothingInstalled(marker, staging, registration, log);
            }
            catch (Exception e)
            {
                log.Error("Recovery failed: " + e);
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "VRPC could not recover from an interrupted setup operation automatically: " + e.Message +
                    " The update files were kept for manual recovery.");
            }
        }

        // No marker means no transaction is known, but an orphaned app.old can
        // still be the only usable copy of the previous installation. Restore it
        // only when it verifies and agrees with the recorded install state;
        // otherwise stop and keep it on disk instead of letting a commit delete it.
        private static RecoveryResult RecoverWithoutMarker(SetupLog log, IInstallerRegistration registration)
        {
            if (Directory.Exists(AppPaths.App)) { return RecoveryResult.Clean; }

            string backup = Path.Combine(AppPaths.Root, "app.old");
            if (!Directory.Exists(backup)) { return RecoveryResult.Clean; }

            if (!InstallVerification.DirectoryHasVersion(backup, null, out string backupVersion))
            {
                log.Error($"Recovery: the orphaned backup at {backup} does not contain a usable VRPC installation.");
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    $"No update marker was found and no application is installed, but a previous-version backup exists at {backup}. " +
                    "The backup could not be verified as a usable installation, so it was kept for manual recovery. " +
                    "Reinstall VRPC from the latest release.");
            }

            IReadOnlyList<string> errors = InstallVerification.VerifyApp(backup, backupVersion);
            if (errors.Count > 0)
            {
                log.Error($"Recovery: the orphaned backup at {backup} did not verify: {string.Join("; ", errors)}");
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    $"No update marker was found and no application is installed, but a previous-version backup exists at {backup}. " +
                    "The backup did not verify (" + string.Join("; ", errors) + "), so it was kept for manual recovery. " +
                    "Reinstall VRPC from the latest release.");
            }

            InstallState? state = InstallStateStore.Load();
            if (state is not null && string.IsNullOrWhiteSpace(state.Version))
            {
                log.Write("Recovery: the install state has no version; restoring the orphaned backup without a state cross-check.");
            }
            else if (state is not null && !InstallVerification.VersionMatches(state.Version, backupVersion))
            {
                log.Error($"Recovery: the orphaned backup is {backupVersion}, but the install state reports {state.Version}.");
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    $"No update marker was found and no application is installed, but the previous-version backup at {backup} " +
                    $"does not match the recorded installation ({state.Version}). Both were kept for manual recovery. " +
                    "Reinstall VRPC from the latest release.");
            }

            if (!TryMoveDirectory(backup, AppPaths.App, out string moveError))
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    $"No update marker was found and no application is installed, but the previous-version backup at {backup} " +
                    $"could not be restored: {moveError}. It was kept for manual recovery.");
            }

            log.Write($"Recovery: no update marker was found and the application folder was missing; restored the previous installation ({backupVersion}) from the orphaned backup at {backup}.");

            if (registration.Enabled && state is not null &&
                !VerifyRegistrationWhenEnabled(registration, AppPaths.AppExe, log, out _))
            {
                // The registration points at the same executable path, so a
                // verification failure is only worth logging: the files are in
                // the right shape and running setup again repairs the keys.
                log.Error("Recovery: the browser registration does not verify after restoring the previous installation; run setup again to repair it.");
            }

            return RecoveryResult.ContinueAfterRecovery;
        }

        // app/ is present but the backup is gone: either the swap never started or
        // it already completed. A matching target version means the commit should
        // be finished; anything else is a marker left behind before the swap.
        private static RecoveryResult RecoverAppWithoutBackup(UpdateTransaction marker, string staging, IInstallerRegistration registration, SetupLog log)
        {
            string appVersion = "";
            bool targetInstalled = !string.IsNullOrWhiteSpace(marker.TargetVersion)
                && InstallVerification.DirectoryHasVersion(AppPaths.App, marker.TargetVersion, out appVersion);
            if (targetInstalled) { return FinishCommit(marker, staging, appVersion, registration, log); }

            log.Write($"Recovery: the installed app is {appVersion}, not the target {marker.TargetVersion}; removing the stale transaction.");
            DeleteStaging(staging, log);
            if (!TryRestoreRegistration(marker, AppPaths.AppExe, registration, log, out string registrationError))
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "An interrupted update was found and the previous version is still installed, but the browser registration could not be restored: " +
                    registrationError + " The update marker was kept for manual recovery.");
            }

            InstallStateStore.DeleteTransaction();
            return RecoveryResult.ContinueAfterRecovery;
        }

        // app/ is missing and only the backup exists: restore it, but only after the
        // backup itself is verified. A failed verification keeps the backup and marker.
        private static RecoveryResult RecoverFromBackup(UpdateTransaction marker, string staging, string backup, IInstallerRegistration registration, SetupLog log)
        {
            if (!InstallVerification.DirectoryHasVersion(backup, marker.PreviousVersion, out string backupVersion))
            {
                log.Error($"Recovery: the backup at {backup} is not a valid {marker.PreviousVersion} package.");
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    $"An interrupted update was found, but the backup of the previous version at {backup} could not be verified. " +
                    "It was kept for manual recovery; reinstall VRPC from the latest release.");
            }

            if (!TryMoveDirectory(backup, AppPaths.App, out string moveError))
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    $"An interrupted update was found and the previous version could not be restored: {moveError}. The backup was kept for manual recovery.");
            }

            log.Write($"Recovery: restored the previous app folder ({backupVersion}).");
            DeleteStaging(staging, log);

            IReadOnlyList<string> errors = InstallVerification.VerifyApp(AppPaths.App, marker.PreviousVersion);
            string registrationError = "";
            bool registrationOk = VerifyRegistrationWhenEnabled(registration, AppPaths.AppExe, log, out registrationError);
            if (errors.Count > 0 || !registrationOk)
            {
                string detail = errors.Count > 0 ? string.Join("; ", errors) : registrationError;
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "An interrupted update was found and the previous version was restored, but it did not verify: " + detail +
                    " The update marker was kept for manual recovery.");
            }

            if (!TryRestoreRegistration(marker, AppPaths.AppExe, registration, log, out string restoreError))
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "The previous version was restored, but the browser registration could not be restored: " + restoreError +
                    " The update marker was kept for manual recovery.");
            }

            InstallStateStore.DeleteTransaction();
            return RecoveryResult.Stop(SetupExitCodes.Failed,
                $"An interrupted update was found. VRPC {marker.PreviousVersion} was restored; run setup again to retry the update.");
        }

        // Both app/ and app.old exist: the swap happened but was not completed.
        // If the installed app, install state and registration all verify against
        // the target, the commit was actually done and only the cleanup is missing.
        private static RecoveryResult RecoverBothPresent(UpdateTransaction marker, string staging, string backup, IInstallerRegistration registration, SetupLog log)
        {
            string verifiedVersion = "";
            bool targetVerified = UpdatePhase.IsAtLeast(marker.Phase, UpdatePhase.Committed)
                && InstalledTargetVerified(marker, registration, out verifiedVersion);
            if (targetVerified)
            {
                log.Write($"Recovery: the target version {verifiedVersion} is installed and verified; finishing the interrupted commit.");
                DeleteStaging(staging, log);
                DeleteDirectoryIfPresent(backup, log);
                InstallStateStore.DeleteTransaction();
                return RecoveryResult.ContinueAfterRecovery;
            }

            if (!InstallVerification.DirectoryHasVersion(backup, marker.PreviousVersion, out string backupVersion))
            {
                log.Error($"Recovery: the backup at {backup} is not a valid {marker.PreviousVersion} package.");
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    $"An interrupted update was found, but neither the installed files nor the backup at {backup} could be verified against the target. " +
                    "Both were kept for manual recovery; reinstall VRPC from the latest release.");
            }

            string failedDir = Path.Combine(AppPaths.Root, "app.failed-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            if (!TryMoveDirectory(AppPaths.App, failedDir, out string failedMoveError))
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "An interrupted update was found and the unverified application files could not be moved aside: " + failedMoveError +
                    " Both versions and the marker were kept for manual recovery.");
            }

            if (!TryMoveDirectory(backup, AppPaths.App, out string restoreError))
            {
                TryMoveDirectory(failedDir, AppPaths.App, out _);
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "An interrupted update was found and the backup could not be restored: " + restoreError +
                    " The backup and the marker were kept for manual recovery.");
            }

            log.Write($"Recovery: restored the previous app folder ({backupVersion}) and moved the unverified files to {failedDir}.");

            IReadOnlyList<string> errors = InstallVerification.VerifyApp(AppPaths.App, marker.PreviousVersion);
            if (errors.Count > 0)
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "An interrupted update was found and the previous version was restored, but it did not verify: " + string.Join("; ", errors) +
                    " The restored files and the update marker were kept for manual recovery.");
            }

            if (!TryRestoreRegistration(marker, AppPaths.AppExe, registration, log, out string registrationError))
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "The previous version was restored, but the browser registration could not be restored: " + registrationError +
                    " The update marker was kept for manual recovery.");
            }

            DeleteStaging(staging, log);
            DeleteDirectoryIfPresent(failedDir, log);
            InstallStateStore.DeleteTransaction();
            return RecoveryResult.Stop(SetupExitCodes.Failed,
                $"An interrupted update was found. VRPC {marker.PreviousVersion} was restored; run setup again to retry the update.");
        }

        // Neither app/ nor app.old exists. A fresh install can simply drop the
        // marker; an update that lost both copies is inconclusive and stops here.
        private static RecoveryResult RecoverNothingInstalled(UpdateTransaction marker, string staging, IInstallerRegistration registration, SetupLog log)
        {
            DeleteStaging(staging, log);

            if (marker.HadAppBefore && marker.Schema >= UpdateTransaction.CurrentSchema)
            {
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "An interrupted setup operation was found, but both the installed application and its backup are missing. " +
                    "The update marker was kept for manual recovery; reinstall VRPC from the latest release.");
            }

            if (marker.RegistrationRemoved && registration.Enabled)
            {
                try
                {
                    registration.Remove(null);
                    registration.RemoveArp();
                }
                catch (Exception e)
                {
                    log.Error("Recovery: could not remove a partial registration: " + e.Message);
                }
            }

            InstallStateStore.DeleteTransaction();
            return RecoveryResult.ContinueAfterRecovery;
        }

        // app/ matches the target version but the state/registration writes may not
        // have finished. Re-apply what is missing, verify, then drop the marker.
        private static RecoveryResult FinishCommit(UpdateTransaction marker, string staging, string appVersion, IInstallerRegistration registration, SetupLog log)
        {
            try
            {
                log.Write($"Recovery: the target version {appVersion} is installed; finishing the interrupted commit.");
                InstallState? previous = InstallStateStore.Load();
                RegistrationResult? applied = null;

                if (registration.Enabled)
                {
                    bool registrationValid = false;
                    if (previous is not null && previous.Manifests.Count > 0)
                    {
                        try { registration.Verify(AppPaths.AppExe, previous.Manifests); registrationValid = true; }
                        catch { registrationValid = false; }
                    }

                    if (!registrationValid || marker.RegistrationRemoved)
                    {
                        RegistrationOptions options = ResolveTargetRegistration(marker, previous);
                        applied = registration.Apply(AppPaths.AppExe, options, marker.TargetVersion);
                    }
                }

                InstallState state = previous ?? new InstallState();
                RegistrationOptions targetOptions = ResolveTargetRegistration(marker, state);
                state.Schema = InstallState.CurrentSchema;
                state.Version = marker.TargetVersion;
                state.AppDir = AppPaths.App;
                state.DataDir = AppPaths.Data;
                state.PreviousVersion = marker.PreviousVersion;
                state.ExtensionIds = null;
                state.ChromiumExtensionIds = targetOptions.ChromiumExtensionIds.ToList();
                state.FirefoxExtensionIds = targetOptions.FirefoxExtensionIds.ToList();
                if (string.IsNullOrWhiteSpace(state.InstalledAtUtc)) { state.InstalledAtUtc = DateTime.UtcNow.ToString("o"); }
                if (applied is not null)
                {
                    state.Manifests = applied.Manifests;
                    state.RegistryKeys = applied.RegistryKeys;
                }
                InstallStateStore.Save(state);

                List<string> errors = new();
                errors.AddRange(InstallVerification.VerifyApp(AppPaths.App, marker.TargetVersion));
                errors.AddRange(InstallVerification.VerifyInstallState(marker.TargetVersion));
                if (registration.Enabled)
                {
                    try { registration.Verify(AppPaths.AppExe, state.Manifests); }
                    catch (RegistrationException e) { errors.Add(e.Message); }
                }

                if (errors.Count > 0)
                {
                    return RecoveryResult.Stop(SetupExitCodes.Failed,
                        "An interrupted update was found and the new version is installed, but it did not verify: " + string.Join("; ", errors) +
                        " The update marker was kept for manual recovery.");
                }

                DeleteStaging(staging, log);
                InstallStateStore.DeleteTransaction();
                return RecoveryResult.ContinueAfterRecovery;
            }
            catch (Exception e)
            {
                log.Error("Recovery: finishing the commit failed: " + e);
                return RecoveryResult.Stop(SetupExitCodes.Failed,
                    "An interrupted update was found and the new version is installed, but finishing the commit failed: " + e.Message +
                    " The update marker was kept for manual recovery.");
            }
        }

        private static RecoveryResult RecoverFromCorruptMarker(TransactionLoadResult loaded, SetupLog log)
        {
            log.Error($"Recovery: the update marker was corrupt; preserved at {loaded.CorruptPath}.");
            string backup = Path.Combine(AppPaths.Root, "app.old");
            bool appPresent = Directory.Exists(AppPaths.App);
            bool backupPresent = Directory.Exists(backup);

            if (!appPresent && backupPresent)
            {
                if (!InstallVerification.DirectoryHasVersion(backup, null, out string backupVersion))
                {
                    return RecoveryResult.Stop(SetupExitCodes.Failed,
                        $"A corrupt update marker was preserved at {loaded.CorruptPath}, and the backup at {backup} could not be verified. " +
                        "It was kept for manual recovery; reinstall VRPC from the latest release.");
                }

                if (!TryMoveDirectory(backup, AppPaths.App, out string moveError))
                {
                    return RecoveryResult.Stop(SetupExitCodes.Failed,
                        $"A corrupt update marker was preserved at {loaded.CorruptPath}, and the backup could not be restored: {moveError}. " +
                        "It was kept for manual recovery.");
                }

                log.Write($"Recovery: restored the app folder ({backupVersion}) after a corrupt marker.");
                return RecoveryResult.ContinueAfterRecovery;
            }

            return RecoveryResult.Stop(SetupExitCodes.Failed,
                $"A corrupt update marker was found and preserved at {loaded.CorruptPath}. VRPC did not touch the installation because it could not " +
                "determine what state it is in. Reinstall VRPC from the latest release; your settings and listening history are kept.");
        }

        private static bool InstalledTargetVerified(UpdateTransaction marker, IInstallerRegistration registration, out string version)
        {
            version = "";
            if (!InstallVerification.DirectoryHasVersion(AppPaths.App, marker.TargetVersion, out version)) { return false; }
            if (InstallVerification.VerifyInstallState(marker.TargetVersion).Count > 0) { return false; }
            return VerifyRegistrationWhenEnabled(registration, AppPaths.AppExe, null, out _);
        }

        private static bool VerifyRegistrationWhenEnabled(IInstallerRegistration registration, string appExe, SetupLog? log, out string error)
        {
            error = "";
            if (!registration.Enabled) { return true; }

            try
            {
                InstallState? state = InstallStateStore.Load();
                registration.Verify(appExe, state?.Manifests ?? new List<RegisteredManifest>());
                return true;
            }
            catch (RegistrationException e)
            {
                error = e.Message;
                log?.Error("Recovery: registration verification failed: " + e.Message);
                return false;
            }
        }

        private static bool TryRestoreRegistration(UpdateTransaction marker, string appExe, IInstallerRegistration registration, SetupLog log, out string error)
        {
            error = "";
            if (!registration.Enabled || !marker.RegistrationRemoved) { return true; }

            List<string> chromium = CleanIds(marker.PreviousChromiumExtensionIds);
            List<string> firefox = CleanIds(marker.PreviousFirefoxExtensionIds);
            if (chromium.Count == 0 || firefox.Count == 0)
            {
                InstallState? state = InstallStateStore.Load();
                if (state is not null && !state.RegistrationMigrationFailed)
                {
                    if (chromium.Count == 0) { chromium = CleanIds(state.ChromiumExtensionIds); }
                    if (firefox.Count == 0) { firefox = CleanIds(state.FirefoxExtensionIds); }
                }
            }

            try
            {
                if (chromium.Count == 0 && firefox.Count == 0)
                {
                    registration.Remove(null);
                    registration.RemoveArp();
                    log.Write("Recovery: removed a partial native-messaging registration.");
                    return true;
                }

                string version = marker.PreviousVersion;
                registration.Apply(appExe, RegistrationOptions.FromLists(chromium, firefox), version);
                log.Write("Recovery: restored the previous native-messaging registration.");
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                log.Error("Recovery: could not restore the previous registration: " + e.Message);
                return false;
            }
        }

        private static RegistrationOptions ResolveTargetRegistration(UpdateTransaction marker, InstallState? state)
        {
            List<string> chromium = CleanIds(marker.TargetChromiumExtensionIds);
            List<string> firefox = CleanIds(marker.TargetFirefoxExtensionIds);

            if ((chromium.Count == 0 || firefox.Count == 0) && state is not null && InstallVerification.VersionMatches(state.Version, marker.TargetVersion))
            {
                if (chromium.Count == 0) { chromium = CleanIds(state.ChromiumExtensionIds); }
                if (firefox.Count == 0) { firefox = CleanIds(state.FirefoxExtensionIds); }
            }

            if (chromium.Count == 0 || firefox.Count == 0)
            {
                throw new InstallException(
                    "The update marker does not record the target extension IDs, so the browser registration cannot be finished automatically. " +
                    "Reinstall VRPC from the latest release.",
                    SetupExitCodes.Failed);
            }

            return RegistrationOptions.FromLists(chromium, firefox);
        }

        private static string ResolveBackupPath(UpdateTransaction marker, out string problem)
        {
            problem = "";
            string root = Path.GetFullPath(AppPaths.Root);
            string fallback = Path.Combine(root, "app.old");
            if (string.IsNullOrWhiteSpace(marker.BackupDir)) { return fallback; }

            if (!IsUsablePath(marker.BackupDir, allowEmpty: false, out problem)) { return ""; }

            string candidate = Path.GetFullPath(marker.BackupDir);
            if (!string.Equals(Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar)), "app.old", StringComparison.OrdinalIgnoreCase))
            {
                problem = "the backup is not the expected app.old folder";
                return "";
            }

            return candidate;
        }

        private static bool IsUsablePath(string? path, bool allowEmpty, out string problem)
        {
            problem = "";
            if (string.IsNullOrWhiteSpace(path))
            {
                if (allowEmpty) { return true; }
                problem = "the path is empty";
                return false;
            }

            if (!AppPaths.IsUnderRoot(path))
            {
                problem = $"{path} is outside {AppPaths.Root}";
                return false;
            }

            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(AppPaths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                problem = "the path is the installation root itself";
                return false;
            }

            return true;
        }

        private static void DeleteStaging(string? staging, SetupLog log)
        {
            if (string.IsNullOrWhiteSpace(staging) || !IsUsablePath(staging, allowEmpty: false, out _)) { return; }
            if (!Directory.Exists(staging)) { return; }

            try
            {
                Directory.Delete(staging, recursive: true);
                log.Write($"Recovery: removed staged files at {staging}.");
            }
            catch (Exception e)
            {
                log.Error($"Recovery: could not remove {staging}: {e.Message}");
            }
        }

        private static void DeleteDirectoryIfPresent(string path, SetupLog log)
        {
            if (string.IsNullOrWhiteSpace(path) || !AppPaths.IsUnderRoot(path) || !Directory.Exists(path)) { return; }
            try
            {
                Directory.Delete(path, recursive: true);
                log.Write($"Recovery: removed {path}.");
            }
            catch (Exception e)
            {
                log.Error($"Recovery: could not remove {path}: {e.Message}");
            }
        }

        private static bool TryMoveDirectory(string source, string destination, out string error)
        {
            error = "";
            try
            {
                if (Directory.Exists(destination)) { Directory.Delete(destination, recursive: true); }
                Directory.Move(source, destination);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
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
