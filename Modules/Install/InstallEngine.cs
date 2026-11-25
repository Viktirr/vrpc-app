using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VRPC.Application;

namespace VRPC.Install
{
    // Install/update/repair/uninstall with staging, rollback and legacy migration.
    public sealed class InstallEngine
    {
        private readonly InstallRequest _request;
        private readonly IPresenter _presenter;
        private readonly SetupLog _log;
        private readonly IInstallerRegistration _registration;
        private readonly SetupLifecycleLease? _lease;
        private readonly SetupOperationLock? _operationLock;
        private PayloadManifest? _payload;
        private InstallState? _state;
        private InstallState? _previousState;
        private LegacyInstallation? _legacy;
        private string _currentVersion = "";
        private RegistrationOptions _effectiveRegistration = RegistrationOptions.Default;
        private RegistrationOptions _previousRegistration = RegistrationOptions.Default;
        private UpdateTransaction? _transaction;
        private string? _staging;
        private string? _backup;
        private bool _hadApp;
        private bool _registrationRemoved;

        public InstallEngine(InstallRequest request, IPresenter presenter, SetupLog log, IInstallerRegistration? registration = null, SetupLifecycleLease? lease = null, SetupOperationLock? operationLock = null)
        {
            _request = request;
            _presenter = presenter;
            _log = log;
            _registration = registration ?? new InstallerRegistration();
            _lease = lease;
            _operationLock = operationLock;
        }

        // Install/update/repair entry point; uninstall has its own path.
        public async Task<InstallResult> RunAsync()
        {
            SetupOperationLock? ownedOperationLock = null;
            try
            {
                _log.Write($"Action={_request.RequestedAction?.ToString() ?? "auto"} Source={_request.SourceDirectory} Root={AppPaths.Root}");

                // The lock comes before recovery: two setup processes must never
                // recover or mutate the same installation at the same time. Setup
                // acquires it before the consent prompt and hands it in; a direct
                // engine caller (tests) still gets the same guarantee here.
                SetupOperationLock operationLock = _operationLock ?? (ownedOperationLock = SetupOperationLock.Acquire());
                _lease?.Refresh();

                RecoveryResult recovery = UpdateRecovery.Run(_log, _registration);
                if (!recovery.ShouldContinue) { return await FailureAsync(recovery.ExitCode, recovery.Message); }
                if (recovery.Recovered) { _log.Write("Recovered from an interrupted setup operation."); }

                if (_request.RequestedAction == InstallAction.Uninstall) { return await RunUninstallAsync(askFirst: true); }

                _payload = PayloadManifest.Load(_request.SourceDirectory);
                _log.Write($"Payload version={_payload.Version} files={_payload.Files.Count} channel={_payload.Channel}");

                IReadOnlyList<string> verifyErrors = _payload.Verify();
                if (verifyErrors.Count > 0)
                {
                    return await FailureAsync(SetupExitCodes.InvalidPayload, "The payload is incomplete or corrupted:\n  " + string.Join("\n  ", verifyErrors.Take(10)));
                }

                _state = InstallStateStore.Load();
                _previousState = _state;
                _legacy = _state is null ? LegacyInstallation.Detect() : null;
                _currentVersion = GetCurrentVersion();
                bool existing = _state is not null || _legacy is not null || File.Exists(AppPaths.AppExe);

                InstallAction action = DetermineAction(existing);
                ResolveRegistrations();
                InstallPlan plan = BuildPlan(action, existing);

                InstallDecision decision = await _presenter.ConfirmAsync(ConsentCopy.ForPlan(plan));
                if (decision == InstallDecision.Cancel) { return await CanceledAsync(action); }

                // The panel offers Uninstall next to Update and Repair. The user
                // has already agreed to it, so the uninstall path must not ask a
                // second time.
                if (decision == InstallDecision.Uninstall)
                {
                    if (!existing) { return await FailureAsync(SetupExitCodes.Usage, "VRPC is not installed."); }
                    _log.Write("The user chose to uninstall from the setup prompt.");
                    return await RunUninstallAsync(askFirst: false);
                }

                if (plan.ForceCloseRequired) { _log.Write("VRPC is running; the force-close prompt follows."); }

                try
                {
                    AppPaths.EnsureDirectories();
                }
                catch (Exception e)
                {
                    _log.Error($"Could not create {AppPaths.Root}: {e}");

                    // The one non-obvious cause of a denied install folder is a
                    // setup started from a folder that runs with restricted
                    // permissions; everything else is an ordinary access problem.
                    string hint = e is UnauthorizedAccessException
                        ? " If this setup was started from a sandboxed or otherwise restricted folder, move the package to a normal folder such as Desktop or Downloads and run it again."
                        : "";

                    return await FailureAsync(SetupExitCodes.Failed, $"The install folder {AppPaths.Root} could not be created: {e.Message}{hint}");
                }

                if (_legacy is not null)
                {
                    _presenter.Report("Preparing the previous installation");
                    if (!await StopHostAsync(_legacy.AppExe, _legacy.Version, InstallAction.Install, allowPipe: false)) { return await FailureAsync(SetupExitCodes.Running, "VRPC is still running."); }
                    MigrateLegacyData(_legacy);
                }

                if (File.Exists(AppPaths.AppExe))
                {
                    _presenter.Report("Stopping the running application");
                    if (!await StopHostAsync(AppPaths.AppExe, _currentVersion, action, allowPipe: true)) { return await FailureAsync(SetupExitCodes.Running, "VRPC is still running."); }
                }

                _staging = Path.Combine(AppPaths.Root, ".staging-" + Guid.NewGuid().ToString("N"));
                _backup = Path.Combine(AppPaths.Root, "app.old");
                _hadApp = Directory.Exists(AppPaths.App);

                // An app.old this transaction did not create must never be
                // deleted: it can be the only usable copy of the previous
                // install. Fail before touching anything else in that case.
                EnsureBackupDestinationIsSafe();

                // The marker must exist before anything on disk changes, including
                // the staging folder, so an interruption is always explainable.
                BeginTransaction();

                _presenter.Report($"Verifying and staging {_payload.Files.Count} files");
                try
                {
                    StagePayload(_staging);
                }
                catch (PayloadException e)
                {
                    AbortBeforeSwap();
                    return await FailureAsync(SetupExitCodes.InvalidPayload, e.Message);
                }
                catch (Exception e)
                {
                    AbortBeforeSwap();
                    return await FailureAsync(SetupExitCodes.Failed, "The new files could not be staged. Close the browser and retry. " + e.Message);
                }

                Advance(UpdatePhase.Staged);
                Advance(UpdatePhase.StoppingHost);

                if (!await StopHostAsync(File.Exists(AppPaths.AppExe) ? AppPaths.AppExe : _legacy?.AppExe ?? AppPaths.AppExe, _currentVersion, action, allowPipe: true))
                {
                    bool clean = AbortBeforeSwap();
                    return await FailureAsync(SetupExitCodes.Running, "VRPC started again and is still running. Close the browser and retry."
                        + (clean ? "" : " The update marker was kept because the previous registration could not be restored."));
                }

                if (_registration.Enabled)
                {
                    // Persist the intent before touching the registration. Recovery
                    // re-applies the previous IDs idempotently, so this closes the
                    // window where the keys are gone but the marker still claims
                    // they were never removed.
                    _registrationRemoved = true;
                    Advance(UpdatePhase.StoppingHost);
                    _registration.Remove(_state);
                }
                Advance(UpdatePhase.RegistrationRemoved);
                Advance(UpdatePhase.Committing);

                _presenter.Report("Replacing the application files");
                try
                {
                    Commit(_staging, _backup, _hadApp);
                    _staging = null;
                }
                catch (InstallException e)
                {
                    _log.Error("Commit failed: " + e.Message);
                    AbortAfterCommitFailure();
                    return await FailureAsync(e.ExitCode, e.Message);
                }
                catch (Exception e)
                {
                    _log.Error("Commit failed: " + e.Message);
                    bool clean = AbortAfterCommitFailure();
                    return await FailureAsync(SetupExitCodes.Failed, "The application files could not be replaced. Close the browser and retry. " + e.Message
                        + (clean ? "" : " The update marker was kept for manual recovery."));
                }

                Advance(UpdatePhase.Committed);
                Advance(UpdatePhase.Verifying);

                _presenter.Report("Running the self-test");
                if (!SmokeTest(AppPaths.AppExe)) { return await RollbackAndFailAsync("The new version failed its self-test."); }

                Advance(UpdatePhase.Registering);
                _presenter.Report("Registering native messaging");
                TransactionResult applied = ApplyRegistrationAndState();
                if (!applied.Success) { return await RollbackAndFailAsync(applied.Error); }

                IReadOnlyList<string> postCommitErrors = VerifyCommittedInstall();
                if (postCommitErrors.Count > 0)
                {
                    return await RollbackAndFailAsync("The installed version did not verify: " + string.Join("; ", postCommitErrors));
                }

                Advance(UpdatePhase.Done);
                _presenter.Report("Cleaning up");
                TryDeleteDirectory(_backup);
                InstallStateStore.DeleteTransaction();
                _transaction = null;
                CleanupLegacy();
                _log.Write("Install/update completed.");

                InstallResult result = new()
                {
                    Success = true,
                    ExitCode = SetupExitCodes.Success,
                    Title = ConsentCopy.ResultTitle(action),
                    Message = action switch
                    {
                        InstallAction.Install => "VRPC is installed. Your settings and listening history were kept.",
                        InstallAction.Repair => $"VRPC {_payload.Version} was repaired. Your settings and listening history were kept.",
                        _ => $"VRPC was updated to {_payload.Version}."
                    },
                    Version = _payload.Version,
                    LogPath = _log.Path
                };
                _lease?.Dispose();
                await _presenter.CompleteAsync(result);
                return result;
            }
            catch (PayloadException e)
            {
                return await FailureAsync(SetupExitCodes.InvalidPayload, e.Message);
            }
            catch (InstallException e)
            {
                return await FailureAsync(e.ExitCode, e.Message);
            }
            catch (Exception e)
            {
                _log.Error(e.ToString());
                return await FailureAsync(SetupExitCodes.Failed, $"Installation failed: {e.Message}");
            }
            finally
            {
                ownedOperationLock?.Dispose();
            }
        }

        // Chooses the IDs the new registration must use; an update cannot fall
        // back to the official defaults without losing a custom registration.
        private void ResolveRegistrations()
        {
            _effectiveRegistration = RegistrationPolicy.ResolveTarget(_state, _request.Registration, _registration.Enabled);
            _previousRegistration = RegistrationPolicy.ResolvePrevious(_state, _request.Registration, _registration.Enabled);
            _log.Write($"Registration: chromium=[{string.Join(", ", _effectiveRegistration.ChromiumExtensionIds)}] firefox=[{string.Join(", ", _effectiveRegistration.FirefoxExtensionIds)}] previousChromium=[{string.Join(", ", _previousRegistration.ChromiumExtensionIds)}] previousFirefox=[{string.Join(", ", _previousRegistration.FirefoxExtensionIds)}]");
        }

        // Picks install, update, repair or a rejected downgrade.
        private InstallAction DetermineAction(bool existing)
        {
            if (_payload is null) { throw new InvalidOperationException("Payload is not loaded."); }
            if (!existing)
            {
                if (_request.RequestedAction == InstallAction.Update) { throw new InstallException("VRPC is not installed; there is nothing to update.", SetupExitCodes.Usage); }
                if (_request.RequestedAction == InstallAction.Repair) { throw new InstallException("VRPC is not installed; there is nothing to repair.", SetupExitCodes.Usage); }
                return InstallAction.Install;
            }

            int comparison = ProductInfo.CompareVersions(_payload.Version, _currentVersion);
            if (comparison < 0 && !_request.AllowDowngrade)
            {
                throw new InstallException($"The package is version {_payload.Version}, but {_currentVersion} is already installed. Downgrades are blocked; pass --allow-downgrade to override.", SetupExitCodes.Failed);
            }

            return comparison == 0 ? InstallAction.Repair : InstallAction.Update;
        }

        private InstallPlan BuildPlan(InstallAction action, bool existing)
        {
            if (_payload is null) { throw new InvalidOperationException("Payload is not loaded."); }

            List<string> warnings = new();
            if (_legacy is not null) { warnings.Add($"Legacy installation detected at {_legacy.Root}; settings and listening data will be migrated."); }
            if (existing && _currentVersion.Length > 0) { warnings.Add($"Installed version: {_currentVersion}."); }
            warnings.Add("Settings and listening history are kept during updates.");
            if (_request.PurgeData) { warnings.Add("Uninstall will delete settings and listening history."); }

            bool forceClose = ProcessHelper.IsRunning(AppPaths.AppExe) || (_legacy is not null && ProcessHelper.IsRunning(_legacy.AppExe));
            return new InstallPlan
            {
                Action = action,
                CurrentVersion = _currentVersion,
                TargetVersion = _payload.Version,
                Root = AppPaths.Root,
                AppDir = AppPaths.App,
                DataDir = AppPaths.Data,
                LegacyInstall = _legacy is not null,
                FileCount = _payload.Files.Count,
                ForceCloseRequired = forceClose,
                Warnings = warnings
            };
        }

        // Stages under the install root so the swap stays on one volume.
        private void StagePayload(string staging)
        {
            if (_payload is null) { throw new InvalidOperationException("Payload is not loaded."); }
            _log.Write($"Staging {_payload.Files.Count} files in {staging}");
            _payload.CopyTo(staging);

            IReadOnlyList<string> errors = _payload.VerifyDirectory(staging);
            if (errors.Count > 0)
            {
                TryDeleteDirectory(staging);
                throw new PayloadException("The staged payload did not verify:\n  " + string.Join("\n  ", errors.Take(10)));
            }
        }

        // Swaps app/ with the verified staging folder.
        private void Commit(string staging, string backup, bool hadApp)
        {
            if (Directory.Exists(backup))
            {
                throw new InstallException(
                    $"An unexpected previous-version backup at {backup} was found while replacing the application files, so VRPC stopped instead of deleting it. " +
                    "It was kept for manual recovery; move it out of the installation folder and run setup again.",
                    SetupExitCodes.Failed);
            }

            if (hadApp)
            {
                _log.Write($"Moving {AppPaths.App} to {backup}");
                Directory.Move(AppPaths.App, backup);
            }

            try
            {
                _log.Write($"Moving {staging} to {AppPaths.App}");
                Directory.Move(staging, AppPaths.App);
            }
            catch
            {
                if (hadApp && Directory.Exists(backup) && !Directory.Exists(AppPaths.App))
                {
                    Directory.Move(backup, AppPaths.App);
                }
                throw;
            }
        }

        // Reconciles a leftover app.old before the transaction marker is written.
        // With the current app present the leftover is renamed aside; without an
        // app it stops the operation so the only usable copy is never deleted.
        private void EnsureBackupDestinationIsSafe()
        {
            if (_backup is null) { throw new InvalidOperationException("The backup path is not initialized."); }
            InstallBackupGuard.Reconcile(_backup, _hadApp, _log);
        }

        // Restores the known previous app after a failed self-test, registration
        // or post-commit verification. Returns false when the restore itself
        // failed and the evidence has to stay on disk.
        private bool TryRollbackCommit()
        {
            try
            {
                if (!_hadApp)
                {
                    if (Directory.Exists(AppPaths.App)) { Directory.Delete(AppPaths.App, recursive: true); }
                    if (Directory.Exists(_backup)) { Directory.Delete(_backup, recursive: true); }
                    _log.Write("Rollback completed: the new installation was removed.");
                    return true;
                }

                if (Directory.Exists(AppPaths.App)) { Directory.Delete(AppPaths.App, recursive: true); }
                if (Directory.Exists(_backup)) { Directory.Move(_backup, AppPaths.App); }
                _log.Write("Rollback completed.");
                return true;
            }
            catch (Exception e)
            {
                _log.Error("Rollback failed: " + e.Message);
                return false;
            }
        }

        // Restores the recorded previous state and proves it by reading the file
        // back. A failed write or a mismatching read is never treated as success.
        private bool RestorePreviousInstallState()
        {
            if (_previousState is not null)
            {
                if (!InstallStateStore.TrySave(_previousState, out string saveError))
                {
                    _log.Error("Could not restore the install state: " + saveError);
                    return false;
                }
            }
            else
            {
                if (!InstallStateStore.TryDelete(out string deleteError))
                {
                    _log.Error("Could not remove the target install state: " + deleteError);
                    return false;
                }
            }

            InstallState? restored = InstallStateStore.Load();
            bool matches = _previousState is null
                ? restored is null
                : restored is not null && InstallVerification.VersionMatches(restored.Version, _previousState.Version);
            if (!matches)
            {
                _log.Error($"The restored install state does not read back as {_previousState?.Version ?? "absent"}.");
                return false;
            }

            _state = _previousState;
            return true;
        }

        // Registers only after the new exe passes its self-test.
        private TransactionResult ApplyRegistrationAndState()
        {
            if (_payload is null) { throw new InvalidOperationException("Payload is not loaded."); }

            try
            {
                RegistrationResult registration = new();
                if (_registration.Enabled)
                {
                    registration = _registration.Apply(AppPaths.AppExe, _effectiveRegistration, _payload.Version);
                }

                InstallState state = new()
                {
                    Schema = InstallState.CurrentSchema,
                    Version = _payload.Version,
                    Channel = _payload.Channel,
                    AppDir = AppPaths.App,
                    DataDir = AppPaths.Data,
                    InstalledAtUtc = DateTime.UtcNow.ToString("o"),
                    PreviousVersion = _currentVersion,
                    MigratedFrom = _legacy?.Root ?? "",
                    ArpKey = ArpRegistration.KeyPath,
                    ChromiumExtensionIds = _effectiveRegistration.ChromiumExtensionIds.ToList(),
                    FirefoxExtensionIds = _effectiveRegistration.FirefoxExtensionIds.ToList(),
                    Manifests = registration.Manifests,
                    RegistryKeys = registration.RegistryKeys
                };
                InstallStateStore.Save(state);
                if (_registration.Enabled) { _registration.RemoveLegacy(); }
                _state = state;
                return new TransactionResult { Success = true };
            }
            catch (Exception e)
            {
                _log.Error("Registration/state failed: " + e);
                return new TransactionResult { Success = false, Error = "The application was installed but registration failed: " + e.Message };
            }
        }

        // Verifies the executable, the install state and the recorded manifests
        // before app.old and the marker are removed.
        private IReadOnlyList<string> VerifyCommittedInstall()
        {
            List<string> errors = new();
            if (_payload is null)
            {
                errors.Add("the payload is not loaded");
                return errors;
            }

            errors.AddRange(InstallVerification.VerifyApp(AppPaths.App, _payload.Version));
            errors.AddRange(InstallVerification.VerifyInstallState(_payload.Version));

            if (_registration.Enabled && errors.Count == 0)
            {
                InstallState? state = InstallStateStore.Load();
                IReadOnlyList<RegisteredManifest> manifests = state is not null ? state.Manifests : Array.Empty<RegisteredManifest>();
                try { _registration.Verify(AppPaths.AppExe, manifests); }
                catch (RegistrationException e) { errors.Add(e.Message); }
            }

            return errors;
        }

        // Puts the previous registration back after a rollback. Returns false when
        // it could not be restored, so the marker is kept.
        private bool RestoreRegistration()
        {
            if (!_registrationRemoved) { return true; }

            try
            {
                string? oldExe = null;
                if (_previousState is not null && File.Exists(AppPaths.AppExe)) { oldExe = AppPaths.AppExe; }
                if (oldExe is null && _legacy is not null && File.Exists(_legacy.AppExe)) { oldExe = _legacy.AppExe; }

                if (oldExe is not null)
                {
                    RegistrationResult applied = _registration.Apply(oldExe, _previousRegistration, _currentVersion);
                    if (_previousState is not null && applied.Manifests.Count > 0)
                    {
                        _previousState.Manifests = applied.Manifests;
                        _previousState.RegistryKeys = applied.RegistryKeys;
                    }
                    _log.Write("Restored the previous native-messaging registration.");
                }
                else
                {
                    _registration.Remove(null);
                    _registration.RemoveArp();
                    _log.Write("Removed the native-messaging registration of the failed installation.");
                }

                _registrationRemoved = false;
                return true;
            }
            catch (Exception e)
            {
                _log.Error("Could not restore registration: " + e.Message);
                return false;
            }
        }

        // True when the on-disk state still describes the version that was
        // installed before this transaction (including "no state" for a fresh
        // install).
        private bool StateMatchesPrevious()
        {
            InstallState? current = InstallStateStore.Load();
            if (_previousState is null) { return current is null; }
            return current is not null && InstallVerification.VersionMatches(current.Version, _previousState.Version);
        }

        private bool VerifyRestoredRegistration()
        {
            if (!_registration.Enabled || _previousState is null) { return true; }
            try
            {
                InstallState? state = InstallStateStore.Load();
                _registration.Verify(AppPaths.AppExe, state?.Manifests ?? new List<RegisteredManifest>());
                return true;
            }
            catch (RegistrationException e)
            {
                _log.Error("The restored registration did not verify: " + e.Message);
                return false;
            }
        }

        private bool AbortBeforeSwap()
        {
            TryDeleteDirectory(_staging ?? "");
            _staging = null;
            bool restored = RestoreRegistration();
            bool stateOk = StateMatchesPrevious();
            bool appOk = !_hadApp || InstallVerification.VerifyApp(AppPaths.App, _currentVersion).Count == 0;
            bool registrationOk = VerifyRestoredRegistration();
            if (restored && stateOk && appOk && registrationOk)
            {
                InstallStateStore.DeleteTransaction();
                _transaction = null;
            }
            else
            {
                _log.Error("The update marker was kept because the previous registration, install state or application files could not be verified.");
            }

            return restored && stateOk && appOk;
        }

        // A failed commit may have left the application folder half-moved; the
        // commit itself already tried to put the backup back. The marker is only
        // removed when the disk really is back in a consistent state, otherwise
        // the next run's recovery restores it.
        private bool AbortAfterCommitFailure()
        {
            TryDeleteDirectory(_staging ?? "");
            _staging = null;
            bool restored = RestoreRegistration();
            bool diskConsistent = _hadApp
                ? Directory.Exists(AppPaths.App) && !Directory.Exists(_backup)
                : !Directory.Exists(_backup) || Directory.Exists(AppPaths.App);
            bool stateOk = StateMatchesPrevious();
            bool appOk = !_hadApp || InstallVerification.VerifyApp(AppPaths.App, _currentVersion).Count == 0;
            bool registrationOk = VerifyRestoredRegistration();
            if (restored && diskConsistent && stateOk && appOk && registrationOk)
            {
                InstallStateStore.DeleteTransaction();
                _transaction = null;
                return true;
            }

            _log.Error("The update marker was kept because the installation could not be verified after the failed commit.");
            return false;
        }

        private async Task<InstallResult> RollbackAndFailAsync(string message)
        {
            bool filesRestored = TryRollbackCommit();
            bool registrationRestored = filesRestored && RestoreRegistration();
            bool stateRestored = filesRestored && RestorePreviousInstallState();

            bool appVerified = !_hadApp || (filesRestored && InstallVerification.VerifyApp(AppPaths.App, _currentVersion).Count == 0);
            bool stateVerified = _previousState is null
                ? InstallStateStore.Load() is null
                : InstallVerification.VerifyInstallState(_previousState.Version).Count == 0;
            bool registrationVerified = !registrationRestored || VerifyRestoredRegistration();

            if (filesRestored && registrationRestored && stateRestored && appVerified && stateVerified && registrationVerified)
            {
                InstallStateStore.DeleteTransaction();
                _transaction = null;
                _log.Write("Rolled back to the previous installation.");
                return await FailureAsync(SetupExitCodes.Failed, message + " The previous installation was restored.");
            }

            _log.Error("Automatic rollback was incomplete; the update marker was kept.");
            return await FailureAsync(SetupExitCodes.Failed,
                message + " Automatic rollback was incomplete; the update files and the update marker were kept for manual recovery. " +
                "Reinstall VRPC from the latest release. Your settings and listening history are kept.");
        }

        // Runs --selftest and checks its output marker.
        private bool SmokeTest(string appExe)
        {
            if (!File.Exists(appExe))
            {
                _log.Error("Self-test failed: the application executable is missing.");
                return false;
            }

            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = appExe,
                    WorkingDirectory = AppPaths.App,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // The child resolves its own roots, and the self-test refuses to
                // run outside <root>\app. Passing the root it was installed into
                // is what makes that guard hold for --root and VRPC_ROOT as well
                // as for the default location.
                startInfo.ArgumentList.Add("--selftest");
                startInfo.ArgumentList.Add("--root");
                startInfo.ArgumentList.Add(AppPaths.Root);

                using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The self-test process did not start.");
                bool exited = process.WaitForExit(15000);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(2000); } catch { }
                    _log.Error("Self-test timed out.");
                }

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                _log.Write($"Self-test exit={process.ExitCode} stdout={output.Trim()} stderr={error.Trim()}");
                return exited && process.ExitCode == 0 && output.Contains("selftest ok", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                _log.Error("Self-test failed: " + e.Message);
                return false;
            }
        }

        // Copies known data; new files win and old copies are kept.
        private void MigrateLegacyData(LegacyInstallation legacy)
        {
            if (!Directory.Exists(legacy.DataDir))
            {
                _log.Write("No legacy data folder was found.");
                return;
            }

            Directory.CreateDirectory(AppPaths.Data);
            List<string> migrated = new();
            foreach (string name in new[] { "Config.json", "ListeningData.json", "ListeningData_backup.json" })
            {
                string source = Path.Combine(legacy.DataDir, name);
                if (!File.Exists(source)) { continue; }

                string destination = Path.Combine(AppPaths.Data, name);
                try
                {
                    if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                    {
                        File.Copy(source, destination, overwrite: true);
                        migrated.Add(name);
                        _log.Write($"Migrated {name}.");
                    }
                    else
                    {
                        string backup = destination + ".legacy-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Copy(source, backup, overwrite: true);
                        migrated.Add(Path.GetFileName(backup));
                        _log.Write($"Kept the new {name} and backed up the legacy copy to {Path.GetFileName(backup)}.");
                    }
                }
                catch (Exception e)
                {
                    _log.Error($"Could not migrate {name}: {e.Message}");
                    throw new InstallException($"Could not migrate {name} from the legacy installation: {e.Message}", SetupExitCodes.Failed);
                }
            }

            try
            {
                string marker = JsonSerializer.Serialize(new
                {
                    source = legacy.Root,
                    migratedAtUtc = DateTime.UtcNow.ToString("o"),
                    files = migrated
                }, InstallJson.Options);
                File.WriteAllText(AppPaths.MigrationMarkerPath, marker, new UTF8Encoding(false));
            }
            catch { }
        }

        // Runs only after the new version is committed and self-tested, so the migrated copy is
        // already in place before the legacy tree disappears.
        private void CleanupLegacy()
        {
            if (_legacy is null) { return; }

            try
            {
                string legacyRoot = _legacy.Root;
                if (Directory.Exists(legacyRoot) && !AppPaths.IsUnderRoot(legacyRoot))
                {
                    Directory.Delete(legacyRoot, recursive: true);
                    _log.Write($"Removed the legacy installation at {legacyRoot}.");
                    return;
                }

                if (File.Exists(AppPaths.LegacyLock)) { File.Delete(AppPaths.LegacyLock); }
            }
            catch (Exception e)
            {
                _log.Error("Could not fully remove the legacy files: " + e.Message);
            }
        }

        // Removes the updater's downloaded/extracted packages and its cached
        // state. User data is untouched: it is only removed by the --purge path.
        private void CleanupUpdaterArtifacts()
        {
            string updates = AppPaths.UpdatesDir;
            try
            {
                if (Directory.Exists(updates) && AppPaths.IsUnderUpdatesRoot(updates))
                {
                    Directory.Delete(updates, recursive: true);
                    _log.Write("Removed the updater download folder.");
                }
            }
            catch (Exception e)
            {
                _log.Error("Could not remove the updater download folder: " + e.Message);
            }

            foreach (string suffix in new[] { "", ".bak" })
            {
                string path = AppPaths.UpdaterStatePath + suffix;
                try
                {
                    if (!File.Exists(path) || !AppPaths.IsUnderRoot(path)) { continue; }
                    File.Delete(path);
                    _log.Write($"Removed {Path.GetFileName(path)}.");
                }
                catch (Exception e)
                {
                    _log.Error($"Could not remove {Path.GetFileName(path)}: " + e.Message);
                }
            }
        }

        // Tries the control pipe first, then asks before force-closing. The
        // question is the panel's second screen when the panel is available.
        private async Task<bool> StopHostAsync(string exePath, string version, InstallAction action, bool allowPipe)
        {
            if (!ProcessHelper.IsRunning(exePath)) { return true; }

            if (allowPipe && OperatingSystem.IsWindows())
            {
                _log.Write("Requesting shutdown through the control pipe.");
                if (ControlClient.TryShutdown(TimeSpan.FromSeconds(3)) && ProcessHelper.WaitForExit(exePath, TimeSpan.FromSeconds(10)))
                {
                    return true;
                }
            }

            if (!_request.Force && !await _presenter.ConfirmForceCloseAsync(ConsentCopy.ForceClose(action, version, exePath)))
            {
                _log.Write("The user chose not to close the running application.");
                return false;
            }

            _log.Write("Force-closing the running application.");
            ProcessHelper.Kill(exePath);
            return ProcessHelper.WaitForExit(exePath, TimeSpan.FromSeconds(10));
        }

        private string GetCurrentVersion()
        {
            if (_state is not null && !string.IsNullOrWhiteSpace(_state.Version)) { return _state.Version; }
            if (_legacy is not null) { return _legacy.Version; }

            try
            {
                if (File.Exists(AppPaths.AppExe))
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(AppPaths.AppExe);
                    string? value = info.ProductVersion ?? info.FileVersion;
                    if (!string.IsNullOrWhiteSpace(value)) { return value.Split('+')[0]; }
                }
            }
            catch { }

            return "";
        }

        // Removes registration, app files, then ARP; data stays unless --purge.
        // `askFirst` is false when the consent prompt already offered Uninstall.
        private async Task<InstallResult> RunUninstallAsync(bool askFirst)
        {
            _state = InstallStateStore.Load();
            _legacy = LegacyInstallation.Detect();
            _currentVersion = GetCurrentVersion();
            bool existing = _state is not null || _legacy is not null || File.Exists(AppPaths.AppExe);
            if (!existing) { return await FailureAsync(SetupExitCodes.Usage, "VRPC is not installed."); }

            InstallPlan plan = new()
            {
                Action = InstallAction.Uninstall,
                CurrentVersion = _currentVersion,
                TargetVersion = _currentVersion,
                Root = AppPaths.Root,
                AppDir = AppPaths.App,
                DataDir = AppPaths.Data,
                LegacyInstall = _legacy is not null,
                FileCount = 0,
                ForceCloseRequired = ProcessHelper.IsRunning(AppPaths.AppExe),
                PurgeData = _request.PurgeData
            };

            if (askFirst)
            {
                InstallDecision decision = await _presenter.ConfirmAsync(ConsentCopy.ForPlan(plan));
                if (decision != InstallDecision.Proceed)
                {
                    _log.Write("The user did not confirm the uninstallation.");
                    return await CanceledAsync(InstallAction.Uninstall);
                }
            }
            else
            {
                _presenter.Report("Stopping the running application", "Uninstalling VRPC");
            }

            if (!await StopHostAsync(AppPaths.AppExe, _currentVersion, InstallAction.Uninstall, allowPipe: true))
            {
                return await FailureAsync(SetupExitCodes.Running, "VRPC is still running. Close it and retry.");
            }

            if (_legacy is not null && !await StopHostAsync(_legacy.AppExe, _legacy.Version, InstallAction.Uninstall, allowPipe: false))
            {
                return await FailureAsync(SetupExitCodes.Running, "The legacy VRPC is still running.");
            }

            _presenter.Report("Removing the application files");

            if (_registration.Enabled)
            {
                _registration.Remove(_state);
                _registration.RemoveAllKeys();
            }

            try
            {
                if (Directory.Exists(AppPaths.App)) { Directory.Delete(AppPaths.App, recursive: true); }
                if (Directory.Exists(AppPaths.Manifests)) { Directory.Delete(AppPaths.Manifests, recursive: true); }
                if (_legacy is not null) { CleanupLegacy(); }
            }
            catch (Exception e)
            {
                _log.Error("Could not delete the application files: " + e.Message);
                return await FailureAsync(SetupExitCodes.Failed, "The application files could not be deleted. Close the browser and retry. " + e.Message);
            }

            if (_request.PurgeData)
            {
                _presenter.Report("Removing your settings and listening history");
                try { if (Directory.Exists(AppPaths.Data)) { Directory.Delete(AppPaths.Data, recursive: true); } } catch { }
                if (_legacy is not null)
                {
                    try { if (Directory.Exists(_legacy.DataDir)) { Directory.Delete(_legacy.DataDir, recursive: true); } } catch { }
                }
            }

            try
            {
                InstallStateStore.Delete();
                InstallStateStore.DeleteTransaction();
            }
            catch { }

            // Uninstall also removes the updater's scratch files and cached
            // state. Settings and listening data stay unless --purge.
            CleanupUpdaterArtifacts();

            _presenter.Report("Removing the registry keys");

            if (_registration.Enabled)
            {
                _registration.RemoveArp();
                _registration.RemoveLegacy();
                _registration.RemoveAllKeys();
            }

            InstallResult result = new()
            {
                Success = true,
                ExitCode = SetupExitCodes.Success,
                Title = ConsentCopy.ResultTitle(InstallAction.Uninstall),
                Message = _request.PurgeData
                    ? "VRPC was uninstalled and its data was removed."
                    : "VRPC was uninstalled. Your settings and listening history were kept.",
                Version = _currentVersion,
                LogPath = _log.Path
            };
            _lease?.Dispose();
            await _presenter.CompleteAsync(result);
            return result;
        }

        // Writes the transaction marker before any file changes.
        private void BeginTransaction()
        {
            if (_payload is null) { throw new InvalidOperationException("Payload is not loaded."); }
            if (_staging is null || _backup is null) { throw new InvalidOperationException("The staging paths are not initialized."); }

            UpdateTransaction transaction = new()
            {
                Schema = UpdateTransaction.CurrentSchema,
                Phase = UpdatePhase.Staging,
                TargetVersion = _payload.Version,
                PreviousVersion = _currentVersion,
                StagingDir = _staging,
                BackupDir = _backup,
                HadAppBefore = _hadApp,
                RegistrationRemoved = false,
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                PreviousInstallState = _previousState is null ? null : InstallStateStore.Clone(_previousState),
                TargetChromiumExtensionIds = _effectiveRegistration.ChromiumExtensionIds.ToList(),
                TargetFirefoxExtensionIds = _effectiveRegistration.FirefoxExtensionIds.ToList(),
                PreviousChromiumExtensionIds = _previousRegistration.ChromiumExtensionIds.ToList(),
                PreviousFirefoxExtensionIds = _previousRegistration.FirefoxExtensionIds.ToList()
            };
            _transaction = transaction;
            InstallStateStore.SaveTransaction(transaction);
            _log.Write($"Update marker written: phase={transaction.Phase} target={transaction.TargetVersion} previous={transaction.PreviousVersion} hadApp={transaction.HadAppBefore}");
        }

        // Records the current phase so an interruption is always explainable.
        private void Advance(string phase)
        {
            if (_transaction is null) { return; }
            _transaction.Phase = phase;
            _transaction.RegistrationRemoved = _registrationRemoved;
            InstallStateStore.SaveTransaction(_transaction);
            _lease?.Refresh();
            _log.Write($"Update marker: phase={phase} registrationRemoved={_registrationRemoved}");
        }

        // Logs and presents a failure result.
        private async Task<InstallResult> FailureAsync(int exitCode, string message)
        {
            _log.Error(message);
            InstallResult result = new()
            {
                Success = false,
                ExitCode = exitCode,
                Title = "VRPC setup failed",
                Message = message,
                Version = _payload?.Version ?? "",
                LogPath = _log.Path
            };
            // A kept marker still blocks host startup; the lease itself only needs
            // to cover the mutation window, not how long a human reads the error.
            _lease?.Dispose();
            await _presenter.FailAsync(result);
            return result;
        }

        // A cancel is not a failure: the panel has already closed itself, so the
        // presenter decides whether anything is still worth printing.
        private async Task<InstallResult> CanceledAsync(InstallAction action)
        {
            _log.Write($"{action} canceled by the user.");
            InstallResult result = new()
            {
                Success = false,
                ExitCode = SetupExitCodes.Canceled,
                Title = ConsentCopy.ResultTitle(action) + " canceled",
                Message = action == InstallAction.Uninstall ? "Nothing was uninstalled." : "Nothing was changed.",
                LogPath = _log.Path
            };
            _lease?.Dispose();
            await _presenter.CanceledAsync(result);
            return result;
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) { Directory.Delete(path, recursive: true); } } catch { }
        }

        private sealed class TransactionResult
        {
            public bool Success { get; init; }
            public string Error { get; init; } = "";
        }
    }

    public sealed class InstallException : Exception
    {
        public int ExitCode { get; }

        public InstallException(string message, int exitCode) : base(message)
        {
            ExitCode = exitCode;
        }
    }
}
