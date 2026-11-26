using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRPC.Deployment.Updates
{
    // Strict serializer settings for the updater's own files and wire payloads.
    // The installer keeps its own InstallJson options.
    internal static class UpdateJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static readonly JsonSerializerOptions Compact = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
