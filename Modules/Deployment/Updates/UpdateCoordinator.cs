using System.Security.Cryptography;
using System.Text.Json;
using VRPC.Application;
using VRPC.Deployment.Install;

namespace VRPC.Deployment.Updates
{
    // Single-flight update state machine: throttled checks, verified download,
    // safe extraction, and handoff to the external setup panel on approval.
    public sealed class UpdateCoordinator : IDisposable
    {
        private const int InitialDelaySeconds = 45;
        private const int ReadyRetentionDays = 7;
        private const int MaxUnattendedAttempts = 3;
        private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan ManualRateLimit = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan UnattendedRetryDelay = TimeSpan.FromMinutes(10);
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
        private readonly Func<bool>? _unattendedEnabled;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        // Manual checks wake this instead of the loop polling: the initial 45 s
        // delay and a 24 h backoff are both interruptible.
        private readonly SemaphoreSlim _wake = new(0);

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
            TimeSpan? initialDelay = null,
            Func<bool>? unattendedEnabled = null)
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
            _unattendedEnabled = unattendedEnabled;
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
                try { _wake.Release(); } catch (SemaphoreFullException) { }
                return true;
            }
        }

        public bool TryRequestInstall(out string reply)
        {
            return TryRequestInstall(unattended: false, out reply);
        }

        // The popup/page can turn the setting on while a package is already
        // ready. Kick the same auto-install path without waiting for the next
        // scheduled check.
        public void RequestAutoInstall()
        {
            if (_unattendedEnabled?.Invoke() != true) { return; }
            // Wake the loop so its next-attempt calculation picks up the
            // unattended retry deadline after a failed immediate attempt.
            try { _wake.Release(); } catch (SemaphoreFullException) { }
            _ = Task.Run(AutoInstallIfReady);
        }

        private bool TryRequestInstall(bool unattended, out string reply)
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

                if (SetupLifecycle.BlocksHostStartup(out string setupReason))
                {
                    _warn("[Updater] The update cannot start while setup is active: " + setupReason);
                    reply = "not_ready";
                    return false;
                }

                if (unattended && !CanAutoInstallLocked(out string autoReason))
                {
                    _info?.Invoke("[Updater] Automatic installation is waiting: " + autoReason);
                    reply = "not_ready";
                    return false;
                }

                packageDirectory = _state.ReadyPackageDir;
                version = _state.ReadyVersion;
                if (string.IsNullOrEmpty(packageDirectory) || string.IsNullOrEmpty(version) ||
                    !Directory.Exists(packageDirectory) || !AppPaths.IsUnderUpdatesRoot(packageDirectory))
                {
                    RemoveReadyLocked();
                    _state.LastErrorCategory = UpdateErrorCategory.Internal;
                    _state.LastErrorMessage = "The downloaded update package is no longer available.";
                    _state.LastErrorUtc = DateTime.UtcNow.ToString("O");
                    _phase = UpdatePhase.Failed;
                    SaveLocked();
                    reply = "not_ready";
                    return false;
                }

                if (unattended) { RecordAutoInstallAttemptLocked(version); }

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

            if (!_launcher.TryLaunch(packageDirectory, version, unattended, out errorMessage))
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
                // The first wait is as interruptible as every later one: a manual
                // check right after startup must not wait 45 seconds.
                await WaitForNextAttemptAsync(token, _initialDelay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) { return; }

            // The host that starts right after a successful update still sees the
            // previous ready/download records; they describe the version that is
            // now installed and must not be offered again.
            ReconcileInstalledState();
            CleanupAbandonedOperations();
            AutoInstallIfReady();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await WaitForNextAttemptAsync(token, null).ConfigureAwait(false);
                    if (token.IsCancellationRequested) { return; }
                    await RunCheckAsync(token).ConfigureAwait(false);
                    AutoInstallIfReady();
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

        // Waits for the scheduled attempt, a manual request, or cancellation.
        // The semaphore is the wake-up edge; there is no polling loop.
        private async Task WaitForNextAttemptAsync(CancellationToken token, TimeSpan? initialDelay)
        {
            if (initialDelay.HasValue)
            {
                bool signaled = false;
                if (initialDelay.Value > TimeSpan.Zero)
                {
                    signaled = await _wake.WaitAsync(initialDelay.Value, token).ConfigureAwait(false);
                }
                // A signal keeps _manualRequested set so the loop starts a check
                // immediately instead of waiting for the next scheduled attempt.
                if (!signaled) { ClearManualRequest(); }
                return;
            }

            while (!token.IsCancellationRequested)
            {
                bool manual;
                DateTime next;
                lock (_gate)
                {
                    manual = _manualRequested;
                    next = NextAttemptUtcLocked();
                }

                if (manual) { ClearManualRequest(); DrainWake(); return; }

                TimeSpan delay = next - DateTime.UtcNow;
                if (delay <= TimeSpan.Zero) { return; }

                if (await _wake.WaitAsync(delay, token).ConfigureAwait(false))
                {
                    ClearManualRequest();
                    return;
                }
            }
        }

        private void ClearManualRequest()
        {
            lock (_gate) { _manualRequested = false; }
        }

        // Opt-in Phase 2: only runs when the user enabled the setting and a
        // verified package is ready. It uses the same install path as the
        // popup, but launches setup with --quiet --yes instead of the panel.
        private void AutoInstallIfReady()
        {
            if (_unattendedEnabled?.Invoke() != true) { return; }

            string version;
            lock (_gate)
            {
                if (_disposed ||
                    _phase == UpdatePhase.Checking ||
                    _phase == UpdatePhase.Downloading ||
                    _phase == UpdatePhase.Installing ||
                    !HasUsableReadyLocked())
                {
                    return;
                }
                if (!CanAutoInstallLocked(out _)) { return; }
                version = _state.ReadyVersion;
            }

            if (TryRequestInstall(unattended: true, out string reply) && reply == "installing")
            {
                _info?.Invoke($"[Updater] Installing {version} automatically.");
            }
        }

        private bool CanAutoInstallLocked(out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(_state.ReadyVersion))
            {
                reason = "no update is ready";
                return false;
            }

            if (!string.IsNullOrEmpty(_state.UnattendedVersion) &&
                string.Equals(_state.UnattendedVersion, _state.ReadyVersion, StringComparison.Ordinal) &&
                _state.UnattendedAttempts >= MaxUnattendedAttempts)
            {
                reason = $"the automatic install limit was reached for {_state.ReadyVersion}";
                return false;
            }

            if (DateTime.TryParse(_state.LastUnattendedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last) &&
                DateTime.UtcNow - last.ToUniversalTime() < UnattendedRetryDelay)
            {
                reason = "the previous automatic attempt was recent";
                return false;
            }

            return true;
        }

        private void RecordAutoInstallAttemptLocked(string version)
        {
            if (!string.Equals(_state.UnattendedVersion, version, StringComparison.Ordinal))
            {
                _state.UnattendedVersion = version;
                _state.UnattendedAttempts = 0;
            }

            _state.UnattendedAttempts++;
            _state.LastUnattendedUtc = DateTime.UtcNow.ToString("O");
        }

        // A manual request both sets the flag and releases the semaphore. When the
        // flag already caused the wake, drop the now-redundant permit so it cannot
        // trigger a second check immediately after the first.
        private void DrainWake()
        {
            while (_wake.Wait(0)) { }
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
                else if (!string.IsNullOrEmpty(_state.ReadyVersion) && !HasUsableReadyLocked())
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
                if (!string.IsNullOrEmpty(_state.UnattendedVersion) && !IsStrictlyNewerThanCurrent(_state.UnattendedVersion))
                {
                    _state.UnattendedVersion = "";
                    _state.UnattendedAttempts = 0;
                    _state.LastUnattendedUtc = "";
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

        // An advertised update that has no usable ready package (or is newer than
        // the ready one) has not finished preparation. A 304 must never clear it.
        private bool HasPendingPreparationLocked()
        {
            if (!string.IsNullOrEmpty(_state.AvailableVersion) && IsStrictlyNewerThanCurrent(_state.AvailableVersion))
            {
                if (!HasUsableReadyLocked()) { return true; }
                if (StrictVersion.IsValid(_state.ReadyVersion) &&
                    StrictVersion.Compare(_state.AvailableVersion, _state.ReadyVersion) > 0)
                {
                    return true;
                }
            }

            // A newer ready record whose package directory disappeared is also
            // unfinished: a 304 must not freeze that broken state.
            return !string.IsNullOrEmpty(_state.ReadyVersion) &&
                   IsStrictlyNewerThanCurrent(_state.ReadyVersion) &&
                   !HasUsableReadyLocked();
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
                result = await CheckWithEtagRecoveryAsync(etag, token).ConfigureAwait(false);
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

        // A 304 is only meaningful when the saved ETag describes a package that
        // actually reached `ready`. Otherwise the cached validator is dropped and
        // the manifest is fetched once more; a second 304 falls through to the
        // bounded-backoff failure path in HandleNoUpdate.
        private async Task<UpdateCheckResult> CheckWithEtagRecoveryAsync(string? etag, CancellationToken token)
        {
            UpdateCheckResult result = await _source.CheckAsync(etag, token).ConfigureAwait(false);
            if (result.Outcome != UpdateCheckOutcome.NotModified) { return result; }

            bool pending;
            lock (_gate)
            {
                pending = HasPendingPreparationLocked();
                if (pending)
                {
                    _state.ManifestEtag = "";
                    _state.CachedManifestVersion = "";
                    SaveLocked();
                }
            }

            if (!pending) { return result; }

            _warn("[Updater] The saved feed ETag hid an update that was never prepared; fetching the full manifest again.");
            token.ThrowIfCancellationRequested();
            return await _source.CheckAsync(null, token).ConfigureAwait(false);
        }

        private void HandleNoUpdate(UpdateCheckResult result)
        {
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;

                if (result.Outcome == UpdateCheckOutcome.NotModified && HasPendingPreparationLocked())
                {
                    _state.ManifestEtag = "";
                    _state.CachedManifestVersion = "";
                    _state.ConsecutiveFailures++;
                    _state.LastErrorCategory = UpdateErrorCategory.InvalidManifest;
                    _state.LastErrorMessage = "The update feed reported no change, but the update package is not ready. The full manifest will be retried later.";
                    _state.LastErrorUtc = now.ToString("O");
                    _state.LastCheckUtc = now.ToString("O");
                    _state.NextAllowedAttemptUtc = now.Add(BackoffFor(_state.ConsecutiveFailures)).ToString("O");
                    _phase = HasUsableReadyLocked() ? UpdatePhase.Ready : UpdatePhase.Failed;
                    SaveLocked();
                    return;
                }

                bool validResponse = result.Outcome != UpdateCheckOutcome.NotModified && result.Outcome != UpdateCheckOutcome.Failed;

                _state.ConsecutiveFailures = 0;
                _state.LastErrorCategory = "";
                _state.LastErrorMessage = "";
                _state.LastCheckUtc = now.ToString("O");
                _state.NextAllowedAttemptUtc = now.Add(AutomaticInterval).ToString("O");

                if (validResponse)
                {
                    AppendEtagLocked(result.ETag);
                    string offeredVersion = result.Manifest?.Payload.Version ?? "";
                    if (!string.IsNullOrWhiteSpace(offeredVersion)) { _state.CachedManifestVersion = offeredVersion; }
                }

                if (!string.IsNullOrEmpty(_state.ReadyVersion) &&
                    StrictVersion.IsValid(_state.ReadyVersion) && StrictVersion.IsValid(_currentVersion))
                {
                    bool remove = StrictVersion.Compare(_state.ReadyVersion, _currentVersion) <= 0;
                    if (!remove && !HasUsableReadyLocked()) { remove = true; }

                    // Withdrawal policy: a valid (non-304) feed response is
                    // authoritative. A ready package it no longer offers is
                    // revoked. Network failures and 304s keep it installable.
                    string offered = result.Manifest?.Payload.Version ?? "";
                    if (!remove && validResponse && StrictVersion.IsValid(offered) &&
                        StrictVersion.Compare(_state.ReadyVersion, offered) > 0)
                    {
                        remove = true;
                        _info?.Invoke($"[Updater] The feed no longer offers {_state.ReadyVersion}; the downloaded package was revoked.");
                    }

                    if (remove) { RemoveReadyLocked(); }
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

            // A valid feed offer older than an already-ready package is a
            // withdrawal of that package. Revoke it before preparing anything,
            // so a failed download cannot leave the withdrawn package installable.
            lock (_gate)
            {
                if (HasUsableReadyLocked() && StrictVersion.IsValid(_state.ReadyVersion) &&
                    StrictVersion.Compare(_state.ReadyVersion, version) > 0)
                {
                    _info?.Invoke($"[Updater] The feed now offers {version}; the downloaded {_state.ReadyVersion} package was revoked.");
                    RemoveReadyLocked();
                    SaveLocked();
                }
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
                    _state.CachedManifestVersion = version;
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
                // The new ETag is deliberately not persisted here: if download,
                // extraction or verification fails, the next check must still
                // fetch the full manifest instead of receiving a 304.
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
                // Persist the validator only now: it describes the manifest whose
                // package actually became ready.
                AppendEtagLocked(result.ETag);
                _state.CachedManifestVersion = version;
                _state.AvailableVersion = version;
                _state.AvailableNotesUrl = manifest.Payload.NotesUrl;
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
            DateTime next = DateTime.UtcNow;
            if (DateTime.TryParse(_state.NextAllowedAttemptUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
            {
                next = parsed.ToUniversalTime();
            }

            // While an unattended retry is still allowed, wake at the retry
            // deadline instead of waiting a full day. Once the attempt limit is
            // reached this no longer shortens the schedule. Do not spin while a
            // handoff is already in progress.
            if (_phase != UpdatePhase.Installing &&
                _unattendedEnabled?.Invoke() == true && HasUsableReadyLocked() &&
                (string.IsNullOrEmpty(_state.UnattendedVersion) ||
                 !string.Equals(_state.UnattendedVersion, _state.ReadyVersion, StringComparison.Ordinal) ||
                 _state.UnattendedAttempts < MaxUnattendedAttempts))
            {
                DateTime retry = DateTime.UtcNow;
                if (DateTime.TryParse(_state.LastUnattendedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last))
                {
                    retry = last.ToUniversalTime().Add(UnattendedRetryDelay);
                }
                if (retry < next) { next = retry; }
            }

            return next;
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
