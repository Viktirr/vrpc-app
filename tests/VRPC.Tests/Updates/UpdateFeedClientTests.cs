using System.Net;
using VRPC.Updates;

namespace VRPC.Tests.Updates
{
    public sealed class UpdateFeedClientTests
    {
        private static UpdateFeedClient CreateClient(FakeHttpMessageHandler handler, FakeUpdateManifestVerifier? verifier = null, string currentVersion = "1.1.0", TimeSpan? timeout = null)
            => new(handler, new Uri(TestHttp.FeedUrl), currentVersion, verifier ?? new FakeUpdateManifestVerifier(), timeout);

        [Fact]
        public async Task CheckAsync_ReturnsUpdateAvailable_ForNewerVerifiedManifest()
        {
            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope("1.2.0"), etag: "abc123"));
            using UpdateFeedClient client = CreateClient(handler, verifier);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
            Assert.NotNull(result.Manifest);
            Assert.Equal("1.2.0", result.Manifest!.Payload.Version);
            Assert.Equal("\"abc123\"", result.ETag);
            Assert.Equal(1, verifier.Calls);

            RecordedRequest request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(TestHttp.FeedUrl, request.RequestUri!.ToString());
            Assert.Contains("application/json", request.Header("Accept") ?? "");
            Assert.Equal("VRPC/1.1.0", request.Header("User-Agent"));
            Assert.False(request.HasHeader("If-None-Match"));
            Assert.False(request.HasHeader("Authorization"));
            Assert.False(request.HasHeader("Cookie"));
        }

        [Fact]
        public async Task CheckAsync_SendsIfNoneMatch_AndReturnsNotModified()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.NotModified("abc"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync("\"abc\"", CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.NotModified, result.Outcome);
            Assert.Null(result.Manifest);
            Assert.Equal("\"abc\"", result.ETag);
            Assert.Equal("\"abc\"", Assert.Single(handler.Requests).Header("If-None-Match"));
        }

        [Theory]
        [InlineData("1.1.0")]
        [InlineData("1.0.9")]
        public async Task CheckAsync_IsUpToDate_WhenCandidateIsNotStrictlyNewer(string candidateVersion)
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope(candidateVersion)));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
            Assert.NotNull(result.Manifest);
        }

        [Fact]
        public async Task CheckAsync_Fails_OnInvalidJson()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Json("{ this is not json"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
            Assert.DoesNotContain("http", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckAsync_Fails_OnNonJsonContentType()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Status(HttpStatusCode.OK, "text/html", ManifestFixture.Envelope()));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
        }

        [Fact]
        public async Task CheckAsync_Fails_WhenVerifierRejectsManifest()
        {
            FakeUpdateManifestVerifier verifier = new()
            {
                VerifyResult = false,
                ErrorCategory = UpdateErrorCategory.InvalidSignature,
                ErrorMessage = "The manifest signature is invalid."
            };
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));
            using UpdateFeedClient client = CreateClient(handler, verifier);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.InvalidSignature, result.ErrorCategory);
            Assert.Equal("The manifest signature is invalid.", result.ErrorMessage);
        }

        [Fact]
        public async Task CheckAsync_Fails_OnHttpError()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Status(HttpStatusCode.ServiceUnavailable, body: "upstream down"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.Http, result.ErrorCategory);
            Assert.Contains("503", result.ErrorMessage);
            Assert.DoesNotContain("upstream down", result.ErrorMessage);
        }

        [Fact]
        public async Task CheckAsync_FollowsHttpsRedirect()
        {
            int calls = 0;
            FakeHttpMessageHandler handler = new(() => ++calls == 1
                ? TestHttp.Redirect("https://feed.test/manifest-v2")
                : TestHttp.Json(ManifestFixture.Envelope("1.2.0"), etag: "e2"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
            Assert.Equal("\"e2\"", result.ETag);
            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task CheckAsync_RejectsNonHttpsRedirect()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Redirect("http://evil.test/manifest"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.Http, result.ErrorCategory);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task CheckAsync_RejectsTooManyRedirects()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Redirect("https://feed.test/loop"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.Http, result.ErrorCategory);
            Assert.Equal(4, handler.CallCount);
        }

        [Fact]
        public async Task CheckAsync_Fails_WhenDeclaredBodyIsTooLarge()
        {
            FakeHttpMessageHandler handler = new(() =>
            {
                HttpResponseMessage response = TestHttp.Bytes(new byte[64], declaredLength: UpdateContract.MaxManifestBytes + 1);
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return response;
            });
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
            Assert.Contains("larger", result.ErrorMessage);
        }

        [Fact]
        public async Task CheckAsync_Fails_WhenStreamedBodyIsTooLarge()
        {
            FakeHttpMessageHandler handler = new(() =>
                TestHttp.Chunked(new NonSeekableStream(new byte[UpdateContract.MaxManifestBytes + 1]), "application/json"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
        }

        [Fact]
        public async Task CheckAsync_Fails_OnTimeout()
        {
            FakeHttpMessageHandler handler = new((_, cancellationToken) => WaitForeverAsync(cancellationToken));
            using UpdateFeedClient client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(100));

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.Timeout, result.ErrorCategory);
        }

        [Fact]
        public async Task CheckAsync_PropagatesCallerCancellation()
        {
            FakeHttpMessageHandler handler = new((_, cancellationToken) => WaitForeverAsync(cancellationToken));
            using UpdateFeedClient client = CreateClient(handler);
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(null, cancellation.Token));
        }

        [Fact]
        public async Task CheckAsync_Fails_WhenVerifiedManifestVersionIsInvalid()
        {
            FakeUpdateManifestVerifier verifier = new();
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope("1.2")));
            using UpdateFeedClient client = CreateClient(handler, verifier);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
            Assert.Equal(1, verifier.Calls);
        }

        [Fact]
        public async Task CheckAsync_Fails_OnNullJsonDocument()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Json("null"));
            using UpdateFeedClient client = CreateClient(handler);

            UpdateCheckResult result = await client.CheckAsync(null, CancellationToken.None);

            Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
            Assert.Equal(UpdateErrorCategory.InvalidManifest, result.ErrorCategory);
        }

        [Fact]
        public void Constructor_RejectsInsecureFeedUrl()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));

            Assert.Throws<ArgumentException>(() =>
                new UpdateFeedClient(handler, new Uri("http://api.viktir.com/vrpc/update-manifest"), "1.1.0", new FakeUpdateManifestVerifier()));
        }

        [Fact]
        public void Constructor_RejectsInvalidCurrentVersion()
        {
            FakeHttpMessageHandler handler = new(() => TestHttp.Json(ManifestFixture.Envelope()));

            Assert.Throws<ArgumentException>(() =>
                new UpdateFeedClient(handler, new Uri(TestHttp.FeedUrl), "1.1", new FakeUpdateManifestVerifier()));
        }

        private static async Task<HttpResponseMessage> WaitForeverAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
