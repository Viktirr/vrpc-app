using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRPC.Deployment.Install
{
    // Shared JSON settings for payload.json, install.json and the update marker.
    internal static class InstallJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
