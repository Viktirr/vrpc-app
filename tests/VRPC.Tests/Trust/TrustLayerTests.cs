using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VRPC.Updates;

namespace VRPC.Tests.Trust
{
    public sealed class TrustLayerTests
    {
        private static string FixturePath(string name)
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Updater", name);

        private static string TestPublicKeyBase64
            => File.ReadAllText(FixturePath("vrpc-test-key.public.spki.b64")).Trim();

        private static UpdateSignatureVerifier TestVerifier
            => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["vrpc-test-key"] = TestPublicKeyBase64 });

        private static UpdateManifest LoadVectorManifest()
        {
            string json = File.ReadAllText(FixturePath("vector.manifest.json"));
            return JsonSerializer.Deserialize<UpdateManifest>(json, UpdateJson.Compact)!;
        }

        [Fact]
        public void Jcs_matches_node_generated_vectors()
        {
            using JsonDocument vectors = JsonDocument.Parse(File.ReadAllText(FixturePath("jcs-vectors.json")));

            foreach (JsonElement valid in vectors.RootElement.GetProperty("valid").EnumerateArray())
            {
                string expected = valid.GetProperty("canonical").GetString()!;
                string actual = Jcs.CanonicalizeToString(valid.GetProperty("value"));
                Assert.Equal(expected, actual);
            }

            foreach (JsonElement invalid in vectors.RootElement.GetProperty("invalid").EnumerateArray())
            {
                using JsonDocument document = JsonDocument.Parse(invalid.GetProperty("raw").GetString()!);
                if (invalid.TryGetProperty("canonical", out JsonElement expected))
                {
                    Assert.Equal(expected.GetString(), Jcs.CanonicalizeToString(document.RootElement));
                }
                else
                {
                    Assert.Throws<JcsException>(() => Jcs.CanonicalizeToString(document.RootElement));
                }
            }
        }

        [Fact]
        public void Jcs_signed_vector_matches_node_canonical_bytes()
        {
            using JsonDocument payload = JsonDocument.Parse(File.ReadAllText(FixturePath("vector.payload.json")));
            byte[] canonical = Jcs.Canonicalize(payload.RootElement);
            byte[] expected = File.ReadAllBytes(FixturePath("vector.canonical.txt"));

            Assert.Equal(expected, canonical);
        }

        [Fact]
        public void Verifier_accepts_node_signed_manifest()
        {
            UpdateManifest manifest = LoadVectorManifest();
            Assert.True(TestVerifier.TryVerify(manifest, out UpdateVerificationError error), error.Message);
            Assert.Equal("vrpc-test-key", manifest.Signature.KeyId);
            Assert.Equal("1.2.0", manifest.Payload.Version);
        }

        [Fact]
        public void Verifier_rejects_tampered_payload()
        {
            UpdateManifest manifest = LoadVectorManifest();
            manifest.Payload.Asset.Sha256 = new string('f', 64);

            Assert.False(TestVerifier.TryVerify(manifest, out UpdateVerificationError error));
            Assert.Equal(UpdateErrorCategory.InvalidSignature, error.Category);
        }

        [Fact]
        public void Verifier_rejects_unknown_key_and_wrong_algorithm()
        {
            UpdateManifest unknownKey = LoadVectorManifest();
            unknownKey.Signature.KeyId = "vrpc-test-key-unknown";
            Assert.False(TestVerifier.TryVerify(unknownKey, out UpdateVerificationError unknownError));
            Assert.Equal(UpdateErrorCategory.UnsupportedKey, unknownError.Category);

            UpdateManifest wrongAlgorithm = LoadVectorManifest();
            wrongAlgorithm.Signature.Algorithm = "ECDSA-P384-SHA384";
            Assert.False(TestVerifier.TryVerify(wrongAlgorithm, out UpdateVerificationError algorithmError));
            Assert.Equal(UpdateErrorCategory.InvalidSignature, algorithmError.Category);

            UpdateManifest truncated = LoadVectorManifest();
            truncated.Signature.Value = Convert.ToBase64String(new byte[32]);
            Assert.False(TestVerifier.TryVerify(truncated, out UpdateVerificationError truncatedError));
            Assert.Equal(UpdateErrorCategory.InvalidSignature, truncatedError.Category);
        }

        [Fact]
        public void Trusted_keys_are_importable_and_contain_no_test_keys()
        {
            Assert.True(TrustedUpdateKeys.Keys.ContainsKey("vrpc-updates-2026-11"));
            Assert.False(TrustedUpdateKeys.Keys.ContainsKey("vrpc-test-key"));

            foreach (string publicKey in TrustedUpdateKeys.Keys.Values)
            {
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out int read);
                Assert.True(read > 0);
            }
        }

        [Fact]
        public void Verifier_rejects_unsigned_extra_payload_fields()
        {
            UpdateManifest manifest = SignPayload(ValidPayload());
            Assert.True(TestVerifier.TryVerify(manifest, out _));

            JsonObject payloadObject = JsonNode.Parse(JsonSerializer.Serialize(manifest.Payload, UpdateJson.Compact))!.AsObject();
            payloadObject["unexpected"] = "value";
            using JsonDocument document = JsonDocument.Parse(payloadObject.ToJsonString());
            manifest.RawPayload = document.RootElement.Clone();

            Assert.False(TestVerifier.TryVerify(manifest, out UpdateVerificationError error));
            Assert.Equal(UpdateErrorCategory.InvalidSignature, error.Category);
        }

        [Fact]
        public void Manifest_reader_preserves_raw_payload_and_rejects_duplicate_envelope_keys()
        {
            string vectorJson = File.ReadAllText(FixturePath("vector.manifest.json"));
            UpdateManifest? manifest = UpdateManifestJson.Deserialize(vectorJson);

            Assert.NotNull(manifest);
            Assert.True(manifest!.RawPayload.HasValue);
            Assert.True(TestVerifier.TryVerify(manifest, out UpdateVerificationError error), error.Message);
            Assert.Throws<JsonException>(() => UpdateManifestJson.Deserialize("{\"schema\":1,\"schema\":1,\"payload\":{},\"signature\":{}}"));
        }

        [Fact]
        public void Signed_payload_round_trips_and_mutations_fail()
        {
            UpdateManifest manifest = SignPayload(ValidPayload());
            Assert.True(TestVerifier.TryVerify(manifest, out UpdateVerificationError error), error.Message);

            manifest.Payload.Version = "1.2.1";
            Assert.False(TestVerifier.TryVerify(manifest, out _));
        }

        [Fact]
        public void Verifier_rejects_invalid_payload_fields()
        {
            List<(string Name, Action<UpdatePayload> Mutate)> cases = new()
            {
                ("prerelease version", payload => payload.Version = "1.2.0-beta"),
                ("leading zero version", payload => payload.Version = "01.2.0"),
                ("four part version", payload => payload.Version = "1.2.0.1"),
                ("kind", payload => payload.Product = "Other"),
                ("channel", payload => payload.Channel = "beta"),
                ("runtime", payload => payload.Runtime = "linux-x64"),
                ("release tag", payload => payload.ReleaseTag = "v1.2.1"),
                ("asset name", payload => payload.Asset.Name = "VRPC-1.2.0-win-x64.exe"),
                ("http asset url", payload => payload.Asset.Url = "http://github.com/Viktirr/vrpc-app/releases/download/v1.2.0/VRPC-1.2.0-win-x64.zip"),
                ("foreign owner asset url", payload => payload.Asset.Url = "https://github.com/someone/vrpc-app/releases/download/v1.2.0/VRPC-1.2.0-win-x64.zip"),
                ("asset url query", payload => payload.Asset.Url = "https://github.com/Viktirr/vrpc-app/releases/download/v1.2.0/VRPC-1.2.0-win-x64.zip?x=1"),
                ("zero size", payload => payload.Asset.Size = 0),
                ("negative size", payload => payload.Asset.Size = -1),
                ("oversize", payload => payload.Asset.Size = UpdateContract.MaxPackageBytes + 1),
                ("uppercase hash", payload => payload.Asset.Sha256 = new string('A', 64)),
                ("short hash", payload => payload.Asset.Sha256 = new string('a', 63)),
                ("non hex hash", payload => payload.Asset.Sha256 = new string('g', 64)),
                ("http notes", payload => payload.NotesUrl = "http://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0"),
                ("notes not url", payload => payload.NotesUrl = "not a url"),
                ("publish date with millis", payload => payload.PublishedAtUtc = "2026-11-15T00:00:00.000Z"),
                ("publish date missing Z", payload => payload.PublishedAtUtc = "2026-11-15 00:00:00")
            };

            foreach ((string name, Action<UpdatePayload> mutate) in cases)
            {
                UpdatePayload payload = ValidPayload();
                mutate(payload);
                UpdateManifest manifest = SignPayload(payload);
                Assert.False(TestVerifier.TryVerify(manifest, out UpdateVerificationError error), $"Expected rejection for {name}.");
                Assert.NotEqual(UpdateErrorCategory.InvalidSignature, error.Category);
            }
        }

        [Fact]
        public void Strict_version_validation_and_ordering()
        {
            Assert.True(StrictVersion.IsValid("0.0.0"));
            Assert.True(StrictVersion.IsValid("1.2.3"));
            Assert.True(StrictVersion.IsValid("10.20.30"));
            Assert.False(StrictVersion.IsValid(""));
            Assert.False(StrictVersion.IsValid("1.2"));
            Assert.False(StrictVersion.IsValid("1.2.3.4"));
            Assert.False(StrictVersion.IsValid("01.2.3"));
            Assert.False(StrictVersion.IsValid("1.02.3"));
            Assert.False(StrictVersion.IsValid("1.2.03"));
            Assert.False(StrictVersion.IsValid("v1.2.3"));
            Assert.False(StrictVersion.IsValid("1.2.3-beta"));
            Assert.False(StrictVersion.IsValid(" 1.2.3"));

            Assert.True(StrictVersion.Compare("1.2.3", "1.2.3") == 0);
            Assert.True(StrictVersion.Compare("1.2.3", "1.2.4") < 0);
            Assert.True(StrictVersion.Compare("1.10.0", "1.9.9") > 0);
            Assert.True(StrictVersion.Compare("2.0.0", "1.99.99") > 0);

            Assert.True(StrictVersion.IsStrictlyNewer("1.2.0", "1.1.9"));
            Assert.False(StrictVersion.IsStrictlyNewer("1.1.0", "1.1.0"));
            Assert.False(StrictVersion.IsStrictlyNewer("1.0.0", "1.1.0"));
        }

        private static UpdatePayload ValidPayload()
        {
            return new UpdatePayload
            {
                Product = "VRPC",
                Version = "1.2.0",
                Channel = "stable",
                Runtime = "win-x64",
                ReleaseTag = "v1.2.0",
                Asset = new UpdateAsset
                {
                    Name = "VRPC-1.2.0-win-x64.zip",
                    Url = "https://github.com/Viktirr/vrpc-app/releases/download/v1.2.0/VRPC-1.2.0-win-x64.zip",
                    Size = 12345678,
                    Sha256 = new string('a', 64)
                },
                NotesUrl = "https://github.com/Viktirr/vrpc-app/releases/tag/v1.2.0",
                PublishedAtUtc = "2026-11-15T00:00:00Z"
            };
        }

        private static UpdateManifest SignPayload(UpdatePayload payload)
        {
            string pem = File.ReadAllText(FixturePath("vrpc-test-key.private.pem"));
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(pem);

            byte[] canonical = Jcs.Canonicalize(JsonSerializer.SerializeToElement(payload, UpdateJson.Compact));
            byte[] signature = key.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            return new UpdateManifest
            {
                Schema = UpdateContract.Schema,
                Payload = payload,
                Signature = new UpdateSignature
                {
                    Algorithm = UpdateContract.Algorithm,
                    KeyId = "vrpc-test-key",
                    Value = Convert.ToBase64String(signature)
                }
            };
        }
    }
}
