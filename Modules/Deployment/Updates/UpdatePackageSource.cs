namespace VRPC.Deployment.Updates
{
    // Outcome of a download/extract step. Categories come from
    // UpdateErrorCategory; messages are sanitized for logs and the popup.
    public sealed class UpdateOperationResult
    {
        public bool Success { get; set; }
        public string ErrorCategory { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }

    // Seam between the updater state machine and the network/ZIP code. The
    // production implementation wraps UpdateFeedClient/Downloader/Extractor;
    // tests inject fakes.
    public interface IUpdatePackageSource
    {
        Task<UpdateCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken);

        Task<UpdateOperationResult> DownloadAsync(UpdateAsset asset, string destinationArchivePath, CancellationToken cancellationToken);

        Task<UpdateOperationResult> ExtractAsync(string archivePath, string destinationDirectory, string expectedVersion, CancellationToken cancellationToken);

        // Re-authenticates a previously saved signed envelope before install.
        bool VerifySavedManifest(UpdateManifest manifest, out string errorCategory, out string errorMessage);

        // Re-validates an extracted package directory against payload.json.
        bool VerifyPackageDirectory(string packageDirectory, string expectedVersion, out string errorCategory, out string errorMessage);
    }

    public interface IUpdateProcessLauncher
    {
        // Starts the extracted setup and returns once the child process is
        // running. `unattended` selects --quiet --yes instead of the panel; the
        // caller performs the host shutdown afterwards.
        bool TryLaunch(string packageDirectory, string version, bool unattended, out string errorMessage);
    }
}
