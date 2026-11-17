using System.Net;
using VRPC.Updates;

namespace VRPC.Tests.Updates
{
    public sealed class HttpUpdatePackageSourceTests
    {
        private static HttpUpdatePackageSource CreateSource(FakeHttpMessageHandler handler, FakeUpdateManifestVerifier verifier, string currentVersion = "1.1.0")
            => new(handler, new Uri(TestHttp.FeedUrl), currentVersion, verifier);

        private static UpdateManifest ManualManifest(
            string version = "1.2.0",
            string product = "VRPC",
            string channel = "stable",
            string runtime = "win-x64",
            int schema = 1)
            => new()
            {
                Schema = schema,
                Payload = new UpdatePayload
                {
                    Product = product,
                    Version = version,
                    Channel = channel,
                    Runtime = runtime,
                    ReleaseTag = "v" + version,
                    Asset = new UpdateAsset
                    {
                        Name = "VRPC-" + version + "-win-x64.zip",
                        Url = "https://github.com/Viktirr/vrpc-app/releases/download/v" + version + "/VRPC-" + version + "-win-x64.zip",
                        Size = 10,
                        Sha256 = new string('a', 64)
                    },
                    NotesUrl = "https://github.com/Viktirr/vrpc-app/releases/tag/v" + version,
                    PublishedAtUtc = "2026-11-15T00:00:00Z"
                },
                Signature = new UpdateSignature { Algorithm = "ECDSA-P256-SHA256", KeyId = "test-key-1", Value = "c2ln" }
            };

        [Fact]
        public async Task FullFlow_CheckDownloadExtractAndVerify()
        {
            using TempDirectory temp = new();
            string operationDirectory = temp.Combine("operation");
            Directory.CreateDirectory(operationDirectory);
            string archive = System.IO.Path.Combine(operationDirectory, "package.zip");
            string packageDirectory = System.IO.Path.Combine(operationDirectory, "package");

            PackageFixture fixture = new();
            fixture.AddExecutables();
            fixture.Add(System.IO.Path.Combine("runtime", "extra.bin"), TestBytes.Random(64, 4));
            string zipPath = fixture.WriteZip(archive);
            byte[] zipBytes = await File.ReadAllBytesAsync(zipPath);
            string zipHash = TestBytes.Sha256Hex(zipBytes);

            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(request =>
            {
                if (string.Equals(request.RequestUri!.Host, "feed.test", StringComparison.Ordinal))
                {
                    return TestHttp.Json(ManifestFixture.Envelope("1.2.0", size: zipBytes.Length, sha256: zipHash), etag: "etag-1");
                }
                return TestHttp.Bytes(zipBytes);
            });

            using HttpUpdatePackageSource source = CreateSource(handler, verifier);

            UpdateCheckResult check = await source.CheckAsync(null, CancellationToken.None);
            Assert.Equal(UpdateCheckOutcome.UpdateAvailable, check.Outcome);
            Assert.Equal("\"etag-1\"", check.ETag);

            UpdateOperationResult download = await source.DownloadAsync(check.Manifest!.Payload.Asset, archive, CancellationToken.None);
            Assert.True(download.Success, download.ErrorMessage);

            UpdateOperationResult extraction = await source.ExtractAsync(archive, packageDirectory, "1.2.0", CancellationToken.None);
            Assert.True(extraction.Success, extraction.ErrorMessage);

            Assert.True(source.VerifyPackageDirectory(packageDirectory, "1.2.0", out string packageCategory, out string packageMessage),
                packageCategory + ": " + packageMessage);

            Assert.True(source.VerifySavedManifest(check.Manifest, out string manifestCategory, out string manifestMessage),
                manifestCategory + ": " + manifestMessage);
        }

        [Fact]
        public void VerifySavedManifest_RejectsNonNewerVersion()
        {
            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));
            using HttpUpdatePackageSource source = CreateSource(handler, verifier);

            Assert.False(source.VerifySavedManifest(ManualManifest("1.1.0"), out string category, out _));
            Assert.Equal(UpdateErrorCategory.PayloadMismatch, category);
        }

        [Fact]
        public void VerifySavedManifest_RejectsUnknownKey()
        {
            FakeUpdateManifestVerifier verifier = new()
            {
                VerifyResult = false,
                ErrorCategory = UpdateErrorCategory.UnsupportedKey,
                ErrorMessage = "Unknown signing key."
            };
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));
            using HttpUpdatePackageSource source = CreateSource(handler, verifier);
            UpdateManifest manifest = new()
            {
                Schema = 1,
                Payload = new UpdatePayload
                {
                    Product = "VRPC",
                    Version = "1.2.0",
                    Channel = "stable",
                    Runtime = "win-x64",
                    ReleaseTag = "v1.2.0",
                    Asset = new UpdateAsset { Name = "VRPC-1.2.0-win-x64.zip", Url = TestHttp.AssetUrl, Size = 10, Sha256 = new string('a', 64) },
                    NotesUrl = "https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0",
                    PublishedAtUtc = "2026-11-15T00:00:00Z"
                },
                Signature = new UpdateSignature { Algorithm = "ECDSA-P256-SHA256", KeyId = "unknown", Value = "c2ln" }
            };

            Assert.False(source.VerifySavedManifest(manifest, out string category, out string message));
            Assert.Equal(UpdateErrorCategory.UnsupportedKey, category);
            Assert.Equal("Unknown signing key.", message);
        }

        [Fact]
        public void VerifySavedManifest_RejectsChannelMismatch()
        {
            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));
            using HttpUpdatePackageSource source = CreateSource(handler, verifier);
            UpdateManifest manifest = new()
            {
                Schema = 1,
                Payload = new UpdatePayload
                {
                    Product = "VRPC",
                    Version = "1.2.0",
                    Channel = "beta",
                    Runtime = "win-x64",
                    ReleaseTag = "v1.2.0",
                    Asset = new UpdateAsset { Name = "VRPC-1.2.0-win-x64.zip", Url = TestHttp.AssetUrl, Size = 10, Sha256 = new string('a', 64) },
                    NotesUrl = "https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0",
                    PublishedAtUtc = "2026-11-15T00:00:00Z"
                },
                Signature = new UpdateSignature { Algorithm = "ECDSA-P256-SHA256", KeyId = "test-key-1", Value = "c2ln" }
            };

            Assert.False(source.VerifySavedManifest(manifest, out string category, out _));
            Assert.Equal(UpdateErrorCategory.InvalidManifest, category);
            Assert.Equal(0, verifier.Calls);
        }

        [Fact]
        public void VerifySavedManifest_RejectsProductMismatch()
        {
            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));
            using HttpUpdatePackageSource source = CreateSource(handler, verifier);
            UpdateManifest manifest = new()
            {
                Schema = 1,
                Payload = new UpdatePayload
                {
                    Product = "Other",
                    Version = "1.2.0",
                    Channel = "stable",
                    Runtime = "win-x64",
                    ReleaseTag = "v1.2.0",
                    Asset = new UpdateAsset { Name = "VRPC-1.2.0-win-x64.zip", Url = TestHttp.AssetUrl, Size = 10, Sha256 = new string('a', 64) },
                    NotesUrl = "https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0",
                    PublishedAtUtc = "2026-11-15T00:00:00Z"
                },
                Signature = new UpdateSignature { Algorithm = "ECDSA-P256-SHA256", KeyId = "test-key-1", Value = "c2ln" }
            };

            Assert.False(source.VerifySavedManifest(manifest, out string category, out _));
            Assert.Equal(UpdateErrorCategory.InvalidManifest, category);
        }

        [Fact]
        public void VerifySavedManifest_RejectsUnsupportedSchema()
        {
            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));
            using HttpUpdatePackageSource source = CreateSource(handler, verifier);
            UpdateManifest manifest = new()
            {
                Schema = 2,
                Payload = new UpdatePayload
                {
                    Product = "VRPC",
                    Version = "1.2.0",
                    Channel = "stable",
                    Runtime = "win-x64",
                    ReleaseTag = "v1.2.0",
                    Asset = new UpdateAsset { Name = "VRPC-1.2.0-win-x64.zip", Url = TestHttp.AssetUrl, Size = 10, Sha256 = new string('a', 64) },
                    NotesUrl = "https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0",
                    PublishedAtUtc = "2026-11-15T00:00:00Z"
                },
                Signature = new UpdateSignature { Algorithm = "ECDSA-P256-SHA256", KeyId = "test-key-1", Value = "c2ln" }
            };

            Assert.False(source.VerifySavedManifest(manifest, out string category, out _));
            Assert.Equal(UpdateErrorCategory.InvalidManifest, category);
        }

        [Fact]
        public void ProductionConstructor_RejectsUnsupportedChannel()
        {
            Assert.Throws<ArgumentException>(() => new HttpUpdatePackageSource("1.1.0", "beta"));
        }

        [Fact]
        public void ProductionConstructor_AcceptsStableChannel()
        {
            using HttpUpdatePackageSource source = new("1.1.0", "stable");
            Assert.False(source.VerifySavedManifest(null!, out string category, out _));
            Assert.Equal(UpdateErrorCategory.InvalidManifest, category);
        }

        [Fact]
        public async Task Adapter_ReturnsFailedResult_WhenDownloadRejected()
        {
            using TempDirectory temp = new();
            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Status(HttpStatusCode.NotFound));
            using HttpUpdatePackageSource source = CreateSource(handler, verifier);
            UpdateAsset asset = TestBytes.Asset(TestBytes.Random(32, 5));

            UpdateOperationResult result = await source.DownloadAsync(asset, temp.Combine("package.zip"), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Http, result.ErrorCategory);
        }
    }
}
