using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VRPC.Deployment.Updates
{
    // Fetches the signed update manifest over HTTPS and authenticates it before
    // any value is trusted. The caller owns the injected handler; a production
    // handler must not follow redirects or store cookies on its own (this class
    // also disables both defensively when it recognizes the handler type).
    public sealed class UpdateFeedClient : IDisposable
    {
        private const int MaxRedirects = 3;
        private const string JsonMediaType = "application/json";
        private const int BodyBufferSize = 8 * 1024;
        private static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(10);

        // Strict reader for the untrusted envelope. The verifier re-serializes the
        // payload with JCS, so this reader only has to reject ambiguous documents.
        private static readonly JsonSerializerOptions ManifestJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 32
        };

        private readonly HttpClient _http;
        private readonly Uri _feedUrl;
        private readonly string _currentVersion;
        private readonly IUpdateManifestVerifier _verifier;
        private readonly TimeSpan _metadataTimeout;

        public UpdateFeedClient(HttpMessageHandler handler, Uri feedUrl, string currentVersion, IUpdateManifestVerifier verifier)
            : this(handler, feedUrl, currentVersion, verifier, null) { }

        // metadataTimeout is a test seam; production uses the 10 second default.
        public UpdateFeedClient(HttpMessageHandler handler, Uri feedUrl, string currentVersion, IUpdateManifestVerifier verifier, TimeSpan? metadataTimeout)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(feedUrl);
            ArgumentNullException.ThrowIfNull(verifier);
            if (!feedUrl.IsAbsoluteUri) { throw new ArgumentException("The update feed URL must be absolute.", nameof(feedUrl)); }
            if (!string.Equals(feedUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The update feed URL must use HTTPS.", nameof(feedUrl));
            }
            if (!StrictVersion.IsValid(currentVersion)) { throw new ArgumentException("The current version is not a strict numeric version.", nameof(currentVersion)); }

            _metadataTimeout = metadataTimeout ?? DefaultMetadataTimeout;
            if (_metadataTimeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(metadataTimeout)); }

            DisableAutomaticRedirectsAndCookies(handler);
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            _feedUrl = feedUrl;
            _currentVersion = currentVersion;
            _verifier = verifier;
        }

        public async Task<UpdateCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_metadataTimeout);

            try
            {
                (HttpResponseMessage? response, string errorCategory, string errorMessage) = await SendAsync(etag, timeout.Token).ConfigureAwait(false);
                if (response is null) { return Failed(errorCategory, errorMessage); }

                using (response)
                {
                    string responseEtag = response.Headers.ETag?.ToString() ?? "";

                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        return new UpdateCheckResult
                        {
                            Outcome = UpdateCheckOutcome.NotModified,
                            ETag = responseEtag.Length > 0 ? responseEtag : etag ?? ""
                        };
                    }

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return Failed(UpdateErrorCategory.Http, $"The update feed returned HTTP {(int)response.StatusCode}.");
                    }

                    string? mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.Equals(mediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase))
                    {
                        return Failed(UpdateErrorCategory.InvalidManifest, "The update feed did not return JSON.");
                    }

                    byte[]? body = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
                    if (body is null)
                    {
                        return Failed(UpdateErrorCategory.InvalidManifest, "The update feed response was larger than allowed.");
                    }

                    UpdateManifest? manifest;
                    try
                    {
                        manifest = UpdateManifestJson.Deserialize(body);
                    }
                    catch (JsonException)
                    {
                        return Failed(UpdateErrorCategory.InvalidManifest, "The update feed response is not a valid update manifest.");
                    }

                    if (manifest is null)
                    {
                        return Failed(UpdateErrorCategory.InvalidManifest, "The update feed response is not a valid update manifest.");
                    }

                    if (!_verifier.TryVerify(manifest, out UpdateVerificationError verificationError))
                    {
                        string category = string.IsNullOrWhiteSpace(verificationError.Category)
                            ? UpdateErrorCategory.InvalidSignature
                            : verificationError.Category;
                        string message = string.IsNullOrWhiteSpace(verificationError.Message)
                            ? "The update manifest signature could not be verified."
                            : verificationError.Message;
                        return Failed(category, message);
                    }

                    string candidateVersion = manifest.Payload.Version;
                    if (!StrictVersion.IsValid(candidateVersion))
                    {
                        return Failed(UpdateErrorCategory.InvalidManifest, "The update manifest contains an invalid version.");
                    }

                    UpdateCheckOutcome outcome = StrictVersion.IsStrictlyNewer(candidateVersion, _currentVersion)
                        ? UpdateCheckOutcome.UpdateAvailable
                        : UpdateCheckOutcome.UpToDate;

                    return new UpdateCheckResult
                    {
                        Outcome = outcome,
                        Manifest = manifest,
                        ETag = responseEtag
                    };
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed(UpdateErrorCategory.Timeout, "The update feed did not respond in time.");
            }
            catch (HttpRequestException)
            {
                return Failed(UpdateErrorCategory.Network, "The update feed could not be reached.");
            }
            catch (IOException)
            {
                return Failed(UpdateErrorCategory.Network, "The update feed could not be read.");
            }
        }

        public void Dispose() => _http.Dispose();

        private async Task<(HttpResponseMessage? Response, string ErrorCategory, string ErrorMessage)> SendAsync(string? etag, CancellationToken cancellationToken)
        {
            Uri current = _feedUrl;
            for (int hop = 0; ; hop++)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, current);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
                request.Headers.TryAddWithoutValidation("User-Agent", "VRPC/" + _currentVersion);
                if (hop == 0 && !string.IsNullOrWhiteSpace(etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                HttpResponseMessage response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                Uri? finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is not null && !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    return (null, UpdateErrorCategory.Http, "The update feed redirected to an insecure address.");
                }

                if (!IsRedirect(response.StatusCode)) { return (response, "", ""); }

                Uri? location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    return (null, UpdateErrorCategory.Http, "The update feed returned an incomplete redirect.");
                }
                if (hop >= MaxRedirects)
                {
                    return (null, UpdateErrorCategory.Http, "The update feed redirected too many times.");
                }

                Uri? next;
                try
                {
                    next = location.IsAbsoluteUri ? location : new Uri(current, location);
                }
                catch (UriFormatException)
                {
                    return (null, UpdateErrorCategory.Http, "The update feed returned an incomplete redirect.");
                }

                if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, UpdateErrorCategory.Http, "The update feed redirected to an insecure address.");
                }

                current = next;
            }
        }

        private static async Task<byte[]?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
        {
            long? declaredLength = content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > UpdateContract.MaxManifestBytes) { return null; }

            await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[BodyBufferSize];
            while (true)
            {
                int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) { return buffer.ToArray(); }
                if (buffer.Length + read > UpdateContract.MaxManifestBytes) { return null; }
                buffer.Write(chunk, 0, read);
            }
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
            => (int)statusCode is 301 or 302 or 303 or 307 or 308;

        private static void DisableAutomaticRedirectsAndCookies(HttpMessageHandler handler)
        {
            try
            {
                if (handler is HttpClientHandler clientHandler)
                {
                    clientHandler.AllowAutoRedirect = false;
                    clientHandler.UseCookies = false;
                }
                else if (handler is SocketsHttpHandler socketsHandler)
                {
                    socketsHandler.AllowAutoRedirect = false;
                    socketsHandler.UseCookies = false;
                }
            }
            catch (InvalidOperationException)
            {
                // The caller already used the handler; the final-URI check below
                // still rejects a non-HTTPS response.
            }
        }

        private static UpdateCheckResult Failed(string category, string message)
            => new() { Outcome = UpdateCheckOutcome.Failed, ErrorCategory = category, ErrorMessage = message };
    }

    // Production IUpdatePackageSource: composes the feed client, the verified
    // streaming downloader and the safe extractor for the coordinator. The
    // coordinator still re-verifies the saved manifest and the extracted
    // package before it launches anything.
    public sealed class HttpUpdatePackageSource : IUpdatePackageSource, IDisposable
    {
        private readonly UpdateFeedClient _feed;
        private readonly UpdateDownloader _downloader;
        private readonly UpdatePackageExtractor _extractor;
        private readonly IUpdateManifestVerifier _verifier;
        private readonly HttpMessageHandler? _ownedHandler;
        private readonly string _currentVersion;
        private readonly string _channel;

        // Production wiring: stable channel and the pinned production public keys.
        public HttpUpdatePackageSource(string currentVersion, string channel)
            : this(CreateHandler(), new Uri(UpdateContract.FeedUrl), currentVersion, NormalizeChannel(channel), UpdateSignatureVerifier.Default, ownsHandler: true) { }

        // Test seam: the caller owns the handler and supplies the verifier.
        public HttpUpdatePackageSource(HttpMessageHandler handler, Uri feedUrl, string currentVersion, IUpdateManifestVerifier verifier)
            : this(handler, feedUrl, currentVersion, UpdateContract.Channel, verifier, ownsHandler: false) { }

        public HttpUpdatePackageSource(HttpMessageHandler handler, Uri feedUrl, string currentVersion, string channel, IUpdateManifestVerifier verifier)
            : this(handler, feedUrl, currentVersion, NormalizeChannel(channel), verifier, ownsHandler: false) { }

        private HttpUpdatePackageSource(HttpMessageHandler handler, Uri feedUrl, string currentVersion, string channel, IUpdateManifestVerifier verifier, bool ownsHandler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(verifier);
            _currentVersion = currentVersion;
            _channel = channel;
            _verifier = verifier;
            _ownedHandler = ownsHandler ? handler : null;
            _feed = new UpdateFeedClient(handler, feedUrl, currentVersion, verifier);
            _downloader = new UpdateDownloader(handler, currentVersion);
            _extractor = new UpdatePackageExtractor();
        }

        public Task<UpdateCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken)
            => _feed.CheckAsync(etag, cancellationToken);

        public Task<UpdateOperationResult> DownloadAsync(UpdateAsset asset, string destinationArchivePath, CancellationToken cancellationToken)
            => _downloader.DownloadAsync(asset, destinationArchivePath, cancellationToken);

        public Task<UpdateOperationResult> ExtractAsync(string archivePath, string destinationDirectory, string expectedVersion, CancellationToken cancellationToken)
            => _extractor.ExtractAsync(archivePath, destinationDirectory, expectedVersion, cancellationToken);

        // Re-authenticates the envelope saved next to a downloaded package before
        // any launch. The injected verifier owns signature/key policy (it fails
        // closed on unknown key IDs and algorithms); this method additionally
        // checks identity fields and the version relation.
        public bool VerifySavedManifest(UpdateManifest manifest, out string errorCategory, out string errorMessage)
        {
            errorCategory = UpdateErrorCategory.InvalidManifest;
            errorMessage = "";

            if (manifest is null)
            {
                errorMessage = "The saved update manifest is missing.";
                return false;
            }

            UpdatePayload? payload = manifest.Payload;
            if (manifest.Schema != UpdateContract.Schema || payload is null || payload.Asset is null ||
                !string.Equals(payload.Product, UpdateContract.Product, StringComparison.Ordinal) ||
                !string.Equals(payload.Channel, _channel, StringComparison.Ordinal) ||
                !string.Equals(payload.Runtime, UpdateContract.Runtime, StringComparison.Ordinal))
            {
                errorMessage = "The saved update manifest does not describe this product, channel or runtime.";
                return false;
            }

            if (!_verifier.TryVerify(manifest, out UpdateVerificationError verificationError))
            {
                errorCategory = string.IsNullOrWhiteSpace(verificationError.Category)
                    ? UpdateErrorCategory.InvalidSignature
                    : verificationError.Category;
                errorMessage = string.IsNullOrWhiteSpace(verificationError.Message)
                    ? "The saved update manifest did not verify."
                    : verificationError.Message;
                return false;
            }

            if (!StrictVersion.IsValid(_currentVersion) || !StrictVersion.IsValid(payload.Version) ||
                !StrictVersion.IsStrictlyNewer(payload.Version, _currentVersion))
            {
                errorCategory = UpdateErrorCategory.PayloadMismatch;
                errorMessage = "The saved update manifest is not newer than the installed version.";
                return false;
            }

            errorCategory = "";
            errorMessage = "";
            return true;
        }

        public bool VerifyPackageDirectory(string packageDirectory, string expectedVersion, out string errorCategory, out string errorMessage)
            => UpdatePackageExtractor.VerifyPackageDirectory(packageDirectory, expectedVersion, out errorCategory, out errorMessage);

        public void Dispose()
        {
            _feed.Dispose();
            _downloader.Dispose();
            _ownedHandler?.Dispose();
        }

        private static string NormalizeChannel(string? channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) { return UpdateContract.Channel; }
            if (!string.Equals(channel, UpdateContract.Channel, StringComparison.Ordinal))
            {
                throw new ArgumentException("Only the stable update channel is supported.", nameof(channel));
            }
            return channel;
        }

        private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None
        };
    }
}
