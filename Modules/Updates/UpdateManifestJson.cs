using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRPC.Updates
{
    // Reads and writes the signed envelope while preserving the exact payload
    // object: that object, not a re-serialized projection, is the JCS signature
    // subject. Unknown or duplicate fields therefore fail closed instead of
    // being silently dropped or last-win.
    internal static class UpdateManifestJson
    {
        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        };

        private static readonly JsonSerializerOptions ReaderOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static UpdateManifest? Deserialize(ReadOnlySpan<byte> utf8Json)
        {
            return Deserialize(Encoding.UTF8.GetString(utf8Json));
        }

        public static UpdateManifest? Deserialize(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }

            RejectDuplicateProperties(root);
            if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            UpdateManifest? manifest = root.Deserialize<UpdateManifest>(ReaderOptions);
            if (manifest is not null)
            {
                manifest.RawPayload = payload.Clone();
            }
            return manifest;
        }

        public static string SerializeEnvelope(UpdateManifest manifest)
        {
            if (manifest.RawPayload is JsonElement raw && raw.ValueKind == JsonValueKind.Object)
            {
                using MemoryStream stream = new MemoryStream();
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema", manifest.Schema);
                    writer.WritePropertyName("payload");
                    raw.WriteTo(writer);
                    writer.WritePropertyName("signature");
                    JsonSerializer.Serialize(writer, manifest.Signature, UpdateJson.Compact);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }

            return JsonSerializer.Serialize(manifest, UpdateJson.Compact);
        }

        private static void RejectDuplicateProperties(JsonElement root)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new JsonException("Duplicate object keys are not allowed in the update manifest.");
                }
            }
        }
    }
}
