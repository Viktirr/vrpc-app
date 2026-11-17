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
            Assert.False(Directory.Exists(Path.Combine(_root, "state", "updates")) &&
                         Directory.GetDirectories(Path.Combine(_root, "state", "updates")).Length > 0);
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
        public async Task State_store_round_trips_and_preserves_corrupt_files()
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

        private UpdateCoordinator CreateCoordinator(FakeSource source, FakeLauncher launcher, UpdateStateStore store, string currentVersion = "1.1.0", TimeSpan? initialDelay = null)
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
                initialDelay ?? TimeSpan.FromMilliseconds(10));
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
                            Sha256 = new string('a', 64)
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
            public bool BlockDownload { get; set; }
            public bool SavedManifestVerifies { get; set; } = true;
            public bool PackageVerifies { get; set; } = true;
            public int Checks;
            public int Downloads;
            public int Extracts;

            public Task<UpdateCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Checks);
                return Task.FromResult(Result);
            }

            public async Task<UpdateOperationResult> DownloadAsync(UpdateAsset asset, string destinationArchivePath, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Downloads);
                if (BlockDownload)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                await File.WriteAllBytesAsync(destinationArchivePath, new byte[asset.Size], cancellationToken);
                return new UpdateOperationResult { Success = true };
            }

            public Task<UpdateOperationResult> ExtractAsync(string archivePath, string destinationDirectory, string expectedVersion, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Extracts);
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
            public bool Result { get; set; } = true;

            public bool TryLaunch(string packageDirectory, string version, out string errorMessage)
            {
                Launches++;
                errorMessage = Result ? "" : "launch failed";
                return Result;
            }
        }
    }
}
