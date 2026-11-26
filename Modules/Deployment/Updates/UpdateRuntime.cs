using VRPC.Application;
using VRPC.Configuration;
using VRPC.Deployment.Install;
using VRPC.Logging;

namespace VRPC.Deployment.Updates
{
    // Decides whether this process is an installed host eligible for automatic
    // update checks, and starts the coordinator if it is.
    internal static class UpdateRuntime
    {
        internal static bool TryStart(Log log)
        {
            try
            {
                if (string.Equals(Environment.GetEnvironmentVariable("VRPC_DISABLE_UPDATER"), "1", StringComparison.Ordinal))
                {
                    log.Info("[Updater] Disabled by VRPC_DISABLE_UPDATER.");
                    return false;
                }

                if (!AppPaths.IsUnderApp(AppContext.BaseDirectory))
                {
                    log.Info("[Updater] Not an installed host; update checks are skipped.");
                    return false;
                }

                InstallState? state = InstallStateStore.Load();
                if (state is null)
                {
                    log.Warn("[Updater] No install state was found; update checks are skipped.");
                    return false;
                }

                string channel = string.IsNullOrWhiteSpace(state.Channel) ? UpdateContract.Channel : state.Channel;
                if (!string.Equals(channel, UpdateContract.Channel, StringComparison.Ordinal))
                {
                    log.Info($"[Updater] Channel '{channel}' is not supported; update checks are skipped.");
                    return false;
                }

                string currentVersion = ProductInfo.Version;
                if (!StrictVersion.IsValid(currentVersion))
                {
                    log.Warn($"[Updater] Running version '{currentVersion}' is not a strict numeric version; update checks are skipped.");
                    return false;
                }

                UpdateCoordinator coordinator = new UpdateCoordinator(
                    currentVersion,
                    channel,
                    CreateSource(currentVersion, channel),
                    new UpdateProcessLauncher(),
                    new UpdateStateStore(),
                    message => log.Warn(message),
                    message => log.Info(message),
                    () => Program.Shutdown(exit: true),
                    ResolveInitialDelay(),
                    () => VRPCSettings.settingsData.UnattendedUpdates);

                Program.Updater = coordinator;
                coordinator.Start(CancellationToken.None);
                log.Info($"[Updater] Automatic update checks started for version {currentVersion}.");
                return true;
            }
            catch (Exception exception)
            {
                log.Warn($"[Updater] The updater could not be started and update checks stay off: {exception.Message}");
                return false;
            }
        }

        // Production always talks to the pinned HTTPS feed. The loopback transport
        // seam exists only for the end-to-end release rehearsal: it rewrites the
        // transport to a local server while every URI-level HTTPS check, signature
        // check and content hash check still runs against the production rules.
        // It changes where bytes come from, never which bytes are trusted.
        private static IUpdatePackageSource CreateSource(string currentVersion, string channel)
        {
            if (TryGetLoopbackFeedTarget(out Uri origin))
            {
                return new HttpUpdatePackageSource(
                    new LoopbackFeedTransport(origin),
                    new Uri(UpdateContract.FeedUrl),
                    currentVersion,
                    UpdateSignatureVerifier.Default);
            }

            return new HttpUpdatePackageSource(currentVersion, channel);
        }

        private static bool TryGetLoopbackFeedTarget(out Uri origin)
        {
            origin = null!;
            string? value = Environment.GetEnvironmentVariable("VRPC_UPDATE_LOCAL_FEED");
            if (string.IsNullOrWhiteSpace(value)) { return false; }

            string candidate = value.Trim().TrimEnd('/');
            int separator = candidate.LastIndexOf(':');
            if (separator <= 0 || separator == candidate.Length - 1) { return false; }

            string host = candidate[..separator];
            if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal) &&
                !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!int.TryParse(candidate[(separator + 1)..], out int port) || port <= 0 || port > 65535) { return false; }
            origin = new Uri($"http://127.0.0.1:{port}/");
            return true;
        }

        private static TimeSpan? ResolveInitialDelay()
        {
            string? value = Environment.GetEnvironmentVariable("VRPC_UPDATER_INITIAL_DELAY_SECONDS");
            if (int.TryParse(value, out int seconds) && seconds >= 0 && seconds <= 600)
            {
                return TimeSpan.FromSeconds(seconds);
            }
            return null;
        }

        // Rewrites the transport target to 127.0.0.1 but keeps the original
        // (HTTPS) URI visible to the HTTP client so its redirect/scheme policy
        // runs unchanged. Test-only: enabled solely by VRPC_UPDATE_LOCAL_FEED.
        private sealed class LoopbackFeedTransport : DelegatingHandler
        {
            private readonly Uri _origin;

            public LoopbackFeedTransport(Uri origin)
                : base(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                _origin = origin;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Uri? original = request.RequestUri;
                HttpMethod method = request.Method;
                if (original is not null)
                {
                    request.RequestUri = new UriBuilder(_origin)
                    {
                        Path = original.AbsolutePath,
                        Query = original.Query
                    }.Uri;
                }

                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (original is not null)
                {
                    response.RequestMessage = new HttpRequestMessage(method, original);
                }
                return response;
            }
        }
    }
}
