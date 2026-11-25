using System.Security.Cryptography;
using System.Text.Json;
using VRPC.Application;
using VRPC.Updates;

namespace VRPC.Tests.Coordinator
{
    public sealed class UpdateCoordinatorTests : IDisposable
    {
        private readonly string _root;
        private readonly string _statePath;

        public UpdateCoordinatorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vrpc-updater-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            AppPaths.OverrideRoot(_root);
            _statePath = Path.Combine(_root, "state", "updater-state.json");
        }

        public void Dispose()
        {
            AppPaths.ResetRoot();
            try { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } } catch { }
        }

        [Fact]
        public async Task Available_update_is_downloaded_extracted_and_becomes_ready()
        {
            FakeSource source = new()
            {
                Result = Available("1.2.0", notesUrl: "https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0")
            };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Ready);

            Assert.Equal(1, source.Downloads);
            Assert.Equal(1, source.Extracts);
            UpdateStatusSnapshot status = coordinator.GetStatus();
            Assert.Equal("1.2.0", status.AvailableVersion);
            Assert.Equal("https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0", status.NotesUrl);

            UpdaterState state = store.Load();
            Assert.Equal("1.2.0", state.ReadyVersion);
            Assert.True(Directory.Exists(state.ReadyPackageDir));
            Assert.True(AppPaths.IsUnderUpdatesRoot(state.ReadyPackageDir));
        }

        [Fact]
        public async Task Up_to_date_check_keeps_idle_and_downloads_nothing()
        {
            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate, ETag = "\"etag\"" } };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => source.Checks >= 1);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Idle);

            Assert.Equal(0, source.Downloads);
            Assert.Equal(0, source.Extracts);
            Assert.Equal("\"etag\"", store.Load().ManifestEtag);
        }

        [Fact]
        public async Task Failed_check_records_backoff()
        {
            FakeSource source = new()
            {
                Result = new UpdateCheckResult
                {
                    Outcome = UpdateCheckOutcome.Failed,
                    ErrorCategory = UpdateErrorCategory.Network,
                    ErrorMessage = "The update feed could not be reached."
                }
            };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Failed);

            UpdaterState state = store.Load();
            Assert.Equal(1, state.ConsecutiveFailures);
            Assert.Equal(UpdateErrorCategory.Network, state.LastErrorCategory);
            Assert.True(DateTime.TryParse(state.NextAllowedAttemptUtc, out DateTime next));
            Assert.True(next.ToUniversalTime() > DateTime.UtcNow.AddMinutes(50));
        }

        [Fact]
        public async Task Up_to_date_clears_a_stale_ready_package()
        {
            string readyDirectory = Path.Combine(AppPaths.UpdatesDir, "stale-" + Guid.NewGuid().ToString("N"));
            string packageDirectory = Path.Combine(readyDirectory, "package");
            Directory.CreateDirectory(packageDirectory);
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState { ReadyVersion = "1.2.0", ReadyPackageDir = packageDirectory });

            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            using UpdateCoordinator coordinator = CreateCoordinator(source, new FakeLauncher(), store, currentVersion: "1.2.0");

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Idle);
            await WaitUntilAsync(() => !Directory.Exists(readyDirectory));

            Assert.Equal("", store.Load().ReadyVersion);
        }

        [Fact]
        public void Manual_check_is_rate_limited_and_rejected_while_busy()
        {
            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(source, new FakeLauncher(), store, initialDelay: TimeSpan.FromDays(1));

            Assert.True(coordinator.TryRequestManualCheck(out string first));
            Assert.Equal("accepted", first);
            Assert.False(coordinator.TryRequestManualCheck(out string second));
            Assert.Equal("rate_limited", second);
        }

        [Fact]
        public async Task Install_requires_ready_then_launches_and_triggers_shutdown()
        {
            FakeSource source = new() { Result = Available("1.2.0") };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using ManualResetEventSlim launched = new(false);
            using UpdateCoordinator coordinator = new(
                "1.1.0", UpdateContract.Channel, source, launcher, store,
                message => { }, null, () => launched.Set(), TimeSpan.FromMilliseconds(10));

            Assert.False(coordinator.TryRequestInstall(out string early));
            Assert.Equal("not_ready", early);
            Assert.Equal(0, launcher.Launches);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Ready);

            Assert.True(coordinator.TryRequestInstall(out string reply));
            Assert.Equal("installing", reply);
            Assert.True(launched.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, launcher.Launches);
            Assert.False(launcher.LastUnattended);
            Assert.Equal("1.2.0", store.Load().InstalledTargetVersion);
        }

        [Fact]
        public async Task Install_fails_when_saved_manifest_no_longer_verifies()
        {
            FakeSource source = new() { Result = Available("1.2.0") };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Ready);

            source.SavedManifestVerifies = false;
            Assert.False(coordinator.TryRequestInstall(out string reply));
            Assert.Equal("failed", reply);
            Assert.Equal(0, launcher.Launches);
            Assert.Equal(UpdatePhase.Failed, coordinator.GetStatus().Phase);
        }

        [Fact]
        public async Task Install_fails_when_the_ready_archive_no_longer_matches_the_signed_hash()
        {
            FakeSource source = new() { Result = Available("1.2.0") };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Ready);

            byte[] tampered = new byte[1024];
            tampered[0] = 1;
            string archivePath = Path.Combine(Directory.GetParent(store.Load().ReadyPackageDir)!.FullName, "package.zip");
            File.WriteAllBytes(archivePath, tampered);

            Assert.False(coordinator.TryRequestInstall(out string reply));
            Assert.Equal("failed", reply);
            Assert.Equal(0, launcher.Launches);
            Assert.Equal(UpdateErrorCategory.HashMismatch, store.Load().LastErrorCategory);
            Assert.Equal(UpdatePhase.Failed, coordinator.GetStatus().Phase);
        }

        [Fact]
        public async Task Cancellation_during_download_leaves_no_ready_state()
        {
            FakeSource source = new() { Result = Available("1.2.0"), BlockDownload = true };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using CancellationTokenSource cancellation = new();
            UpdateCoordinator coordinator = new(
                "1.1.0", UpdateContract.Channel, source, launcher, store,
                message => { }, null, null, TimeSpan.FromMilliseconds(10));

            coordinator.Start(cancellation.Token);
            await WaitUntilAsync(() => source.Downloads == 1);
            cancellation.Cancel();
            coordinator.Dispose();

            await Task.Delay(100);
            UpdaterState state = store.Load();
            Assert.Equal("", state.ReadyVersion);
            Assert.Equal("", state.ManifestEtag);
            Assert.Equal("1.2.0", state.AvailableVersion);
            Assert.False(Directory.Exists(Path.Combine(_root, "state", "updates")) &&
                         Directory.GetDirectories(Path.Combine(_root, "state", "updates")).Length > 0);

            // A restart must retry the still-advertised version from a full manifest.
            state.NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O");
            store.Save(state);
            source.BlockDownload = false;

            using UpdateCoordinator restarted = CreateCoordinator(source, launcher, store);
            restarted.Start(CancellationToken.None);
            await WaitUntilAsync(() => restarted.GetStatus().Phase == UpdatePhase.Ready);
            Assert.Equal("1.2.0", store.Load().ReadyVersion);
            Assert.Equal("\"etag-1.2.0\"", store.Load().ManifestEtag);
        }

        [Fact]
        public async Task Ready_package_for_the_installed_version_is_reconciled_away()
        {
            string readyDirectory = Path.Combine(AppPaths.UpdatesDir, "installed-" + Guid.NewGuid().ToString("N"));
            string packageDirectory = Path.Combine(readyDirectory, "package");
            Directory.CreateDirectory(packageDirectory);
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ReadyVersion = "1.2.0",
                ReadyPackageDir = packageDirectory,
                InstalledTargetVersion = "1.2.0"
            });

            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            using UpdateCoordinator coordinator = CreateCoordinator(source, new FakeLauncher(), store, currentVersion: "1.2.0");

            Assert.NotEqual(UpdatePhase.Ready, coordinator.GetStatus().Phase);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => !Directory.Exists(readyDirectory));

            UpdaterState state = store.Load();
            Assert.Equal("", state.ReadyVersion);
            Assert.Equal("", state.InstalledTargetVersion);
        }

        [Fact]
        public async Task Transient_check_failure_keeps_a_ready_package_installable()
        {
            string readyDirectory = Path.Combine(AppPaths.UpdatesDir, "transient-" + Guid.NewGuid().ToString("N"));
            string packageDirectory = Path.Combine(readyDirectory, "package");
            Directory.CreateDirectory(packageDirectory);
            UpdateManifest saved = new UpdateManifest
            {
                Schema = UpdateContract.Schema,
                Payload = Available("1.2.0").Manifest!.Payload,
                Signature = new UpdateSignature
                {
                    Algorithm = UpdateContract.Algorithm,
                    KeyId = "vrpc-test-key",
                    Value = Convert.ToBase64String(new byte[64])
                }
            };
            File.WriteAllText(Path.Combine(readyDirectory, "manifest.json"), UpdateManifestJson.SerializeEnvelope(saved));
            File.WriteAllBytes(Path.Combine(readyDirectory, "package.zip"), new byte[saved.Payload.Asset.Size]);

            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState { ReadyVersion = "1.2.0", ReadyPackageDir = packageDirectory });

            FakeSource source = new()
            {
                Result = new UpdateCheckResult
                {
                    Outcome = UpdateCheckOutcome.Failed,
                    ErrorCategory = UpdateErrorCategory.Network,
                    ErrorMessage = "The update feed could not be reached."
                }
            };
            FakeLauncher launcher = new();
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => store.Load().ConsecutiveFailures >= 1);

            Assert.Equal(UpdatePhase.Ready, coordinator.GetStatus().Phase);
            Assert.True(coordinator.TryRequestInstall(out string reply));
            Assert.Equal("installing", reply);
            Assert.Equal(1, launcher.Launches);
        }

        [Fact]
        public async Task Download_failure_does_not_persist_the_etag_and_the_retry_fetches_the_full_manifest()
        {
            FakeSource source = new()
            {
                Result = Available("1.2.0"),
                DownloadResult = new UpdateOperationResult
                {
                    Success = false,
                    ErrorCategory = UpdateErrorCategory.Network,
                    ErrorMessage = "The download was interrupted."
                }
            };
            UpdateStateStore store = new(_statePath);
            using (UpdateCoordinator first = CreateCoordinator(source, new FakeLauncher(), store))
            {
                first.Start(CancellationToken.None);
                await WaitUntilAsync(() => first.GetStatus().Phase == UpdatePhase.Failed);
            }

            UpdaterState afterFailure = store.Load();
            Assert.Equal("", afterFailure.ManifestEtag);
            Assert.Equal("1.2.0", afterFailure.AvailableVersion);
            Assert.Equal("", afterFailure.ReadyVersion);

            afterFailure.NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O");
            store.Save(afterFailure);
            source.DownloadResult = new UpdateOperationResult { Success = true };

            using UpdateCoordinator second = CreateCoordinator(source, new FakeLauncher(), store);
            second.Start(CancellationToken.None);
            await WaitUntilAsync(() => second.GetStatus().Phase == UpdatePhase.Ready);

            Assert.Contains(source.RequestedEtags, etag => etag is null);
            Assert.Equal("1.2.0", store.Load().ReadyVersion);
            Assert.Equal("\"etag-1.2.0\"", store.Load().ManifestEtag);
        }

        [Theory]
        [InlineData("extract")]
        [InlineData("verify")]
        public async Task Extraction_and_verification_failures_keep_the_old_etag(string failure)
        {
            FakeSource source = new() { Result = Available("1.2.0") };
            if (failure == "extract")
            {
                source.ExtractResult = new UpdateOperationResult
                {
                    Success = false,
                    ErrorCategory = UpdateErrorCategory.Extraction,
                    ErrorMessage = "The archive could not be extracted."
                };
            }
            else
            {
                source.PackageVerifies = false;
            }

            UpdateStateStore store = new(_statePath);
            UpdaterState initial = new()
            {
                ManifestEtag = "\"etag-old\"",
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            };
            store.Save(initial);

            using (UpdateCoordinator first = CreateCoordinator(source, new FakeLauncher(), store))
            {
                first.Start(CancellationToken.None);
                await WaitUntilAsync(() => first.GetStatus().Phase == UpdatePhase.Failed);
            }

            Assert.Equal("\"etag-old\"", store.Load().ManifestEtag);
            Assert.Equal("1.2.0", store.Load().AvailableVersion);

            source.ExtractResult = new UpdateOperationResult { Success = true };
            source.PackageVerifies = true;
            UpdaterState retry = store.Load();
            retry.NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O");
            store.Save(retry);

            using UpdateCoordinator second = CreateCoordinator(source, new FakeLauncher(), store);
            second.Start(CancellationToken.None);
            await WaitUntilAsync(() => second.GetStatus().Phase == UpdatePhase.Ready);

            Assert.Equal("\"etag-1.2.0\"", store.Load().ManifestEtag);
        }

        [Fact]
        public async Task Persisted_bad_etag_with_an_unprepared_update_recovers_through_a_304()
        {
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ManifestEtag = "\"etag-1.2.0\"",
                CachedManifestVersion = "1.2.0",
                AvailableVersion = "1.2.0",
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            });

            FakeSource source = new();
            source.CheckHandler = (etag, attempt) =>
            {
                if (attempt == 1 && etag == "\"etag-1.2.0\"")
                {
                    return new UpdateCheckResult { Outcome = UpdateCheckOutcome.NotModified, ETag = etag! };
                }
                return Available("1.2.0");
            };

            using UpdateCoordinator coordinator = CreateCoordinator(source, new FakeLauncher(), store);
            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Ready);

            Assert.Equal(2, source.Checks);
            Assert.Equal("\"etag-1.2.0\"", source.RequestedEtags[0]);
            Assert.Null(source.RequestedEtags[1]);
            Assert.Equal("1.2.0", store.Load().ReadyVersion);
            Assert.Equal("\"etag-1.2.0\"", store.Load().ManifestEtag);
        }

        [Fact]
        public async Task Repeated_304s_use_bounded_backoff_instead_of_a_tight_loop()
        {
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ManifestEtag = "\"etag-1.2.0\"",
                AvailableVersion = "1.2.0",
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            });

            FakeSource source = new()
            {
                Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.NotModified, ETag = "\"etag-1.2.0\"" }
            };
            using UpdateCoordinator coordinator = CreateCoordinator(source, new FakeLauncher(), store);
            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => store.Load().ConsecutiveFailures >= 1);
            await Task.Delay(300);

            Assert.InRange(source.Checks, 1, 2);
            UpdaterState state = store.Load();
            Assert.Equal("", state.ManifestEtag);
            Assert.Equal("1.2.0", state.AvailableVersion);
            Assert.True(DateTime.TryParse(state.NextAllowedAttemptUtc, out DateTime next));
            Assert.True(next.ToUniversalTime() > DateTime.UtcNow.AddMinutes(30));
        }

        [Fact]
        public async Task A_valid_feed_that_no_longer_offers_the_ready_version_revokes_it()
        {
            string readyDirectory = WriteReadyPackage("1.2.0");
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ReadyVersion = "1.2.0",
                ReadyPackageDir = Path.Combine(readyDirectory, "package"),
                AvailableVersion = "1.2.0",
                ManifestEtag = "\"etag-1.2.0\"",
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            });

            FakeSource source = new() { Result = UpToDate("1.1.0", "etag-1.1.0") };
            FakeLauncher launcher = new();
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);
            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Idle);

            Assert.Equal("", store.Load().ReadyVersion);
            Assert.False(Directory.Exists(readyDirectory));
            Assert.False(coordinator.TryRequestInstall(out string reply));
            Assert.Equal("not_ready", reply);
            Assert.Equal(0, launcher.Launches);
        }

        [Fact]
        public async Task A_newer_offer_revokes_an_even_newer_ready_package_and_replaces_it()
        {
            string readyDirectory = WriteReadyPackage("1.3.0");
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ReadyVersion = "1.3.0",
                ReadyPackageDir = Path.Combine(readyDirectory, "package"),
                AvailableVersion = "1.3.0",
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            });

            FakeSource source = new() { Result = Available("1.2.0") };
            using UpdateCoordinator coordinator = CreateCoordinator(source, new FakeLauncher(), store);
            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => store.Load().ReadyVersion == "1.2.0");

            Assert.Equal("1.2.0", store.Load().ReadyVersion);
            Assert.False(Directory.Exists(readyDirectory));
        }

        [Fact]
        public async Task A_304_for_the_same_manifest_keeps_the_ready_package_installable()
        {
            string readyDirectory = WriteReadyPackage("1.2.0");
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ReadyVersion = "1.2.0",
                ReadyPackageDir = Path.Combine(readyDirectory, "package"),
                AvailableVersion = "1.2.0",
                ManifestEtag = "\"etag-1.2.0\"",
                CachedManifestVersion = "1.2.0",
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            });

            FakeSource source = new()
            {
                Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.NotModified, ETag = "\"etag-1.2.0\"" }
            };
            FakeLauncher launcher = new();
            using UpdateCoordinator coordinator = CreateCoordinator(source, launcher, store);
            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => source.Checks >= 1);

            Assert.Equal(UpdatePhase.Ready, coordinator.GetStatus().Phase);
            Assert.Equal("1.2.0", store.Load().ReadyVersion);
            Assert.True(coordinator.TryRequestInstall(out string reply));
            Assert.Equal("installing", reply);
            Assert.Equal(1, launcher.Launches);
        }

        [Fact]
        public async Task Manual_check_during_the_initial_delay_starts_promptly()
        {
            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            using UpdateCoordinator coordinator = CreateCoordinator(
                source, new FakeLauncher(), new UpdateStateStore(_statePath), initialDelay: TimeSpan.FromDays(1));

            coordinator.Start(CancellationToken.None);
            Assert.True(coordinator.TryRequestManualCheck(out string reply));
            Assert.Equal("accepted", reply);
            await WaitUntilAsync(() => source.Checks >= 1, timeoutMs: 2000);
        }

        [Fact]
        public async Task Manual_check_during_a_long_backoff_starts_promptly()
        {
            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            using UpdateCoordinator coordinator = CreateCoordinator(
                source, new FakeLauncher(), new UpdateStateStore(_statePath));

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => source.Checks >= 1);
            Assert.True(coordinator.TryRequestManualCheck(out string reply));
            Assert.Equal("accepted", reply);
            await WaitUntilAsync(() => source.Checks >= 2, timeoutMs: 2000);
        }

        [Fact]
        public async Task Shutdown_cancels_the_initial_wait()
        {
            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            UpdateCoordinator coordinator = CreateCoordinator(
                source, new FakeLauncher(), new UpdateStateStore(_statePath), initialDelay: TimeSpan.FromDays(1));

            coordinator.Start(CancellationToken.None);
            coordinator.Dispose();
            await Task.Delay(150);
            Assert.Equal(0, source.Checks);
        }

        [Fact]
        public async Task Unattended_updates_install_a_ready_package_automatically()
        {
            FakeSource source = new() { Result = Available("1.2.0") };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(
                source, launcher, store, unattended: () => true);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => launcher.Launches == 1);
            await WaitUntilAsync(() => store.Load().InstalledTargetVersion == "1.2.0");

            Assert.True(launcher.LastUnattended);
            UpdaterState state = store.Load();
            Assert.Equal("1.2.0", state.InstalledTargetVersion);
            Assert.Equal(1, state.UnattendedAttempts);
            Assert.Equal("1.2.0", state.UnattendedVersion);
        }

        [Fact]
        public async Task Unattended_updates_do_not_install_when_disabled()
        {
            FakeSource source = new() { Result = Available("1.2.0") };
            FakeLauncher launcher = new();
            UpdateStateStore store = new(_statePath);
            using UpdateCoordinator coordinator = CreateCoordinator(
                source, launcher, store, unattended: () => false);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Ready);
            await Task.Delay(400);

            Assert.Equal(0, launcher.Launches);
            Assert.Equal(UpdatePhase.Ready, coordinator.GetStatus().Phase);
        }

        [Fact]
        public async Task Enabling_unattended_updates_kicks_a_ready_package()
        {
            string readyDirectory = WriteReadyPackage("1.2.0");
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ReadyVersion = "1.2.0",
                ReadyPackageDir = Path.Combine(readyDirectory, "package"),
                AvailableVersion = "1.2.0",
                NextAllowedAttemptUtc = DateTime.UtcNow.AddHours(20).ToString("O")
            });

            bool enabled = false;
            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            FakeLauncher launcher = new();
            using UpdateCoordinator coordinator = CreateCoordinator(
                source, launcher, store, unattended: () => enabled);

            coordinator.Start(CancellationToken.None);
            await Task.Delay(200);
            Assert.Equal(0, launcher.Launches);

            enabled = true;
            coordinator.RequestAutoInstall();
            await WaitUntilAsync(() => launcher.Launches == 1);

            Assert.True(launcher.LastUnattended);
            Assert.Equal("1.2.0", store.Load().InstalledTargetVersion);
        }

        [Fact]
        public async Task Unattended_updates_stop_after_the_attempt_limit()
        {
            string readyDirectory = WriteReadyPackage("1.2.0");
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ReadyVersion = "1.2.0",
                ReadyPackageDir = Path.Combine(readyDirectory, "package"),
                AvailableVersion = "1.2.0",
                UnattendedVersion = "1.2.0",
                UnattendedAttempts = 3,
                LastUnattendedUtc = DateTime.UtcNow.AddMinutes(-30).ToString("O"),
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            });

            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            FakeLauncher launcher = new();
            using UpdateCoordinator coordinator = CreateCoordinator(
                source, launcher, store, unattended: () => true);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => source.Checks >= 1);
            await Task.Delay(300);

            Assert.Equal(0, launcher.Launches);
            Assert.Equal(UpdatePhase.Ready, coordinator.GetStatus().Phase);
        }

        [Fact]
        public async Task Unattended_updates_respect_the_retry_delay()
        {
            string readyDirectory = WriteReadyPackage("1.2.0");
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState
            {
                ReadyVersion = "1.2.0",
                ReadyPackageDir = Path.Combine(readyDirectory, "package"),
                AvailableVersion = "1.2.0",
                UnattendedVersion = "1.2.0",
                UnattendedAttempts = 1,
                LastUnattendedUtc = DateTime.UtcNow.ToString("O"),
                NextAllowedAttemptUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O")
            });

            FakeSource source = new() { Result = new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate } };
            FakeLauncher launcher = new();
            using UpdateCoordinator coordinator = CreateCoordinator(
                source, launcher, store, unattended: () => true);

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => source.Checks >= 1);
            await Task.Delay(300);

            Assert.Equal(0, launcher.Launches);
        }

        private string WriteReadyPackage(string version)
        {
            string resourceDirectory = Path.Combine(AppPaths.UpdatesDir, "ready-" + Guid.NewGuid().ToString("N"));
            string packageDirectory = Path.Combine(resourceDirectory, "package");
            Directory.CreateDirectory(packageDirectory);

            UpdateManifest manifest = Available(version).Manifest!;
            File.WriteAllText(Path.Combine(resourceDirectory, "manifest.json"), UpdateManifestJson.SerializeEnvelope(manifest));
            File.WriteAllBytes(Path.Combine(resourceDirectory, "package.zip"), new byte[manifest.Payload.Asset.Size]);
            return resourceDirectory;
        }

        private static UpdateCheckResult UpToDate(string version, string etag)
        {
            UpdateCheckResult result = Available(version, "https://github.com/Viktirr/vrpc-app/releases/tag/v" + version);
            result.Outcome = UpdateCheckOutcome.UpToDate;
            result.ETag = "\"" + etag + "\"";
            return result;
        }

        [Fact]
        public void State_store_round_trips_and_preserves_corrupt_files()
        {
            UpdateStateStore store = new(_statePath);
            store.Save(new UpdaterState { ReadyVersion = "1.2.0", ConsecutiveFailures = 2 });

            UpdaterState loaded = store.Load();
            Assert.Equal("1.2.0", loaded.ReadyVersion);
            Assert.Equal(2, loaded.ConsecutiveFailures);
            Assert.True(File.Exists(_statePath));

            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, "{ not json");
            UpdaterState recovered = store.Load();
            Assert.Equal("", recovered.ReadyVersion);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_statePath)!, "updater-state.json.corrupt-*"));
        }

        private UpdateCoordinator CreateCoordinator(
            FakeSource source,
            FakeLauncher launcher,
            UpdateStateStore store,
            string currentVersion = "1.1.0",
            TimeSpan? initialDelay = null,
            Func<bool>? unattended = null)
        {
            return new UpdateCoordinator(
                currentVersion,
                UpdateContract.Channel,
                source,
                launcher,
                store,
                message => { },
                null,
                null,
                initialDelay ?? TimeSpan.FromMilliseconds(10),
                unattended);
        }

        private static UpdateCheckResult Available(string version, string notesUrl = "https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0")
        {
            return new UpdateCheckResult
            {
                Outcome = UpdateCheckOutcome.UpdateAvailable,
                ETag = "\"etag-" + version + "\"",
                Manifest = new UpdateManifest
                {
                    Schema = UpdateContract.Schema,
                    Payload = new UpdatePayload
                    {
                        Product = UpdateContract.Product,
                        Version = version,
                        Channel = UpdateContract.Channel,
                        Runtime = UpdateContract.Runtime,
                        ReleaseTag = UpdateContract.ReleaseTag(version),
                        Asset = new UpdateAsset
                        {
                            Name = UpdateContract.AssetName(version),
                            Url = $"https://github.com/Viktirr/vrpc-app/releases/download/{UpdateContract.ReleaseTag(version)}/{UpdateContract.AssetName(version)}",
                            Size = 1024,
                            Sha256 = Convert.ToHexString(SHA256.HashData(new byte[1024])).ToLowerInvariant()
                        },
                        NotesUrl = notesUrl,
                        PublishedAtUtc = "2026-11-15T00:00:00Z"
                    },
                    Signature = new UpdateSignature
                    {
                        Algorithm = UpdateContract.Algorithm,
                        KeyId = "vrpc-test-key",
                        Value = Convert.ToBase64String(new byte[64])
                    }
                }
            };
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) { return; }
                await Task.Delay(20);
            }
            throw new TimeoutException("The condition was not met in time.");
        }

        private sealed class FakeSource : IUpdatePackageSource
        {
            public UpdateCheckResult Result { get; set; } = new() { Outcome = UpdateCheckOutcome.UpToDate };
            public Func<string?, int, UpdateCheckResult>? CheckHandler { get; set; }
            public UpdateOperationResult DownloadResult { get; set; } = new() { Success = true };
            public UpdateOperationResult ExtractResult { get; set; } = new() { Success = true };
            public bool BlockDownload { get; set; }
            public bool SavedManifestVerifies { get; set; } = true;
            public bool PackageVerifies { get; set; } = true;
            public int Checks;
            public int Downloads;
            public int Extracts;
            public List<string?> RequestedEtags { get; } = new();

            public Task<UpdateCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken)
            {
                int attempt = Interlocked.Increment(ref Checks);
                RequestedEtags.Add(etag);
                if (CheckHandler is not null) { return Task.FromResult(CheckHandler(etag, attempt)); }
                return Task.FromResult(Result);
            }

            public async Task<UpdateOperationResult> DownloadAsync(UpdateAsset asset, string destinationArchivePath, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Downloads);
                if (BlockDownload)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                if (!DownloadResult.Success) { return DownloadResult; }
                await File.WriteAllBytesAsync(destinationArchivePath, new byte[asset.Size], cancellationToken);
                return new UpdateOperationResult { Success = true };
            }

            public Task<UpdateOperationResult> ExtractAsync(string archivePath, string destinationDirectory, string expectedVersion, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Extracts);
                if (!ExtractResult.Success) { return Task.FromResult(ExtractResult); }
                Directory.CreateDirectory(destinationDirectory);
                File.WriteAllText(Path.Combine(destinationDirectory, "VRPC.exe"), "stub");
                File.WriteAllText(Path.Combine(destinationDirectory, "payload.json"), JsonSerializer.Serialize(new { version = expectedVersion }));
                return Task.FromResult(new UpdateOperationResult { Success = true });
            }

            public bool VerifySavedManifest(UpdateManifest manifest, out string errorCategory, out string errorMessage)
            {
                errorCategory = "";
                errorMessage = "";
                return SavedManifestVerifies;
            }

            public bool VerifyPackageDirectory(string packageDirectory, string expectedVersion, out string errorCategory, out string errorMessage)
            {
                errorCategory = "";
                errorMessage = "";
                return PackageVerifies;
            }
        }

        private sealed class FakeLauncher : IUpdateProcessLauncher
        {
            public int Launches;
            public bool LastUnattended;
            public bool Result { get; set; } = true;

            public bool TryLaunch(string packageDirectory, string version, bool unattended, out string errorMessage)
            {
                Launches++;
                LastUnattended = unattended;
                errorMessage = Result ? "" : "launch failed";
                return Result;
            }
        }
    }
}
