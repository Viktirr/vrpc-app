using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using VRPC.Application;

namespace VRPC.Deployment.Install
{
    // Generates manifests, writes registry keys and reads them back.
    public static class RegistrationManager
    {
        // Writes both manifest families and the registry entries.
        public static RegistrationResult Apply(string appExe, RegistrationOptions? options = null)
        {
            options ??= RegistrationOptions.Default;
            if (!File.Exists(appExe)) { throw new RegistrationException($"The application executable was not found at {appExe}."); }
            if (options.ChromiumExtensionIds.Count == 0 && options.FirefoxExtensionIds.Count == 0)
            {
                throw new RegistrationException("At least one extension ID is required.");
            }

            Directory.CreateDirectory(AppPaths.Manifests);
            string firefoxPath = Path.Combine(AppPaths.Manifests, BrowserCatalog.FirefoxManifestName);
            string chromiumPath = Path.Combine(AppPaths.Manifests, BrowserCatalog.ChromiumManifestName);

            if (options.FirefoxExtensionIds.Count > 0)
            {
                WriteManifest(firefoxPath, new FirefoxManifest
                {
                    Name = "vrpc",
                    Description = BrowserCatalog.ManifestDescription,
                    Path = appExe,
                    Type = "stdio",
                    AllowedExtensions = options.FirefoxExtensionIds.ToArray()
                });
            }

            if (options.ChromiumExtensionIds.Count > 0)
            {
                WriteManifest(chromiumPath, new ChromiumManifest
                {
                    Name = "vrpc",
                    Description = BrowserCatalog.ManifestDescription,
                    Path = appExe,
                    Type = "stdio",
                    AllowedOrigins = options.ChromiumExtensionIds
                        .Select(id => id.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ? id : $"chrome-extension://{id.TrimEnd('/')}/")
                        .ToArray()
                });
            }

            RegistrationResult result = new();
            result.ExtensionIds.AddRange(options.ChromiumExtensionIds);
            result.ExtensionIds.AddRange(options.FirefoxExtensionIds);

            if (OperatingSystem.IsWindows())
            {
                foreach (BrowserTarget target in BrowserCatalog.Targets)
                {
                    string manifestPath = target.Family == BrowserFamily.Firefox ? firefoxPath : chromiumPath;
                    if (!File.Exists(manifestPath)) { continue; }

                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(target.RegistryKey, writable: true)
                        ?? throw new RegistrationException($"Could not open {target.RegistryKey}.");
                    key.SetValue(null, manifestPath, RegistryValueKind.String);

                    using RegistryKey? verification = Registry.CurrentUser.OpenSubKey(target.RegistryKey);
                    string? actual = verification?.GetValue(null) as string;
                    if (!string.Equals(actual, manifestPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RegistrationException($"The {target.Name} native-messaging registration did not read back correctly.");
                    }

                    result.RegistryKeys.Add(new RegisteredRegistryKey
                    {
                        Hive = "HKCU",
                        Path = target.RegistryKey,
                        ValueName = "",
                        Value = manifestPath
                    });
                }
            }

            if (File.Exists(firefoxPath))
            {
                result.Manifests.Add(new RegisteredManifest
                {
                    Name = BrowserCatalog.FirefoxManifestName,
                    Path = firefoxPath,
                    Sha256 = PayloadManifest.HashFile(firefoxPath)
                });
            }
            if (File.Exists(chromiumPath))
            {
                result.Manifests.Add(new RegisteredManifest
                {
                    Name = BrowserCatalog.ChromiumManifestName,
                    Path = chromiumPath,
                    Sha256 = PayloadManifest.HashFile(chromiumPath)
                });
            }

            if (File.Exists(firefoxPath)) { VerifyManifest(firefoxPath, appExe); }
            if (File.Exists(chromiumPath)) { VerifyManifest(chromiumPath, appExe); }
            return result;
        }

        // Removes only catalog keys and manifest paths under our root.
        public static void Remove(InstallState? state = null)
        {
            List<string> keys = new();
            HashSet<string> knownKeys = new(BrowserCatalog.Targets.Select(target => target.RegistryKey), StringComparer.OrdinalIgnoreCase);
            if (state is not null)
            {
                keys.AddRange(state.RegistryKeys
                    .Where(key => string.Equals(key.Hive, "HKCU", StringComparison.OrdinalIgnoreCase))
                    .Select(key => key.Path)
                    .Where(knownKeys.Contains));
            }

            keys.AddRange(knownKeys);
            if (OperatingSystem.IsWindows())
            {
                foreach (string key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!key.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase)) { continue; }
                    try { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); } catch { }
                }
            }

            foreach (string manifest in new[] { BrowserCatalog.FirefoxManifestName, BrowserCatalog.ChromiumManifestName })
            {
                string path = Path.Combine(AppPaths.Manifests, manifest);
                try { if (File.Exists(path)) { File.Delete(path); } } catch { }
            }

            if (state is not null)
            {
                foreach (RegisteredManifest manifest in state.Manifests)
                {
                    if (!AppPaths.IsUnderRoot(manifest.Path)) { continue; }
                    try { if (File.Exists(manifest.Path)) { File.Delete(manifest.Path); } } catch { }
                }
            }
        }

        public static void RemoveLegacy()
        {
            if (!OperatingSystem.IsWindows()) { return; }

            foreach (BrowserTarget target in BrowserCatalog.Targets)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(target.RegistryKey, throwOnMissingSubKey: false); } catch { }
            }

            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Viktir\vrpc", throwOnMissingSubKey: false); } catch { }
            ArpRegistration.RemoveLegacy();
        }

        public static void VerifyManifest(string manifestPath, string expectedExe)
        {
            if (!File.Exists(manifestPath))
            {
                throw new RegistrationException($"The manifest {manifestPath} was not created.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                string? path = document.RootElement.GetProperty("path").GetString();
                if (!string.Equals(path, expectedExe, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RegistrationException($"The manifest {manifestPath} points at the wrong executable.");
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new RegistrationException($"The manifest {manifestPath} is not valid: {e.Message}");
            }
        }

        private static void WriteManifest<T>(string path, T manifest)
        {
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private sealed class FirefoxManifest
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("description")] public string Description { get; set; } = "";
            [JsonPropertyName("path")] public string Path { get; set; } = "";
            [JsonPropertyName("type")] public string Type { get; set; } = "stdio";
            [JsonPropertyName("allowed_extensions")] public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
        }

        private sealed class ChromiumManifest
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("description")] public string Description { get; set; } = "";
            [JsonPropertyName("path")] public string Path { get; set; } = "";
            [JsonPropertyName("type")] public string Type { get; set; } = "stdio";
            [JsonPropertyName("allowed_origins")] public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
        }
    }

    public sealed class RegistrationException : Exception
    {
        public RegistrationException(string message) : base(message) { }
    }
}
