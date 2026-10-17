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
        private PayloadManifest? _payload;
        private InstallState? _state;
        private LegacyInstallation? _legacy;
        private string _currentVersion = "";
        private bool SkipRegistration => string.Equals(Environment.GetEnvironmentVariable("VRPC_NO_REGISTRY"), "1", StringComparison.Ordinal);

        public InstallEngine(InstallRequest request, IPresenter presenter, SetupLog log)
        {
            _request = request;
            _presenter = presenter;
            _log = log;
        }

        // Install/update/repair entry point; uninstall has its own path.
        public InstallResult Run()
        {
            try
            {
                _log.Write($"Action={_request.RequestedAction?.ToString() ?? "auto"} Source={_request.SourceDirectory} Root={AppPaths.Root}");
                RecoverInterruptedTransaction();

                if (_request.RequestedAction == InstallAction.Uninstall) { return RunUninstall(); }

                _payload = PayloadManifest.Load(_request.SourceDirectory);
                _log.Write($"Payload version={_payload.Version} files={_payload.Files.Count} channel={_payload.Channel}");

                IReadOnlyList<string> verifyErrors = _payload.Verify();
                if (verifyErrors.Count > 0)
                {
                    return Failure(SetupExitCodes.InvalidPayload, "The payload is incomplete or corrupted:\n  " + string.Join("\n  ", verifyErrors.Take(10)));
                }

                _state = InstallStateStore.Load();
                _legacy = _state is null ? LegacyInstallation.Detect() : null;
                _currentVersion = GetCurrentVersion();
                bool existing = _state is not null || _legacy is not null || File.Exists(AppPaths.AppExe);

                InstallAction action = DetermineAction(existing);
                InstallPlan plan = BuildPlan(action, existing);

                if (!_presenter.Confirm(plan))
                {
                    return Failure(SetupExitCodes.Canceled, "Installation canceled.");
                }

                AppPaths.EnsureDirectories();

                if (_legacy is not null)
                {
                    if (!StopHost(_legacy.AppExe, version: _legacy.Version, allowPipe: false)) { return Failure(SetupExitCodes.Running, "VRPC is still running."); }
                    MigrateLegacyData(_legacy);
                }

                if (File.Exists(AppPaths.AppExe))
                {
                    if (!StopHost(AppPaths.AppExe, version: _currentVersion, allowPipe: true)) { return Failure(SetupExitCodes.Running, "VRPC is still running."); }
                }

                string staging = StagePayload();
                string backup = Path.Combine(AppPaths.Root, "app.old");

                if (!StopHost(File.Exists(AppPaths.AppExe) ? AppPaths.AppExe : _legacy?.AppExe ?? AppPaths.AppExe, version: _currentVersion, allowPipe: true))
                {
                    TryDeleteDirectory(staging);
                    return Failure(SetupExitCodes.Running, "VRPC started again and is still running. Close the browser and retry.");
                }

                if (!SkipRegistration) { RegistrationManager.Remove(_state); }

                bool hadApp = Directory.Exists(AppPaths.App);
                UpdateTransaction transaction = new()
                {
                    Phase = UpdatePhase.Committing,
                    TargetVersion = _payload.Version,
                    PreviousVersion = _currentVersion,
                    StagingDir = staging,
                    BackupDir = backup,
                    StartedAtUtc = DateTime.UtcNow.ToString("o")
                };
                InstallStateStore.SaveTransaction(transaction);

                try
                {
                    Commit(staging, backup, hadApp);
                }
                catch (Exception e)
                {
                    _log.Error("Commit failed: " + e.Message);
                    TryDeleteDirectory(staging);
                    RestoreRegistration();
                    InstallStateStore.DeleteTransaction();
                    return Failure(SetupExitCodes.Failed, "The application files could not be replaced. Close the browser and retry. " + e.Message);
                }

                if (!SmokeTest(AppPaths.AppExe))
                {
                    RollbackCommit(backup, hadApp);
                    RestoreRegistration();
                    InstallStateStore.DeleteTransaction();
                    return Failure(SetupExitCodes.Failed, "The new version failed its self-test. The previous installation was restored.");
                }

                TransactionResult applied = ApplyRegistrationAndState();
                if (!applied.Success)
                {
                    RollbackCommit(backup, hadApp);
                    RestoreRegistration();
                    InstallStateStore.DeleteTransaction();
                    return Failure(SetupExitCodes.Failed, applied.Error);
                }

                TryDeleteDirectory(backup);
                InstallStateStore.DeleteTransaction();
                CleanupLegacy();
                _log.Write("Install/update completed.");

                InstallResult result = new()
                {
                    Success = true,
                    ExitCode = SetupExitCodes.Success,
                    Message = action switch
                    {
                        InstallAction.Install => "VRPC is installed. Your settings and listening history were kept.",
                        InstallAction.Repair => $"VRPC {_payload.Version} was repaired. Your settings and listening history were kept.",
                        _ => $"VRPC was updated to {_payload.Version}."
                    },
                    Version = _payload.Version,
                    LogPath = _log.Path
                };
                _presenter.Complete(result);
                return result;
            }
            catch (PayloadException e)
            {
                return Failure(SetupExitCodes.InvalidPayload, e.Message);
            }
            catch (InstallException e)
            {
                return Failure(e.ExitCode, e.Message);
            }
            catch (Exception e)
            {
                _log.Error(e.ToString());
                return Failure(SetupExitCodes.Failed, $"Installation failed: {e.Message}");
            }
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
        private string StagePayload()
        {
            if (_payload is null) { throw new InvalidOperationException("Payload is not loaded."); }
            string staging = Path.Combine(AppPaths.Root, ".staging-" + Guid.NewGuid().ToString("N"));
            _log.Write($"Staging {_payload.Files.Count} files in {staging}");
            _payload.CopyTo(staging);

            IReadOnlyList<string> errors = _payload.VerifyDirectory(staging);
            if (errors.Count > 0)
            {
                TryDeleteDirectory(staging);
                throw new PayloadException("The staged payload did not verify:\n  " + string.Join("\n  ", errors.Take(10)));
            }

            return staging;
        }

        // Swaps app/ with the verified staging folder.
        private void Commit(string staging, string backup, bool hadApp)
        {
            if (Directory.Exists(backup)) { TryDeleteDirectory(backup); }
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

        // Restores app.old after a failed self-test or registration.
        private void RollbackCommit(string backup, bool hadApp)
        {
            try
            {
                if (Directory.Exists(AppPaths.App)) { Directory.Delete(AppPaths.App, recursive: true); }
                if (hadApp && Directory.Exists(backup)) { Directory.Move(backup, AppPaths.App); }
                _log.Write("Rollback completed.");
            }
            catch (Exception e)
            {
                _log.Error("Rollback failed: " + e.Message);
            }
        }

        // Registers only after the new exe passes its self-test.
        private TransactionResult ApplyRegistrationAndState()
        {
            if (_payload is null) { throw new InvalidOperationException("Payload is not loaded."); }

            try
            {
                RegistrationResult registration = SkipRegistration
                    ? new RegistrationResult()
                    : RegistrationManager.Apply(AppPaths.AppExe, _request.Registration);
                if (!SkipRegistration) { ArpRegistration.Write(_payload.Version, AppPaths.AppExe, AppPaths.AppExe); }

                InstallState state = new()
                {
                    Version = _payload.Version,
                    Channel = _payload.Channel,
                    AppDir = AppPaths.App,
                    DataDir = AppPaths.Data,
                    InstalledAtUtc = DateTime.UtcNow.ToString("o"),
                    PreviousVersion = _currentVersion,
                    MigratedFrom = _legacy?.Root ?? "",
                    ArpKey = ArpRegistration.KeyPath,
                    ExtensionIds = registration.ExtensionIds,
                    Manifests = registration.Manifests,
                    RegistryKeys = registration.RegistryKeys
                };
                InstallStateStore.Save(state);
                if (!SkipRegistration) { ArpRegistration.RemoveLegacy(); }
                _state = state;
                return new TransactionResult { Success = true };
            }
            catch (Exception e)
            {
                _log.Error("Registration/state failed: " + e);
                return new TransactionResult { Success = false, Error = "The application was installed but registration failed: " + e.Message };
            }
        }

        private void RestoreRegistration()
        {
            if (SkipRegistration) { return; }
            try
            {
                string? oldExe = null;
                if (_state is not null && File.Exists(Path.Combine(AppPaths.App, AppPaths.AppExeName))) { oldExe = AppPaths.AppExe; }
                if (oldExe is null && _legacy is not null && File.Exists(_legacy.AppExe)) { oldExe = _legacy.AppExe; }

                if (oldExe is not null)
                {
                    RegistrationManager.Apply(oldExe, _request.Registration);
                    if (_state is not null) { ArpRegistration.Write(_currentVersion, oldExe, oldExe); }
                    _log.Write("Restored the previous native-messaging registration.");
                }
                else
                {
                    RegistrationManager.Remove(null);
                    ArpRegistration.Remove();
                }
            }
            catch (Exception e)
            {
                _log.Error("Could not restore registration: " + e.Message);
            }
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
                    Arguments = "--selftest",
                    WorkingDirectory = AppPaths.App,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

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

        private void CleanupLegacy()
        {
            if (_legacy is null) { return; }

            try
            {
                string legacyBin = Path.GetDirectoryName(_legacy.AppExe) ?? "";
                if (Directory.Exists(legacyBin)) { Directory.Delete(legacyBin, recursive: true); }
                if (File.Exists(AppPaths.LegacyLock)) { File.Delete(AppPaths.LegacyLock); }
                _log.Write("Removed the legacy bin folder; legacy data was left in place as a backup.");
            }
            catch (Exception e)
            {
                _log.Error("Could not fully remove the legacy files: " + e.Message);
            }
        }

        // Tries the control pipe first, then force-close.
        private bool StopHost(string exePath, string version, bool allowPipe)
        {
            string message = $"VRPC {version} is still running from {exePath}.";
            if (!ProcessHelper.IsRunning(exePath)) { return true; }

            if (allowPipe && OperatingSystem.IsWindows())
            {
                _log.Write("Requesting shutdown through the control pipe.");
                if (ControlClient.TryShutdown(TimeSpan.FromSeconds(3)) && ProcessHelper.WaitForExit(exePath, TimeSpan.FromSeconds(10)))
                {
                    return true;
                }
            }

            if (!_request.Force && !_presenter.ConfirmForceClose(message))
            {
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
        private InstallResult RunUninstall()
        {
            _state = InstallStateStore.Load();
            _legacy = LegacyInstallation.Detect();
            _currentVersion = GetCurrentVersion();
            bool existing = _state is not null || _legacy is not null || File.Exists(AppPaths.AppExe);
            if (!existing) { return Failure(SetupExitCodes.Usage, "VRPC is not installed."); }

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
                ForceCloseRequired = ProcessHelper.IsRunning(AppPaths.AppExe)
            };

            if (!_presenter.Confirm(plan)) { return Failure(SetupExitCodes.Canceled, "Uninstallation canceled."); }

            if (!StopHost(AppPaths.AppExe, _currentVersion, allowPipe: true))
            {
                return Failure(SetupExitCodes.Running, "VRPC is still running. Close it and retry.");
            }

            if (_legacy is not null && !StopHost(_legacy.AppExe, _legacy.Version, allowPipe: false))
            {
                return Failure(SetupExitCodes.Running, "The legacy VRPC is still running.");
            }

            if (!SkipRegistration)
            {
                RegistrationManager.Remove(_state);
                RegistrationManager.Remove(null);
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
                return Failure(SetupExitCodes.Failed, "The application files could not be deleted. Close the browser and retry. " + e.Message);
            }

            if (_request.PurgeData)
            {
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

            if (!SkipRegistration)
            {
                ArpRegistration.Remove();
                ArpRegistration.RemoveLegacy();
                RegistrationManager.Remove(null);
            }

            InstallResult result = new()
            {
                Success = true,
                ExitCode = SetupExitCodes.Success,
                Message = _request.PurgeData
                    ? "VRPC was uninstalled and its data was removed."
                    : "VRPC was uninstalled. Your settings and listening history were kept.",
                Version = _currentVersion,
                LogPath = _log.Path
            };
            _presenter.Complete(result);
            return result;
        }

        // Restores app.old if a previous update died mid-swap.
        private void RecoverInterruptedTransaction()
        {
            UpdateTransaction? transaction = InstallStateStore.LoadTransaction();
            if (transaction is null) { return; }

            _log.Write($"Recovering an interrupted update (phase {transaction.Phase}).");
            try
            {
                string staging = transaction.StagingDir;
                string backup = transaction.BackupDir;
                if (AppPaths.IsUnderRoot(staging) && Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
                if (AppPaths.IsUnderRoot(backup) && Directory.Exists(backup) && !Directory.Exists(AppPaths.App))
                {
                    Directory.Move(backup, AppPaths.App);
                    _log.Write("Restored the previous app folder.");
                }
            }
            catch (Exception e)
            {
                _log.Error("Recovery failed: " + e.Message);
            }
            finally
            {
                InstallStateStore.DeleteTransaction();
            }
        }

        // Logs and presents a failure result.
        private InstallResult Failure(int exitCode, string message)
        {
            _log.Error(message);
            InstallResult result = new()
            {
                Success = false,
                ExitCode = exitCode,
                Message = message,
                Version = _payload?.Version ?? "",
                LogPath = _log.Path
            };
            _presenter.Fail(result);
            return result;
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } } catch { }
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
