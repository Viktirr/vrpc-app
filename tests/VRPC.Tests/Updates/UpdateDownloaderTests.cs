using System.Net;
using VRPC.Updates;

namespace VRPC.Tests.Updates
{
    public sealed class UpdateDownloaderTests
    {
        private static readonly byte[] Payload = TestBytes.Random(4096, 7);

        [Fact]
        public async Task DownloadAsync_StreamsAndVerifiesPackage()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(Payload));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("updates", "package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("", result.ErrorCategory);
            Assert.True(File.Exists(destination));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".part"));
            Assert.Equal("VRPC/1.1.0", Assert.Single(handler.Requests).Header("User-Agent"));
        }

        [Fact]
        public async Task DownloadAsync_SucceedsWithoutContentLength()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Chunked(new NonSeekableStream(Payload)));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
        }

        [Fact]
        public async Task DownloadAsync_Fails_WhenContentLengthMismatches()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(Payload, declaredLength: Payload.Length + 9));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.SizeMismatch, result.ErrorCategory);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
        }

        [Fact]
        public async Task DownloadAsync_Fails_WhenBodyIsTruncated()
        {
            using TempDirectory temp = new();
            byte[] truncated = Payload[..^100];
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(truncated, declaredLength: Payload.Length));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.SizeMismatch, result.ErrorCategory);
            Assert.False(File.Exists(destination));
        }

        [Fact]
        public async Task DownloadAsync_Fails_WhenBodyExceedsSignedSize()
        {
            using TempDirectory temp = new();
            byte[] oversized = Payload.Concat(TestBytes.Random(32, 55)).ToArray();
            FakeHttpMessageHandler handler = new(() => TestHttp.Chunked(new NonSeekableStream(oversized)));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.SizeMismatch, result.ErrorCategory);
            Assert.False(File.Exists(destination + ".part"));
        }

        [Fact]
        public async Task DownloadAsync_Fails_OnHashMismatch()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(Payload));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");
            UpdateAsset asset = TestBytes.Asset(Payload, sha256: TestBytes.Sha256Hex(TestBytes.Random(Payload.Length, 123)));

            UpdateOperationResult result = await downloader.DownloadAsync(asset, destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.HashMismatch, result.ErrorCategory);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
        }

        [Fact]
        public async Task DownloadAsync_ReusesExistingVerifiedPackage()
        {
            using TempDirectory temp = new();
            string destination = temp.Combine("package.zip");
            await File.WriteAllBytesAsync(destination, Payload);
            FakeHttpMessageHandler handler = new(() => throw new InvalidOperationException("The network must not be used."));
            using UpdateDownloader downloader = new(handler, "1.1.0");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task DownloadAsync_ReplacesCorruptExistingPackage()
        {
            using TempDirectory temp = new();
            string destination = temp.Combine("package.zip");
            await File.WriteAllBytesAsync(destination, TestBytes.Random(Payload.Length, 77));
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(Payload));
            using UpdateDownloader downloader = new(handler, "1.1.0");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, handler.CallCount);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
        }

        [Fact]
        public async Task DownloadAsync_Fails_OnHttpError()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Status(HttpStatusCode.NotFound, "text/plain", "missing"));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Http, result.ErrorCategory);
            Assert.Contains("404", result.ErrorMessage);
            Assert.DoesNotContain("missing", result.ErrorMessage);
        }

        [Fact]
        public async Task DownloadAsync_FollowsHttpsRedirect()
        {
            using TempDirectory temp = new();
            int calls = 0;
            FakeHttpMessageHandler handler = new(() => ++calls == 1
                ? TestHttp.Redirect("https://objects.test/package.zip")
                : TestHttp.Bytes(Payload));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task DownloadAsync_RejectsNonHttpsRedirect()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Redirect("http://objects.test/package.zip"));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Http, result.ErrorCategory);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task DownloadAsync_RejectsTooManyRedirects()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Redirect("https://objects.test/loop"));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Http, result.ErrorCategory);
            Assert.Equal(6, handler.CallCount);
        }

        [Fact]
        public async Task DownloadAsync_Fails_WhenTransferStalls()
        {
            using TempDirectory temp = new();
            StallingStream stalled = new(Payload[..16]);
            FakeHttpMessageHandler handler = new(() => TestHttp.Chunked(stalled, declaredLength: Payload.Length));
            using UpdateDownloader downloader = new(handler, "1.1.0", TimeSpan.FromMilliseconds(150));
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Timeout, result.ErrorCategory);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
        }

        [Fact]
        public async Task DownloadAsync_PropagatesCancellation_AndDeletesPartFile()
        {
            using TempDirectory temp = new();
            StallingStream stalled = new(Payload[..16]);
            FakeHttpMessageHandler handler = new(() => TestHttp.Chunked(stalled, declaredLength: Payload.Length));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                downloader.DownloadAsync(TestBytes.Asset(Payload), destination, cancellation.Token));

            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
        }

        [Fact]
        public async Task DownloadAsync_Fails_WhenAssetUrlIsNotHttps()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(Payload));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");
            UpdateAsset asset = TestBytes.Asset(Payload, url: "http://github.com/Viktirr/vrpc-app/releases/download/v1.2.0/VRPC-1.2.0-win-x64.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(asset, destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task DownloadAsync_Fails_WhenAssetSizeIsInvalid()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(Payload));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            string destination = temp.Combine("package.zip");
            UpdateAsset asset = new()
            {
                Name = "VRPC-1.2.0-win-x64.zip",
                Url = TestHttp.AssetUrl,
                Size = 0,
                Sha256 = TestBytes.Sha256Hex(Payload)
            };

            UpdateOperationResult result = await downloader.DownloadAsync(asset, destination, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task DownloadAsync_ReportsProgress()
        {
            using TempDirectory temp = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Bytes(Payload));
            using UpdateDownloader downloader = new(handler, "1.1.0");
            CollectingProgress progress = new();
            downloader.Progress = progress;
            string destination = temp.Combine("package.zip");

            UpdateOperationResult result = await downloader.DownloadAsync(TestBytes.Asset(Payload), destination, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotEmpty(progress.Values);
            Assert.Equal(100, progress.Values[^1]);
        }
    }
}
