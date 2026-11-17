using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VRPC.Updates
{
    // Validates the discovery manifest and authenticates it against the pinned
    // public keys. Never throws for untrusted input.
    public sealed class UpdateSignatureVerifier : IUpdateManifestVerifier
    {
        private const int P256SignatureLength = 64;
        private static readonly Regex Sha256Pattern = new(UpdateContract.Sha256Pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex VersionPattern = new(UpdateContract.VersionPattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly IReadOnlyDictionary<string, string> _keys;

        public UpdateSignatureVerifier(IReadOnlyDictionary<string, string> keys)
        {
            _keys = keys;
        }

        public static UpdateSignatureVerifier Default { get; } = new(TrustedUpdateKeys.Keys);

        public bool TryVerify(UpdateManifest manifest, out UpdateVerificationError error)
        {
            error = new UpdateVerificationError();
            try
            {
                if (!TryValidatePayload(manifest, error))
                {
                    return false;
                }

                UpdateSignature signature = manifest.Signature;
                if (!string.Equals(signature.Algorithm, UpdateContract.Algorithm, StringComparison.Ordinal))
                {
                    return Fail(error, UpdateErrorCategory.InvalidSignature, "Unsupported signature algorithm.");
                }
                if (!_keys.TryGetValue(signature.KeyId, out string? publicKeyBase64))
                {
                    return Fail(error, UpdateErrorCategory.UnsupportedKey, "Unknown signing key.");
                }

                byte[] signatureBytes;
                try { signatureBytes = Convert.FromBase64String(signature.Value); }
                catch (FormatException) { return Fail(error, UpdateErrorCategory.InvalidSignature, "Signature is not valid base64."); }
                if (signatureBytes.Length != P256SignatureLength)
                {
                    return Fail(error, UpdateErrorCategory.InvalidSignature, "Signature has an unexpected length.");
                }

                JsonElement payloadElement = manifest.RawPayload is JsonElement raw && raw.ValueKind == JsonValueKind.Object
                    ? raw
                    : JsonSerializer.SerializeToElement(manifest.Payload, UpdateJson.Compact);
                byte[] canonical = Jcs.Canonicalize(payloadElement);

                using ECDsa ecdsa = ECDsa.Create();
                try { ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _); }
                catch (Exception exception) when (exception is CryptographicException or FormatException)
                {
                    return Fail(error, UpdateErrorCategory.UnsupportedKey, "Trusted public key is invalid.");
                }

                if (!ecdsa.VerifyData(canonical, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return Fail(error, UpdateErrorCategory.InvalidSignature, "Signature does not match the payload.");
                }

                return true;
            }
            catch (JcsException exception)
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Payload is not canonicalizable: " + exception.Message);
            }
            catch (Exception exception) when (exception is JsonException or CryptographicException or ArgumentException)
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest could not be verified.");
            }
        }

        private static bool TryValidatePayload(UpdateManifest manifest, UpdateVerificationError error)
        {
            if (manifest is null || manifest.Schema != UpdateContract.Schema)
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Unsupported manifest schema.");
            }

            UpdatePayload payload = manifest.Payload;
            if (payload is null || payload.Asset is null)
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest payload is missing.");
            }

            if (!string.Equals(payload.Product, UpdateContract.Product, StringComparison.Ordinal) ||
                !string.Equals(payload.Channel, UpdateContract.Channel, StringComparison.Ordinal) ||
                !string.Equals(payload.Runtime, UpdateContract.Runtime, StringComparison.Ordinal))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest does not describe this product, channel or runtime.");
            }

            string version = payload.Version ?? "";
            if (!VersionPattern.IsMatch(version))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest version is not a strict numeric dotted version.");
            }

            if (!string.Equals(payload.ReleaseTag, UpdateContract.ReleaseTag(version), StringComparison.Ordinal))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest release tag does not match the version.");
            }

            UpdateAsset asset = payload.Asset;
            if (!string.Equals(asset.Name, UpdateContract.AssetName(version), StringComparison.Ordinal))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest asset name does not match the version.");
            }

            string expectedUrl = $"https://github.com/Viktirr/vrpc-app/releases/download/{payload.ReleaseTag}/{asset.Name}";
            if (!string.Equals(asset.Url, expectedUrl, StringComparison.Ordinal))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest asset URL is not the expected GitHub release asset.");
            }

            if (asset.Size <= 0 || asset.Size > UpdateContract.MaxPackageBytes)
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest asset size is out of range.");
            }

            if (!Sha256Pattern.IsMatch(asset.Sha256 ?? ""))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest asset hash is not a lowercase SHA-256.");
            }

            if (!IsHttpsUrl(payload.NotesUrl))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest notes URL must be https.");
            }

            if (!DateTimeOffset.TryParseExact(payload.PublishedAtUtc, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
            {
                return Fail(error, UpdateErrorCategory.InvalidManifest, "Manifest publish date is not a UTC timestamp.");
            }

            return true;
        }

        private static bool IsHttpsUrl(string? value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;
        }

        private static bool Fail(UpdateVerificationError error, string category, string message)
        {
            error.Category = category;
            error.Message = message;
            return false;
        }
    }
}
