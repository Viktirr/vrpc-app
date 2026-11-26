namespace VRPC.Deployment.Updates
{
    // Result of authenticating a discovery manifest. Categories come from
    // UpdateErrorCategory so logs and the popup never expose parser internals.
    public sealed class UpdateVerificationError
    {
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public interface IUpdateManifestVerifier
    {
        // Validates envelope + payload fields and the signature over JCS(payload).
        // Returns false with a sanitized error on any failure; never throws for
        // untrusted input.
        bool TryVerify(UpdateManifest manifest, out UpdateVerificationError error);
    }
}
