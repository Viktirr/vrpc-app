using System.Security.Cryptography;
using System.Text.Json;
using VRPC.Application;

namespace VRPC.Updates
{
    // Single-flight update state machine: throttled checks, verified download,
    // safe extraction, and handoff to the external setup panel on approval.
    public sealed class UpdateCoordinator : IDisposable
    {
        private const int InitialDelaySeconds = 45;
        private const int ManualPollSeconds = 5;
        private const int ReadyRetentionDays = 7;
        private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan ManualRateLimit = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan[] FailureBackoff =
        {
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(24)
        };

        private readonly string _currentVersion;
        private readonly string _channel;
        private readonly IUpdatePackageSource _source;
        private readonly IUpdateProcessLauncher _launcher;
        private readonly UpdateStateStore _store;
        private readonly Action<string> _warn;
        private readonly Action<string>? _info;
        private readonly Action? _onLaunched;
        private readonly TimeSpan _initialDelay;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();

        private UpdaterState _state;
        private Task? _loop;
        private bool _started;
        private bool _disposed;
        private bool _manualRequested;
        private string _phase = UpdatePhase.Idle;
        private int _progressPercent;
        private DateTime _lastManualCheckUtc = DateTime.MinValue;

        public UpdateCoordinator(
            string currentVersion,
            string channel,
            IUpdatePackageSource source,
            IUpdateProcessLauncher launcher,
            UpdateStateStore store,
            Action<string> warn,
            Action<string>? info = null,
            Action? onLaunched = null,
            TimeSpan? initialDelay = null)
        {
            _currentVersion = currentVersion;
            _channel = channel;
            _source = source;
            _launcher = launcher;
            _store = store;
            _warn = warn;
            _info = info;
            _onLaunched = onLaunched;
            _initialDelay = initialDelay ?? TimeSpan.FromSeconds(InitialDelaySeconds);
            _state = _store.Load();
            if (string.IsNullOrEmpty(_state.Channel)) { _state.Channel = channel; }
            if (!string.IsNullOrEmpty(_state.ReadyVersion) && IsStrictlyNewerThanCurrent(_state.ReadyVersion))
            {
                _phase = UpdatePhase.Ready;
            }
            else if (_state.ConsecutiveFailures > 0)
            {
                _phase = UpdatePhase.Failed;
            }
            else
            {
                _phase = UpdatePhase.Idle;
            }
        }

        public string CurrentVersion => _currentVersion;

        public void Start(CancellationToken shutdownToken)
        {
            lock (_gate)
            {
                if (_started || _disposed) { return; }
                _started = true;
            }

            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, shutdownToken);
            _loop = Task.Run(() => LoopAsync(linked.Token), CancellationToken.None);
        }

        public UpdateStatusSnapshot GetStatus()
        {
            lock (_gate)
            {
                UpdateStatusSnapshot status = new UpdateStatusSnapshot
                {
                    Phase = _phase,
                    CurrentVersion = _currentVersion,
                    AvailableVersion = _state.AvailableVersion,
                    NotesUrl = _state.AvailableNotesUrl,
                    LastCheckedUtc = _state.LastCheckUtc,
                    LastErrorCategory = _state.LastErrorCategory,
                    LastErrorMessage = _state.LastErrorMessage,
                    ProgressPercent = _progressPercent
                };

                if (string.IsNullOrEmpty(status.AvailableVersion) && !string.IsNullOrEmpty(_state.ReadyVersion))
                {
                    status.AvailableVersion = _state.ReadyVersion;
                }
                return status;
            }
        }

        public bool TryRequestManualCheck(out string reply)
        {
            lock (_gate)
            {
                if (_disposed) { reply = "failed"; return false; }
                if (_phase == UpdatePhase.Checking || _phase == UpdatePhase.Downloading || _phase == UpdatePhase.Installing)
                {
                    reply = "rate_limited";
                    return false;
                }

                DateTime now = DateTime.UtcNow;
                if (now - _lastManualCheckUtc < ManualRateLimit)
                {
                    reply = "rate_limited";
                    return false;
                }

                _lastManualCheckUtc = now;
                _manualRequested = true;
                reply = "accepted";
                return true;
            }
        }

        public bool TryRequestInstall(out string reply)
        {
            string packageDirectory;
            string version;
            lock (_gate)
            {
                if (_disposed ||
                    _phase == UpdatePhase.Checking ||
                    _phase == UpdatePhase.Downloading ||
                    _phase == UpdatePhase.Installing ||
                    !HasUsableReadyLocked())
                {
                    reply = "not_ready";
                    return false;
                }

                packageDirectory = _state.ReadyPackageDir;
                version = _state.ReadyVersion;
                if (string.IsNullOrEmpty(packageDirectory) || string.IsNullOrEmpty(version) ||
                    !Directory.Exists(packageDirectory) || !AppPaths.IsUnderUpdatesRoot(packageDirectory))
                {
                    _state.LastErrorCategory = UpdateErrorCategory.Internal;
                    _state.LastErrorMessage = "The downloaded update package is no longer available.";
                    _state.LastErrorUtc = DateTime.UtcNow.ToString("O");
                    _phase = UpdatePhase.Failed;
                    SaveLocked();
                    reply = "not_ready";
                    return false;
                }

                _phase = UpdatePhase.Installing;
                SaveLocked();
            }

            if (!VerifyReadyPackage(packageDirectory, version, out string errorCategory, out string errorMessage))
            {
                lock (_gate)
                {
                    _phase = UpdatePhase.Failed;
                    _state.LastErrorCategory = errorCategory;
                    _state.LastErrorMessage = errorMessage;
                    _state.LastErrorUtc = DateTime.UtcNow.ToString("O");
                    SaveLocked();
                }
                reply = "failed";
                return false;
            }

            if (!_launcher.TryLaunch(packageDirectory, version, out errorMessage))
            {
                lock (_gate)
                {
                    _phase = UpdatePhase.Failed;
                    _state.LastErrorCategory = UpdateErrorCategory.Internal;
                    _state.LastErrorMessage = errorMessage;
                    _state.LastErrorUtc = DateTime.UtcNow.ToString("O");
                    SaveLocked();
                }
                reply = "failed";
                return false;
            }

            lock (_gate)
            {
                _state.InstalledTargetVersion = version;
                _state.LastLaunchUtc = DateTime.UtcNow.ToString("O");
                SaveLocked();
            }

            reply = "installing";
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    _onLaunched?.Invoke();
                }
                catch { }
            });
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) { return; }
                _disposed = true;
            }

            try { _lifetime.Cancel(); } catch { }
            try { _lifetime.Dispose(); } catch { }
        }

        private async Task LoopAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(_initialDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The host that starts right after a successful update still sees the
            // previous ready/download records; they describe the version that is
            // now installed and must not be offered again.
            ReconcileInstalledState();
            CleanupAbandonedOperations();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await WaitForNextAttemptAsync(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) { return; }
                    await RunCheckAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _warn($"[Updater] The update loop hit an unexpected error and will retry later: {exception.Message}");
                    RecordFailure(UpdateErrorCategory.Internal, "The update check failed unexpectedly.");
                }
            }
        }

        // Sleeps in short slices so a manual "check now" can wake the loop
        // without leaking a pending wait per iteration.
        private async Task WaitForNextAttemptAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                bool manual;
                DateTime next;
                lock (_gate)
                {
                    manual = _manualRequested;
                    _manualRequested = false;
                    next = NextAttemptUtcLocked();
                }

                if (manual || DateTime.UtcNow >= next) { return; }
                await Task.Delay(TimeSpan.FromSeconds(ManualPollSeconds), token).ConfigureAwait(false);
            }
        }

        private void ReconcileInstalledState()
        {
            lock (_gate)
            {
                bool changed = false;
                if (!string.IsNullOrEmpty(_state.ReadyVersion) && !IsStrictlyNewerThanCurrent(_state.ReadyVersion))
                {
                    RemoveReadyLocked();
                    _phase = UpdatePhase.Idle;
                    changed = true;
                }
                if (!string.IsNullOrEmpty(_state.InstalledTargetVersion) &&
                    StrictVersion.IsValid(_state.InstalledTargetVersion) &&
                    StrictVersion.IsValid(_currentVersion) &&
                    StrictVersion.Compare(_state.InstalledTargetVersion, _currentVersion) <= 0)
                {
                    _state.InstalledTargetVersion = "";
                    changed = true;
                }
                if (!string.IsNullOrEmpty(_state.AvailableVersion) && !IsStrictlyNewerThanCurrent(_state.AvailableVersion))
                {
                    _state.AvailableVersion = "";
                    _state.AvailableNotesUrl = "";
                    changed = true;
                }
                if (changed) { SaveLocked(); }
            }
        }

        private bool IsStrictlyNewerThanCurrent(string version)
        {
            return StrictVersion.IsValid(version) && StrictVersion.IsValid(_currentVersion) &&
                   StrictVersion.Compare(version, _currentVersion) > 0;
        }

        // A verified, downloaded package stays installable even if a later check
        // fails; a network blip must not hide an update that is already ready.
        private bool HasUsableReadyLocked()
        {
            return !string.IsNullOrEmpty(_state.ReadyVersion) &&
                   IsStrictlyNewerThanCurrent(_state.ReadyVersion) &&
                   !string.IsNullOrEmpty(_state.ReadyPackageDir) &&
                   Directory.Exists(_state.ReadyPackageDir) &&
                   AppPaths.IsUnderUpdatesRoot(_state.ReadyPackageDir);
        }

        private async Task RunCheckAsync(CancellationToken token)
        {
            string? etag;
            lock (_gate)
            {
                if (_phase == UpdatePhase.Checking || _phase == UpdatePhase.Downloading || _phase == UpdatePhase.Installing) { return; }
                _phase = UpdatePhase.Checking;
                _state.LastAttemptUtc = DateTime.UtcNow.ToString("O");
                etag = string.IsNullOrEmpty(_state.ManifestEtag) ? null : _state.ManifestEtag;
                SaveLocked();
            }

            UpdateCheckResult result;
            try
            {
                result = await _source.CheckAsync(etag, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate) { _phase = UpdatePhase.Idle; }
                throw;
            }
            catch (Exception exception)
            {
                _warn($"[Updater] The update feed could not be checked: {exception.Message}");
                RecordFailure(UpdateErrorCategory.Network, "The update feed could not be reached.");
                return;
            }

            if (token.IsCancellationRequested) { return; }

            switch (result.Outcome)
            {
                case UpdateCheckOutcome.Failed:
                    _warn($"[Updater] Update check failed ({result.ErrorCategory}): {result.ErrorMessage}");
                    RecordFailure(string.IsNullOrEmpty(result.ErrorCategory) ? UpdateErrorCategory.Internal : result.ErrorCategory, result.ErrorMessage);
                    return;

                case UpdateCheckOutcome.NotModified:
                case UpdateCheckOutcome.UpToDate:
                    HandleNoUpdate(result);
                    return;

                case UpdateCheckOutcome.UpdateAvailable:
                    if (result.Manifest is null)
                    {
                        RecordFailure(UpdateErrorCategory.InvalidManifest, "The update feed returned no manifest.");
                        return;
                    }
                    await HandleUpdateAvailableAsync(result, token).ConfigureAwait(false);
                    return;
            }
        }

        private void HandleNoUpdate(UpdateCheckResult result)
        {
            lock (_gate)
            {
                _state.ConsecutiveFailures = 0;
                _state.LastErrorCategory = "";
                _state.LastErrorMessage = "";
                _state.LastCheckUtc = DateTime.UtcNow.ToString("O");
                _state.NextAllowedAttemptUtc = DateTime.UtcNow.Add(AutomaticInterval).ToString("O");
                AppendEtagLocked(result.ETag);

                if (!string.IsNullOrEmpty(_state.ReadyVersion) &&
                    StrictVersion.IsValid(_state.ReadyVersion) && StrictVersion.IsValid(_currentVersion) &&
                    StrictVersion.Compare(_state.ReadyVersion, _currentVersion) <= 0)
                {
                    RemoveReadyLocked();
                }

                _state.AvailableVersion = "";
                _state.AvailableNotesUrl = "";
                _phase = string.IsNullOrEmpty(_state.ReadyVersion) ? UpdatePhase.Idle : UpdatePhase.Ready;
                SaveLocked();
            }
        }

        private async Task HandleUpdateAvailableAsync(UpdateCheckResult result, CancellationToken token)
        {
            UpdateManifest manifest = result.Manifest!;
            string version = manifest.Payload.Version;

            if (!StrictVersion.IsValid(version) || !StrictVersion.IsValid(_currentVersion) ||
                StrictVersion.Compare(version, _currentVersion) <= 0)
            {
                HandleNoUpdate(result);
                return;
            }

            bool alreadyReady;
            lock (_gate)
            {
                alreadyReady = !string.IsNullOrEmpty(_state.ReadyVersion) &&
                               string.Equals(_state.ReadyVersion, version, StringComparison.Ordinal) &&
                               !string.IsNullOrEmpty(_state.ReadyPackageDir) &&
                               Directory.Exists(_state.ReadyPackageDir);
                if (alreadyReady)
                {
                    _state.AvailableVersion = version;
                    _state.AvailableNotesUrl = manifest.Payload.NotesUrl;
                    _state.LastCheckUtc = DateTime.UtcNow.ToString("O");
                    _state.NextAllowedAttemptUtc = DateTime.UtcNow.Add(AutomaticInterval).ToString("O");
                    AppendEtagLocked(result.ETag);
                    _state.ConsecutiveFailures = 0;
                    _phase = UpdatePhase.Ready;
                    SaveLocked();
                }
            }
            if (alreadyReady) { return; }

            string operationDirectory;
            lock (_gate)
            {
                _phase = UpdatePhase.Downloading;
                _progressPercent = 0;
                _state.AvailableVersion = version;
                _state.AvailableNotesUrl = manifest.Payload.NotesUrl;
                _state.LastCheckUtc = DateTime.UtcNow.ToString("O");
                AppendEtagLocked(result.ETag);
                SaveLocked();
            }

            try
            {
                Directory.CreateDirectory(AppPaths.UpdatesDir);
                operationDirectory = System.IO.Path.Combine(AppPaths.UpdatesDir, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(operationDirectory);
            }
            catch (Exception exception)
            {
                RecordFailure(UpdateErrorCategory.Disk, "The update folder could not be created: " + exception.Message);
                return;
            }

            string archivePath = System.IO.Path.Combine(operationDirectory, "package.zip");
            string packageDirectory = System.IO.Path.Combine(operationDirectory, "package");

            try
            {
                if (!HasEnoughFreeSpace(manifest.Payload.Asset.Size))
                {
                    RecordFailure(UpdateErrorCategory.Disk, "Not enough free disk space for the update.");
                    TryDeleteDirectory(operationDirectory);
                    return;
                }

                string envelopePath = System.IO.Path.Combine(operationDirectory, "manifest.json");
                File.WriteAllText(envelopePath, UpdateManifestJson.SerializeEnvelope(manifest));

                UpdateOperationResult download = await _source.DownloadAsync(manifest.Payload.Asset, archivePath, token).ConfigureAwait(false);
                if (!download.Success)
                {
                    TryDeleteDirectory(operationDirectory);
                    RecordFailure(download.ErrorCategory, download.ErrorMessage);
                    return;
                }

                _progressPercent = 60;
                UpdateOperationResult extraction = await _source.ExtractAsync(archivePath, packageDirectory, version, token).ConfigureAwait(false);
                if (!extraction.Success)
                {
                    TryDeleteDirectory(operationDirectory);
                    RecordFailure(extraction.ErrorCategory, extraction.ErrorMessage);
                    return;
                }

                if (!_source.VerifyPackageDirectory(packageDirectory, version, out string errorCategory, out string errorMessage))
                {
                    TryDeleteDirectory(operationDirectory);
                    RecordFailure(errorCategory, errorMessage);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                TryDeleteDirectory(operationDirectory);
                throw;
            }
            catch (Exception exception)
            {
                TryDeleteDirectory(operationDirectory);
                RecordFailure(UpdateErrorCategory.Internal, "The update could not be prepared: " + exception.Message);
                return;
            }

            lock (_gate)
            {
                RemoveReadyLocked();
                _state.ReadyVersion = version;
                _state.ReadyPackageDir = packageDirectory;
                _state.LastNotifiedVersion = version;
                _state.ConsecutiveFailures = 0;
                _state.LastErrorCategory = "";
                _state.LastErrorMessage = "";
                _state.LastErrorUtc = "";
                _state.NextAllowedAttemptUtc = DateTime.UtcNow.Add(AutomaticInterval).ToString("O");
                _progressPercent = 100;
                _phase = UpdatePhase.Ready;
                SaveLocked();
            }

            _info?.Invoke($"[Updater] Update {version} is downloaded and verified; waiting for user approval.");
        }

        private bool VerifyReadyPackage(string packageDirectory, string version, out string errorCategory, out string errorMessage)
        {
            errorCategory = UpdateErrorCategory.Internal;
            errorMessage = "";
            try
            {
                string operationDirectory = System.IO.Path.GetDirectoryName(packageDirectory) ?? "";
                string envelopePath = System.IO.Path.Combine(operationDirectory, "manifest.json");
                if (!File.Exists(envelopePath))
                {
                    errorMessage = "The saved update manifest is missing.";
                    return false;
                }

                UpdateManifest? manifest = UpdateManifestJson.Deserialize(File.ReadAllText(envelopePath));
                if (manifest is null || !_source.VerifySavedManifest(manifest, out errorCategory, out errorMessage))
                {
                    errorMessage = string.IsNullOrEmpty(errorMessage) ? "The saved update manifest did not verify." : errorMessage;
                    return false;
                }

                if (!string.Equals(manifest.Payload.Version, version, StringComparison.Ordinal) ||
                    !StrictVersion.IsStrictlyNewer(manifest.Payload.Version, _currentVersion))
                {
                    errorCategory = UpdateErrorCategory.PayloadMismatch;
                    errorMessage = "The downloaded update version is no longer newer than the installed one.";
                    return false;
                }

                // The extracted files re-verify against payload.json and the saved
                // envelope re-authenticates; the archive is the anchor between
                // them, so its bytes must still match the signed hash.
                string archivePath = System.IO.Path.Combine(operationDirectory, "package.zip");
                if (!File.Exists(archivePath))
                {
                    errorCategory = UpdateErrorCategory.Internal;
                    errorMessage = "The downloaded update archive is missing.";
                    return false;
                }
                if (!ArchiveMatchesSignedAsset(archivePath, manifest.Payload.Asset))
                {
                    errorCategory = UpdateErrorCategory.HashMismatch;
                    errorMessage = "The downloaded update archive no longer matches the signed hash.";
                    return false;
                }

                return _source.VerifyPackageDirectory(packageDirectory, version, out errorCategory, out errorMessage);
            }
            catch (Exception exception)
            {
                errorMessage = "The update could not be re-verified: " + exception.Message;
                return false;
            }
        }

        private static bool ArchiveMatchesSignedAsset(string archivePath, UpdateAsset asset)
        {
            try
            {
                using FileStream stream = File.OpenRead(archivePath);
                if (stream.Length != asset.Size)
                {
                    return false;
                }
                return string.Equals(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), asset.Sha256, StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void RecordFailure(string errorCategory, string errorMessage)
        {
            lock (_gate)
            {
                _state.ConsecutiveFailures++;
                DateTime now = DateTime.UtcNow;
                _state.LastErrorCategory = errorCategory;
                _state.LastErrorMessage = errorMessage;
                _state.LastErrorUtc = now.ToString("O");
                _state.LastCheckUtc = now.ToString("O");
                _state.NextAllowedAttemptUtc = now.Add(BackoffFor(_state.ConsecutiveFailures)).ToString("O");
                _progressPercent = 0;
                _phase = HasUsableReadyLocked() ? UpdatePhase.Ready : UpdatePhase.Failed;
                SaveLocked();
            }
        }

        private static TimeSpan BackoffFor(int failures)
        {
            if (failures <= 0) { return FailureBackoff[0]; }
            int index = Math.Min(failures - 1, FailureBackoff.Length - 1);
            return FailureBackoff[index];
        }

        private DateTime NextAttemptUtcLocked()
        {
            if (DateTime.TryParse(_state.NextAllowedAttemptUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime next))
            {
                return next.ToUniversalTime();
            }
            return DateTime.UtcNow;
        }

        private void AppendEtagLocked(string etag)
        {
            if (!string.IsNullOrEmpty(etag)) { _state.ManifestEtag = etag; }
        }

        private void RemoveReadyLocked()
        {
            if (!string.IsNullOrEmpty(_state.ReadyPackageDir))
            {
                string operationDirectory = System.IO.Path.GetDirectoryName(_state.ReadyPackageDir) ?? "";
                TryDeleteDirectory(operationDirectory);
            }
            _state.ReadyVersion = "";
            _state.ReadyPackageDir = "";
        }

        private void CleanupAbandonedOperations()
        {
            try
            {
                if (!Directory.Exists(AppPaths.UpdatesDir)) { return; }
                DateTime cutoff = DateTime.UtcNow.AddDays(-ReadyRetentionDays);
                string? readyOperationDirectory = null;
                lock (_gate)
                {
                    if (!string.IsNullOrEmpty(_state.ReadyPackageDir))
                    {
                        readyOperationDirectory = System.IO.Path.GetDirectoryName(_state.ReadyPackageDir);
                    }
                }

                foreach (string directory in Directory.GetDirectories(AppPaths.UpdatesDir))
                {
                    if (!AppPaths.IsUnderUpdatesRoot(directory)) { continue; }
                    if (!string.IsNullOrEmpty(readyOperationDirectory) &&
                        string.Equals(System.IO.Path.GetFullPath(directory).TrimEnd('\\'), System.IO.Path.GetFullPath(readyOperationDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        TryDeleteDirectory(directory);
                    }
                }
            }
            catch (Exception exception)
            {
                _warn($"[Updater] Old update files could not be cleaned up: {exception.Message}");
            }
        }

        private static bool HasEnoughFreeSpace(long archiveSize)
        {
            try
            {
                string root = AppPaths.Root;
                string drive = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(root)) ?? "";
                if (string.IsNullOrEmpty(drive)) { return true; }
                DriveInfo info = new DriveInfo(drive);
                if (!info.IsReady) { return true; }
                long required = archiveSize * 4;
                return info.AvailableFreeSpace >= required;
            }
            catch
            {
                return true;
            }
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { return; }
                if (!AppPaths.IsUnderUpdatesRoot(directory)) { return; }
                Directory.Delete(directory, recursive: true);
            }
            catch { }
        }

        private void SaveLocked()
        {
            try { _store.Save(_state); }
            catch (Exception exception) { _warn($"[Updater] Updater state could not be saved: {exception.Message}"); }
        }
    }
}
