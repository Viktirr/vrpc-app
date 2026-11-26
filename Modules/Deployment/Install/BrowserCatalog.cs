namespace VRPC.Deployment.Install
{
    // Static table of browser registry paths and known extension IDs.
    public enum BrowserFamily
    {
        Firefox,
        Chromium
    }

    public sealed record BrowserTarget(string Name, BrowserFamily Family, string RegistryKey, string ManifestName);

    public static class BrowserCatalog
    {
        public const string ChromeStoreId = "dmfcnhgakihhigbkpefjgjkbbnkjagpb";
        public const string FirefoxExtensionId = "vrpc@viktir.com";
        public const string ManifestDescription = "Transfers Rich Presence data to Discord and saves Listening Data locally.";

        public const string FirefoxManifestName = "vrpc-firefox.json";
        public const string ChromiumManifestName = "vrpc-chromium.json";

        public static readonly BrowserTarget[] WindowsTargets =
        {
            new("Firefox", BrowserFamily.Firefox, @"Software\Mozilla\NativeMessagingHosts\vrpc", FirefoxManifestName),
            new("Chrome", BrowserFamily.Chromium, @"Software\Google\Chrome\NativeMessagingHosts\vrpc", ChromiumManifestName),
            new("Edge", BrowserFamily.Chromium, @"Software\Microsoft\Edge\NativeMessagingHosts\vrpc", ChromiumManifestName),
            new("Brave", BrowserFamily.Chromium, @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\vrpc", ChromiumManifestName),
            new("Vivaldi", BrowserFamily.Chromium, @"Software\Vivaldi\NativeMessagingHosts\vrpc", ChromiumManifestName),
            new("Opera", BrowserFamily.Chromium, @"Software\Opera Software\NativeMessagingHosts\vrpc", ChromiumManifestName),
            new("Chromium", BrowserFamily.Chromium, @"Software\Chromium\NativeMessagingHosts\vrpc", ChromiumManifestName)
        };

        public static IEnumerable<BrowserTarget> Targets => WindowsTargets;
    }

    public sealed class RegistrationOptions
    {
        public List<string> ChromiumExtensionIds { get; set; } = new() { BrowserCatalog.ChromeStoreId };
        public List<string> FirefoxExtensionIds { get; set; } = new() { BrowserCatalog.FirefoxExtensionId };

        // True when setup was passed --extension-id / --firefox-extension-id for
        // this family. An explicit list replaces any recorded one instead of
        // being compared with it.
        public bool ChromiumExplicit { get; set; }
        public bool FirefoxExplicit { get; set; }

        public static RegistrationOptions Default => new();

        public static RegistrationOptions FromLists(IEnumerable<string> chromiumIds, IEnumerable<string> firefoxIds)
        {
            return new RegistrationOptions
            {
                ChromiumExtensionIds = chromiumIds.ToList(),
                FirefoxExtensionIds = firefoxIds.ToList(),
                ChromiumExplicit = true,
                FirefoxExplicit = true
            };
        }

        public RegistrationOptions Clone()
        {
            return new RegistrationOptions
            {
                ChromiumExtensionIds = ChromiumExtensionIds.ToList(),
                FirefoxExtensionIds = FirefoxExtensionIds.ToList(),
                ChromiumExplicit = ChromiumExplicit,
                FirefoxExplicit = FirefoxExplicit
            };
        }
    }

    public sealed class RegistrationResult
    {
        public List<RegisteredManifest> Manifests { get; } = new();
        public List<RegisteredRegistryKey> RegistryKeys { get; } = new();
        public List<string> ExtensionIds { get; } = new();
    }
}
