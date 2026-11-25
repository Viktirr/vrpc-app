using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VRPC.Application;
using VRPC.Updates;

namespace VRPC.Tests.Integration
{
    // Full pipeline: signed feed bytes -> HTTP client -> downloader -> extractor
    // -> payload verification -> coordinator ready state -> install handoff.
    // The transport is faked so the test is hermetic; every security check in
    // the production classes still runs.
    public sealed class UpdatePipelineTests : IDisposable
    {
        private readonly string _root;
        private readonly string _work;

        public UpdatePipelineTests()
        {
            _work = Path.Combine(Path.GetTempPath(), "vrpc-pipeline-test-" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_work, "root");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_work, "package"));
            AppPaths.OverrideRoot(_root);
        }

        public void Dispose()
        {
            AppPaths.ResetRoot();
            try { if (Directory.Exists(_work)) { Directory.Delete(_work, recursive: true); } } catch { }
        }

        [Fact]
        public async Task Signed_feed_downloads_verifies_extracts_and_hands_off_to_setup()
        {
            Package package = CreatePackage("1.2.0");
            byte[] zip = File.ReadAllBytes(package.ZipPath);
            UpdateManifest manifest = SignManifest(BuildPayload("1.2.0", zip, package.AssetPath));
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJson.Compact);

            UpdateTransport transport = new(manifestBytes, zip, package.AssetPath);
            UpdateSignatureVerifier verifier = new(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vrpc-test-key"] = File.ReadAllText(FixturePath("vrpc-test-key.public.spki.b64")).Trim()
            });
            Uri feedUri = new("https://api.viktir.com/products/vrpc/update-manifest?channel=stable&runtime=win-x64");
            using HttpUpdatePackageSource source = new(transport, feedUri, "1.1.0", verifier);

            RecordingLauncher launcher = new();
            UpdateStateStore store = new(Path.Combine(_root, "state", "updater-state.json"));
            using ManualResetEventSlim shutdown = new(false);
            using UpdateCoordinator coordinator = new(
                "1.1.0", UpdateContract.Channel, source, launcher, store,
                message => { }, null, () => shutdown.Set(), TimeSpan.FromMilliseconds(10));

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Ready);

            UpdaterState state = store.Load();
            Assert.Equal("1.2.0", state.ReadyVersion);
            Assert.True(Directory.Exists(state.ReadyPackageDir));
            Assert.True(AppPaths.IsUnderUpdatesRoot(state.ReadyPackageDir));
            Assert.True(File.Exists(Path.Combine(state.ReadyPackageDir, "VRPC.exe")));
            Assert.True(File.Exists(Path.Combine(state.ReadyPackageDir, "payload.json")));
            Assert.True(File.Exists(Path.Combine(state.ReadyPackageDir, "vrpc-installer-ui.exe")));
            Assert.Equal("1.2.0", coordinator.GetStatus().AvailableVersion);

            Assert.True(coordinator.TryRequestInstall(out string reply));
            Assert.Equal("installing", reply);
            Assert.True(shutdown.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, launcher.Launches);
            Assert.Equal("1.2.0", launcher.Version);
            Assert.Equal(Path.GetFullPath(state.ReadyPackageDir), Path.GetFullPath(launcher.PackageDirectory!));
        }

        [Fact]
        public async Task Tampered_asset_hash_fails_closed_and_leaves_nothing_ready()
        {
            Package package = CreatePackage("1.2.0");
            byte[] zip = File.ReadAllBytes(package.ZipPath);
            UpdatePayload payload = BuildPayload("1.2.0", zip, package.AssetPath);
            payload.Asset.Sha256 = new string('b', 64);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(SignManifest(payload), UpdateJson.Compact);

            UpdateTransport transport = new(manifestBytes, zip, package.AssetPath);
            using HttpUpdatePackageSource source = new(transport, new Uri("https://api.viktir.com/products/vrpc/update-manifest?channel=stable&runtime=win-x64"), "1.1.0", TestVerifier());
            RecordingLauncher launcher = new();
            UpdateStateStore store = new(Path.Combine(_root, "state", "updater-state.json"));
            using UpdateCoordinator coordinator = new(
                "1.1.0", UpdateContract.Channel, source, launcher, store,
                message => { }, null, null, TimeSpan.FromMilliseconds(10));

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Failed);

            Assert.Equal(UpdateErrorCategory.HashMismatch, coordinator.GetStatus().LastErrorCategory);
            Assert.Equal("", store.Load().ReadyVersion);
            Assert.False(coordinator.TryRequestInstall(out _));
        }

        [Fact]
        public async Task Invalid_signature_fails_before_download()
        {
            Package package = CreatePackage("1.2.0");
            byte[] zip = File.ReadAllBytes(package.ZipPath);
            UpdateManifest manifest = SignManifest(BuildPayload("1.2.0", zip, package.AssetPath));
            manifest.Payload.Asset.Sha256 = new string('c', 64);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJson.Compact);

            UpdateTransport transport = new(manifestBytes, zip, package.AssetPath);
            using HttpUpdatePackageSource source = new(transport, new Uri("https://api.viktir.com/products/vrpc/update-manifest?channel=stable&runtime=win-x64"), "1.1.0", TestVerifier());
            UpdateStateStore store = new(Path.Combine(_root, "state", "updater-state.json"));
            using UpdateCoordinator coordinator = new(
                "1.1.0", UpdateContract.Channel, source, new RecordingLauncher(), store,
                message => { }, null, null, TimeSpan.FromMilliseconds(10));

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Failed);

            Assert.Equal(UpdateErrorCategory.InvalidSignature, coordinator.GetStatus().LastErrorCategory);
            Assert.Equal(0, transport.AssetRequests);
        }

        [Fact]
        public async Task Extraction_rejects_an_archive_with_traversal_entries()
        {
            Package package = CreatePackage("1.2.0");
            byte[] zip = File.ReadAllBytes(package.ZipPath);
            File.WriteAllBytes(package.ZipPath, AddTraversalEntry(package.ZipPath));

            UpdatePayload payload = BuildPayload("1.2.0", File.ReadAllBytes(package.ZipPath), package.AssetPath);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(SignManifest(payload), UpdateJson.Compact);
            UpdateTransport transport = new(manifestBytes, File.ReadAllBytes(package.ZipPath), package.AssetPath);
            using HttpUpdatePackageSource source = new(transport, new Uri("https://api.viktir.com/products/vrpc/update-manifest?channel=stable&runtime=win-x64"), "1.1.0", TestVerifier());
            UpdateStateStore store = new(Path.Combine(_root, "state", "updater-state.json"));
            using UpdateCoordinator coordinator = new(
                "1.1.0", UpdateContract.Channel, source, new RecordingLauncher(), store,
                message => { }, null, null, TimeSpan.FromMilliseconds(10));

            coordinator.Start(CancellationToken.None);
            await WaitUntilAsync(() => coordinator.GetStatus().Phase == UpdatePhase.Failed);

            Assert.Equal(UpdateErrorCategory.Extraction, coordinator.GetStatus().LastErrorCategory);
            Assert.Equal("", store.Load().ReadyVersion);
        }

        private static UpdateSignatureVerifier TestVerifier()
        {
            return new UpdateSignatureVerifier(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vrpc-test-key"] = File.ReadAllText(FixturePath("vrpc-test-key.public.spki.b64")).Trim()
            });
        }

        private static string FixturePath(string name)
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Updater", name);

        private UpdatePayload BuildPayload(string version, byte[] zip, string assetPath)
        {
            return new UpdatePayload
            {
                Product = UpdateContract.Product,
                Version = version,
                Channel = UpdateContract.Channel,
                Runtime = UpdateContract.Runtime,
                ReleaseTag = UpdateContract.ReleaseTag(version),
                Asset = new UpdateAsset
                {
                    Name = UpdateContract.AssetName(version),
                    Url = assetPath,
                    Size = zip.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()
                },
                NotesUrl = $"https://github.com/Viktirr/vrpc-app/releases/tag/{UpdateContract.ReleaseTag(version)}",
                PublishedAtUtc = "2026-11-15T00:00:00Z"
            };
        }

        private static UpdateManifest SignManifest(UpdatePayload payload)
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(FixturePath("vrpc-test-key.private.pem")));
            byte[] canonical = Jcs.Canonicalize(JsonSerializer.SerializeToElement(payload, UpdateJson.Compact));
            byte[] signature = key.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new UpdateManifest
            {
                Schema = UpdateContract.Schema,
                Payload = payload,
                Signature = new UpdateSignature
                {
                    Algorithm = UpdateContract.Algorithm,
                    KeyId = "vrpc-test-key",
                    Value = Convert.ToBase64String(signature)
                }
            };
        }

        private Package CreatePackage(string version)
        {
            string packageDirectory = Path.Combine(_work, "package");
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(Path.Combine(packageDirectory, "VRPC.exe"), "stub executable");
            File.WriteAllText(Path.Combine(packageDirectory, "vrpc-installer-ui.exe"), "stub panel");

            List<object> files = new();
            foreach (string name in new[] { "VRPC.exe", "vrpc-installer-ui.exe" })
            {
                string path = Path.Combine(packageDirectory, name);
                files.Add(new
                {
                    path = name,
                    size = new FileInfo(path).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                });
            }

            string payloadJson = JsonSerializer.Serialize(new
            {
                schema = 1,
                product = "VRPC",
                version,
                channel = "stable",
                entryPoint = "VRPC.exe",
                files
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            File.WriteAllText(Path.Combine(packageDirectory, "payload.json"), payloadJson, new UTF8Encoding(false));

            string zipPath = Path.Combine(_work, "VRPC-" + version + "-win-x64.zip");
            ZipFile.CreateFromDirectory(packageDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            return new Package
            {
                ZipPath = zipPath,
                AssetPath = $"https://github.com/Viktirr/vrpc-app/releases/download/v{version}/VRPC-{version}-win-x64.zip"
            };
        }

        private static byte[] AddTraversalEntry(string zipPath)
        {
            using MemoryStream stream = new();
            using (FileStream source = File.OpenRead(zipPath))
            {
                source.CopyTo(stream);
            }
            stream.Position = 0;
            using ZipArchive archive = new(stream, ZipArchiveMode.Update, leaveOpen: true);
            ZipArchiveEntry entry = archive.CreateEntry("../../escaped.txt");
            using (StreamWriter writer = new(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write("escaped");
            }
            archive.Dispose();
            return stream.ToArray();
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) { return; }
                await Task.Delay(25);
            }
            throw new TimeoutException("The condition was not met in time.");
        }

        private sealed class Package
        {
            public string ZipPath { get; init; } = "";
            public string AssetPath { get; init; } = "";
        }

        private sealed class RecordingLauncher : IUpdateProcessLauncher
        {
            public int Launches;
            public string? PackageDirectory;
            public string? Version;

            public bool TryLaunch(string packageDirectory, string version, bool unattended, out string errorMessage)
            {
                Launches++;
                PackageDirectory = packageDirectory;
                Version = version;
                errorMessage = "";
                return true;
            }
        }

        private sealed class UpdateTransport : HttpMessageHandler
        {
            private readonly byte[] _manifest;
            private readonly byte[] _zip;
            private readonly string _assetPath;

            public UpdateTransport(byte[] manifest, byte[] zip, string assetPath)
            {
                _manifest = manifest;
                _zip = zip;
                _assetPath = new Uri(assetPath).AbsolutePath;
            }

            public int AssetRequests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri?.AbsolutePath ?? "";
                if (path == "/products/vrpc/update-manifest")
                {
                    return Task.FromResult(Response(_manifest, "application/json"));
                }
                if (string.Equals(path, _assetPath, StringComparison.Ordinal))
                {
                    AssetRequests++;
                    return Task.FromResult(Response(_zip, "application/zip"));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static HttpResponseMessage Response(byte[] bytes, string mediaType)
            {
                ByteArrayContent content = new(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
        }
    }
}
