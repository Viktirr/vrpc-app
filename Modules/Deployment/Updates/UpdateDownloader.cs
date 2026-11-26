using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VRPC.Deployment.Updates
{
    // Streams the signed update archive to disk while enforcing the signed length
    // and SHA-256. A file is never trusted because it is present or because a
    // previous run named it: bytes are always hashed before use.
    public sealed class UpdateDownloader : IDisposable
    {
        private const int MaxRedirects = 5;
        private const int BufferSize = 81920;
        private const int FreeSpaceMultiplier = 4;
        private static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(15);
        private static readonly Regex Sha256Pattern = new(UpdateContract.Sha256Pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly HttpMessageHandler? _ownedHandler;
        private readonly string _version;
        private readonly TimeSpan _inactivityTimeout;
        private int _lastReportedPercent = -1;

        public UpdateDownloader(string userAgentVersion)
            : this(CreateDefaultHandler(), userAgentVersion, null, ownsHandler: true) { }

        // Test seam: the caller owns the handler; a short inactivity timeout makes
        // stalled transfers testable.
        public UpdateDownloader(HttpMessageHandler handler, string userAgentVersion, TimeSpan? inactivityTimeout = null)
            : this(handler, userAgentVersion, inactivityTimeout, ownsHandler: false) { }

        private UpdateDownloader(HttpMessageHandler handler, string userAgentVersion, TimeSpan? inactivityTimeout, bool ownsHandler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!StrictVersion.IsValid(userAgentVersion))
            {
                throw new ArgumentException("The user agent version is not a strict numeric version.", nameof(userAgentVersion));
            }

            _inactivityTimeout = inactivityTimeout ?? DefaultInactivityTimeout;
            if (_inactivityTimeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(inactivityTimeout)); }

            _version = userAgentVersion;
            _ownedHandler = ownsHandler ? handler : null;
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }

        // Optional 0-100 progress for the popup. Reported at most once per percent.
        public IProgress<int>? Progress { get; set; }

        public async Task<UpdateOperationResult> DownloadAsync(UpdateAsset asset, string destinationArchivePath, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            if (string.IsNullOrWhiteSpace(destinationArchivePath))
            {
                throw new ArgumentException("A destination path is required.", nameof(destinationArchivePath));
            }
            if (!TryValidateAsset(asset, out string validationMessage))
            {
                return Failed(UpdateErrorCategory.InvalidManifest, validationMessage);
            }

            string destination = Path.GetFullPath(destinationArchivePath);
            string partPath = destination + ".part";
            bool completed = false;

            try
            {
                if (File.Exists(destination))
                {
                    if (VerifyFile(destination, asset))
                    {
                        completed = true;
                        return new UpdateOperationResult { Success = true };
                    }

                    TryDelete(destination);
                }

                string? parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }

                if (!HasEnoughFreeSpace(destination, asset.Size))
                {
                    return Failed(UpdateErrorCategory.Disk, "There is not enough free disk space to download the update.");
                }

                TryDelete(partPath);
                UpdateOperationResult result = await DownloadCoreAsync(asset, partPath, destination, cancellationToken).ConfigureAwait(false);
                completed = result.Success;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Failed(UpdateErrorCategory.Timeout, "The update download timed out.");
            }
            catch (HttpRequestException)
            {
                return Failed(UpdateErrorCategory.Network, "The update server could not be reached.");
            }
            catch (IOException)
            {
                return Failed(UpdateErrorCategory.Network, "The update package could not be downloaded.");
            }
            catch (UnauthorizedAccessException)
            {
                return Failed(UpdateErrorCategory.Disk, "The update package could not be saved.");
            }
            finally
            {
                if (!completed) { TryDelete(partPath); }
            }
        }

        public void Dispose()
        {
            _http.Dispose();
            _ownedHandler?.Dispose();
        }

        private async Task<UpdateOperationResult> DownloadCoreAsync(UpdateAsset asset, string partPath, string destination, CancellationToken cancellationToken)
        {
            using CancellationTokenSource headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headersTimeout.CancelAfter(HeadersTimeout);

            (HttpResponseMessage? response, string errorCategory, string errorMessage) = await SendAsync(asset.Url, headersTimeout.Token).ConfigureAwait(false);
            if (response is null) { return Failed(errorCategory, errorMessage); }

            using (response)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return Failed(UpdateErrorCategory.Http, $"The update server returned HTTP {(int)response.StatusCode}.");
                }

                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value != asset.Size)
                {
                    return Failed(UpdateErrorCategory.SizeMismatch, "The update package size does not match the signed size.");
                }

                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using CancellationTokenSource inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                byte[] buffer = new byte[BufferSize];
                long written = 0;

                await using (FileStream output = new(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        inactivity.CancelAfter(_inactivityTimeout);
                        int read;
                        try
                        {
                            read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), inactivity.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            return Failed(UpdateErrorCategory.Timeout, "The update download stalled.");
                        }

                        if (read == 0) { break; }

                        written += read;
                        if (written > asset.Size)
                        {
                            return Failed(UpdateErrorCategory.SizeMismatch, "The update package is larger than the signed size.");
                        }

                        hash.AppendData(buffer, 0, read);
                        try
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            return Failed(UpdateErrorCategory.Disk, "The update package could not be written to disk.");
                        }

                        ReportProgress(written, asset.Size);
                    }
                }

                if (written != asset.Size)
                {
                    return Failed(UpdateErrorCategory.SizeMismatch, "The update package is incomplete.");
                }

                byte[] actualHash = hash.GetHashAndReset();
                byte[] expectedHash = Convert.FromHexString(asset.Sha256);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    return Failed(UpdateErrorCategory.HashMismatch, "The update package failed hash verification.");
                }

                try
                {
                    File.Move(partPath, destination, overwrite: false);
                }
                catch (IOException)
                {
                    if (File.Exists(destination) && VerifyFile(destination, asset))
                    {
                        return new UpdateOperationResult { Success = true };
                    }
                    return Failed(UpdateErrorCategory.Disk, "The downloaded update package could not be finalized.");
                }
                catch (UnauthorizedAccessException)
                {
                    return Failed(UpdateErrorCategory.Disk, "The downloaded update package could not be finalized.");
                }

                return new UpdateOperationResult { Success = true };
            }
        }

        private async Task<(HttpResponseMessage? Response, string Category, string Message)> SendAsync(string url, CancellationToken cancellationToken)
        {
            Uri? current = TryGetHttpsUri(url);
            if (current is null)
            {
                return (null, UpdateErrorCategory.InvalidManifest, "The update asset URL is not a secure HTTPS URL.");
            }

            for (int hop = 0; ; hop++)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", "VRPC/" + _version);

                HttpResponseMessage response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                Uri? finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is not null && !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    return (null, UpdateErrorCategory.Http, "The update download redirected to an insecure address.");
                }

                if (!IsRedirect(response.StatusCode)) { return (response, "", ""); }

                Uri? location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    return (null, UpdateErrorCategory.Http, "The update download returned an incomplete redirect.");
                }
                if (hop >= MaxRedirects)
                {
                    return (null, UpdateErrorCategory.Http, "The update download redirected too many times.");
                }

                Uri? next;
                try
                {
                    next = location.IsAbsoluteUri ? location : new Uri(current, location);
                }
                catch (UriFormatException)
                {
                    return (null, UpdateErrorCategory.Http, "The update download returned an incomplete redirect.");
                }

                if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, UpdateErrorCategory.Http, "The update download redirected to an insecure address.");
                }

                current = next;
            }
        }

        private static UpdateOperationResult Failed(string category, string message)
            => new() { Success = false, ErrorCategory = category, ErrorMessage = message };

        private void ReportProgress(long written, long total)
        {
            IProgress<int>? progress = Progress;
            if (progress is null || total <= 0) { return; }

            int percent = (int)Math.Clamp(written * 100 / total, 0, 100);
            if (percent == _lastReportedPercent) { return; }
            _lastReportedPercent = percent;
            progress.Report(percent);
        }

        private static bool TryValidateAsset(UpdateAsset asset, out string message)
        {
            message = "";
            if (asset.Size <= 0 || asset.Size > UpdateContract.MaxPackageBytes)
            {
                message = "The update package size in the manifest is invalid.";
                return false;
            }
            if (string.IsNullOrEmpty(asset.Sha256) || !Sha256Pattern.IsMatch(asset.Sha256))
            {
                message = "The update package hash in the manifest is invalid.";
                return false;
            }
            if (TryGetHttpsUri(asset.Url) is null)
            {
                message = "The update asset URL is not a secure HTTPS URL.";
                return false;
            }
            return true;
        }

        private static Uri? TryGetHttpsUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return null; }
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) { return null; }
            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? uri : null;
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
            => (int)statusCode is 301 or 302 or 303 or 307 or 308;

        private static bool VerifyFile(string path, UpdateAsset asset)
        {
            try
            {
                FileInfo info = new(path);
                if (!info.Exists || info.Length != asset.Size) { return false; }

                using FileStream stream = File.OpenRead(path);
                byte[] actual = SHA256.HashData(stream);
                return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(asset.Sha256));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                return false;
            }
        }

        private static bool HasEnoughFreeSpace(string destinationPath, long packageSize)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
                if (string.IsNullOrEmpty(root)) { return true; }

                DriveInfo drive = new(root);
                if (!drive.IsReady) { return true; }

                long required = packageSize * FreeSpaceMultiplier;
                return drive.AvailableFreeSpace >= required;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return true;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch { }
        }

        private static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None
        };
    }
}
