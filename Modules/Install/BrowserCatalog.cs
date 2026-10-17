namespace VRPC.Install
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
        public List<string> ChromiumExtensionIds { get; } = new() { BrowserCatalog.ChromeStoreId };
        public List<string> FirefoxExtensionIds { get; } = new() { BrowserCatalog.FirefoxExtensionId };

        public static RegistrationOptions Default => new();
    }

    public sealed class RegistrationResult
    {
        public List<RegisteredManifest> Manifests { get; } = new();
        public List<RegisteredRegistryKey> RegistryKeys { get; } = new();
        public List<string> ExtensionIds { get; } = new();
    }
}
