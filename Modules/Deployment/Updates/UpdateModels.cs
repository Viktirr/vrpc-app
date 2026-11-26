using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRPC.Deployment.Updates
{
    // Contract for the signed discovery manifest. Serialization is camelCase and
    // the payload object is the JCS-signature subject; never add security-relevant
    // fields outside the payload.
    public static class UpdateContract
    {
        public const int Schema = 1;
        public const string Product = "VRPC";
        public const string Channel = "stable";
        public const string Runtime = "win-x64";
        public const string Algorithm = "ECDSA-P256-SHA256";
        public const string FeedUrl = "https://api.viktir.com/products/vrpc/update-manifest?channel=stable&runtime=win-x64";
        public const int MaxManifestBytes = 64 * 1024;
        public const long MaxPackageBytes = 500_000_000;
        public const string VersionPattern = @"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$";
        public const string Sha256Pattern = "^[0-9a-f]{64}$";

        public static string AssetName(string version) => $"VRPC-{version}-win-x64.zip";
        public static string ReleaseTag(string version) => $"v{version}";
    }

    public sealed class UpdateAsset
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public sealed class UpdatePayload
    {
        public string Product { get; set; } = "";
        public string Version { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Runtime { get; set; } = "";
        public string ReleaseTag { get; set; } = "";
        public UpdateAsset Asset { get; set; } = new();
        public string NotesUrl { get; set; } = "";
        public string PublishedAtUtc { get; set; } = "";
    }

    public sealed class UpdateSignature
    {
        public string Algorithm { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public sealed class UpdateManifest
    {
        public int Schema { get; set; }
        public UpdatePayload Payload { get; set; } = new();
        public UpdateSignature Signature { get; set; } = new();

        // The exact payload object received on the wire; the JCS signature is
        // verified over this when present so unknown fields cannot ride along
        // unauthenticated.
        [JsonIgnore]
        public JsonElement? RawPayload { get; set; }
    }

    public static class UpdatePhase
    {
        public const string Idle = "idle";
        public const string Checking = "checking";
        public const string Downloading = "downloading";
        public const string Ready = "ready";
        public const string Installing = "installing";
        public const string Failed = "failed";
        public const string NotInstalled = "not_installed";
        public const string Unsupported = "unsupported";
        public const string Disabled = "disabled";
    }

    public enum UpdateCheckOutcome
    {
        NotModified,
        UpToDate,
        UpdateAvailable,
        Failed
    }

    public sealed class UpdateCheckResult
    {
        public UpdateCheckOutcome Outcome { get; set; }
        public UpdateManifest? Manifest { get; set; }
        public string ETag { get; set; } = "";
        public string ErrorCategory { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }

    // Persisted in state/updater-state.json. Never reuse state/update.json (the
    // installer's transaction marker).
    public sealed class UpdaterState
    {
        public int Schema { get; set; } = 1;
        public string Channel { get; set; } = UpdateContract.Channel;
        public string LastAttemptUtc { get; set; } = "";
        public string LastCheckUtc { get; set; } = "";
        public string NextAllowedAttemptUtc { get; set; } = "";
        public int ConsecutiveFailures { get; set; }
        public string LastErrorCategory { get; set; } = "";
        public string LastErrorMessage { get; set; } = "";
        public string LastErrorUtc { get; set; } = "";
        public string AvailableVersion { get; set; } = "";
        public string AvailableNotesUrl { get; set; } = "";
        public string ReadyVersion { get; set; } = "";
        public string ReadyPackageDir { get; set; } = "";
        public string LastNotifiedVersion { get; set; } = "";
        public string ManifestEtag { get; set; } = "";
        public string CachedManifestVersion { get; set; } = "";
        public string InstalledTargetVersion { get; set; } = "";
        public string LastLaunchUtc { get; set; } = "";

        // Opt-in unattended installation bookkeeping. The version resets the
        // counters; the timestamp enforces a delay between attempts.
        public string UnattendedVersion { get; set; } = "";
        public int UnattendedAttempts { get; set; }
        public string LastUnattendedUtc { get; set; } = "";
    }

    // What the extension popup receives through Native Messaging.
    public sealed class UpdateStatusSnapshot
    {
        public int Schema { get; set; } = 1;
        public string Phase { get; set; } = UpdatePhase.Idle;
        public string CurrentVersion { get; set; } = "";
        public string AvailableVersion { get; set; } = "";
        public string NotesUrl { get; set; } = "";
        public string LastCheckedUtc { get; set; } = "";
        public string LastErrorCategory { get; set; } = "";
        public string LastErrorMessage { get; set; } = "";
        public int ProgressPercent { get; set; }
    }

    public static class UpdateErrorCategory
    {
        public const string Network = "network";
        public const string Http = "http";
        public const string Timeout = "timeout";
        public const string InvalidManifest = "invalid_manifest";
        public const string InvalidSignature = "invalid_signature";
        public const string UnsupportedKey = "unsupported_key";
        public const string SizeMismatch = "size_mismatch";
        public const string HashMismatch = "hash_mismatch";
        public const string Disk = "disk";
        public const string Extraction = "extraction";
        public const string PayloadMismatch = "payload_mismatch";
        public const string UnsupportedPlatform = "unsupported_platform";
        public const string Internal = "internal";
    }
}
