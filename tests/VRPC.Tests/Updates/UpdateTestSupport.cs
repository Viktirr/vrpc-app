using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VRPC.Updates;

namespace VRPC.Tests.Updates
{
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrpc-updater-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts)
            => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
            }
            catch { }
        }
    }

    internal sealed class RecordedRequest
    {
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        public Uri? RequestUri { get; init; }
        public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name)
            => Headers.TryGetValue(name, out string[]? values) ? string.Join(", ", values) : null;

        public bool HasHeader(string name) => Headers.ContainsKey(name);
    }

    internal sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> _responder;

        public FakeHttpMessageHandler(Func<HttpResponseMessage> responder)
            : this((_, _) => Task.FromResult(responder())) { }

        public FakeHttpMessageHandler(Func<RecordedRequest, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request))) { }

        public FakeHttpMessageHandler(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<RecordedRequest> Requests { get; } = new();

        public int CallCount => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordedRequest recorded = new() { Method = request.Method, RequestUri = request.RequestUri };
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                recorded.Headers[header.Key] = header.Value.ToArray();
            }

            Requests.Add(recorded);
            return _responder(recorded, cancellationToken);
        }
    }

    internal static class TestHttp
    {
        public const string FeedUrl = "https://feed.test/vrpc/update-manifest?channel=stable&runtime=win-x64";
        public const string AssetUrl = "https://github.com/Viktirr/vrpc-app/releases/download/v1.2.0/VRPC-1.2.0-win-x64.zip";

        public static HttpResponseMessage Json(string json, string? etag = null)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            if (etag is not null) { response.Headers.ETag = new EntityTagHeaderValue("\"" + etag + "\""); }
            return response;
        }

        public static HttpResponseMessage NotModified(string? etag = null)
        {
            HttpResponseMessage response = new(HttpStatusCode.NotModified);
            if (etag is not null) { response.Headers.ETag = new EntityTagHeaderValue("\"" + etag + "\""); }
            return response;
        }

        public static HttpResponseMessage Status(HttpStatusCode statusCode, string mediaType = "application/json", string body = "{}")
        {
            HttpResponseMessage response = new(statusCode) { Content = new StringContent(body, Encoding.UTF8) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return response;
        }

        public static HttpResponseMessage Redirect(string location, HttpStatusCode statusCode = HttpStatusCode.Found)
        {
            HttpResponseMessage response = new(statusCode);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        }

        public static HttpResponseMessage Bytes(byte[] data, long? declaredLength = null)
        {
            StreamContent content = new(new MemoryStream(data));
            if (declaredLength.HasValue) { content.Headers.ContentLength = declaredLength.Value; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        public static HttpResponseMessage Chunked(Stream stream, string mediaType = "application/octet-stream", long? declaredLength = null)
        {
            StreamContent content = new(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            if (declaredLength.HasValue) { content.Headers.ContentLength = declaredLength.Value; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    internal static class TestBytes
    {
        public static byte[] Random(int length, int seed)
        {
            byte[] data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        public static UpdateAsset Asset(byte[] content, string? url = null, string? sha256 = null) => new()
        {
            Name = "VRPC-1.2.0-win-x64.zip",
            Url = url ?? TestHttp.AssetUrl,
            Size = content.Length,
            Sha256 = sha256 ?? Sha256Hex(content)
        };
    }

    internal sealed class FakeUpdateManifestVerifier : IUpdateManifestVerifier
    {
        public bool VerifyResult { get; set; } = true;
        public string ErrorCategory { get; set; } = UpdateErrorCategory.InvalidSignature;
        public string ErrorMessage { get; set; } = "Signature rejected by the test verifier.";
        public int Calls { get; private set; }
        public UpdateManifest? LastManifest { get; private set; }

        public bool TryVerify(UpdateManifest manifest, out UpdateVerificationError error)
        {
            Calls++;
            LastManifest = manifest;
            if (VerifyResult)
            {
                error = new UpdateVerificationError { Category = "", Message = "" };
                return true;
            }

            error = new UpdateVerificationError { Category = ErrorCategory, Message = ErrorMessage };
            return false;
        }
    }

    internal static class ManifestFixture
    {
        public static string Envelope(
            string version = "1.2.0",
            string product = "VRPC",
            string channel = "stable",
            string runtime = "win-x64",
            long size = 36829330,
            string? sha256 = null,
            int schema = 1)
        {
            string tag = "v" + version;
            string name = $"VRPC-{version}-win-x64.zip";
            var document = new
            {
                schema,
                payload = new
                {
                    product,
                    version,
                    channel,
                    runtime,
                    releaseTag = tag,
                    asset = new
                    {
                        name,
                        url = $"https://github.com/Viktirr/vrpc-app/releases/download/{tag}/{name}",
                        size,
                        sha256 = sha256 ?? new string('a', 64)
                    },
                    notesUrl = $"https://github.com/Viktirr/vrpc-app/releases/tag/{tag}",
                    publishedAtUtc = "2026-11-15T00:00:00Z"
                },
                signature = new
                {
                    algorithm = "ECDSA-P256-SHA256",
                    keyId = "test-key-1",
                    value = "c2lnbmF0dXJl"
                }
            };

            return JsonSerializer.Serialize(document);
        }
    }

    internal sealed class PackageFixture
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedFromManifest { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> HashOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Version { get; set; } = "1.2.0";
        public string Product { get; set; } = "VRPC";

        public PackageFixture Add(string relativePath, byte[] content)
        {
            Files[relativePath] = content;
            return this;
        }

        public PackageFixture AddExecutables()
        {
            Add("VRPC.exe", TestBytes.Random(512, 1));
            Add("vrpc-installer-ui.exe", TestBytes.Random(256, 2));
            Add(System.IO.Path.Combine("runtime", "discordrpc.dll"), TestBytes.Random(128, 3));
            return this;
        }

        public byte[] BuildPayloadJson()
        {
            var files = Files
                .Where(pair => !ExcludedFromManifest.Contains(pair.Key))
                .Select(pair => new
                {
                    path = pair.Key,
                    size = (long)pair.Value.Length,
                    sha256 = HashOverrides.TryGetValue(pair.Key, out string? hash) ? hash : TestBytes.Sha256Hex(pair.Value)
                })
                .ToArray();

            var manifest = new
            {
                schema = 1,
                product = Product,
                version = Version,
                channel = "stable",
                entryPoint = "VRPC.exe",
                files
            };

            return JsonSerializer.SerializeToUtf8Bytes(manifest);
        }

        public string WriteZip(string zipPath)
        {
            using TempDirectory source = new();
            foreach (KeyValuePair<string, byte[]> pair in Files)
            {
                string targetPath = System.IO.Path.Combine(source.Path, pair.Key);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
                File.WriteAllBytes(targetPath, pair.Value);
            }

            File.WriteAllBytes(System.IO.Path.Combine(source.Path, "payload.json"), BuildPayloadJson());
            ZipFile.CreateFromDirectory(source.Path, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
    }

    internal sealed record ZipEntrySpec(string Name, byte[] Content, int? ExternalAttributes = null);

    internal static class ZipWriter
    {
        public static void Create(string path, params ZipEntrySpec[] entries)
        {
            using FileStream stream = File.Create(path);
            using ZipArchive archive = new(stream, ZipArchiveMode.Create);
            foreach (ZipEntrySpec spec in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(spec.Name);
                if (spec.ExternalAttributes.HasValue) { entry.ExternalAttributes = spec.ExternalAttributes.Value; }
                using Stream entryStream = entry.Open();
                entryStream.Write(spec.Content);
            }
        }
    }

    internal sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class StallingStream : Stream
    {
        private readonly byte[] _preamble;
        private bool _sentPreamble;

        public StallingStream(byte[] preamble) => _preamble = preamble;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("This stream only supports asynchronous reads.");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentPreamble && _preamble.Length > 0)
            {
                _sentPreamble = true;
                int count = Math.Min(_preamble.Length, buffer.Length);
                _preamble.AsSpan(0, count).CopyTo(buffer.Span);
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class CollectingProgress : IProgress<int>
    {
        public List<int> Values { get; } = new();

        public void Report(int value) => Values.Add(value);
    }
}
