using System.Reflection;

namespace VRPC.Application
{
    // Product version from the assembly, written by the build into payload.json and ARP.
    public static class ProductInfo
    {
        public static string Version { get; } = ReadVersion();

        public static string DisplayVersion => Version;

        // Parses 1.1, 1.1.0 or 1.1.0.0; invalid input becomes 0.0.0.
        public static bool TryParseVersion(string? value, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(value)) { return false; }

            string numeric = value.Split('+')[0].Split('-')[0].Trim();
            int dotCount = numeric.Count(c => c == '.');
            if (dotCount == 1) { numeric += ".0"; }
            if (dotCount == 0) { numeric += ".0.0"; }

            return System.Version.TryParse(numeric, out version!);
        }

        public static int CompareVersions(string? left, string? right)
        {
            if (!TryParseVersion(left, out Version leftVersion)) { leftVersion = new Version(0, 0, 0); }
            if (!TryParseVersion(right, out Version rightVersion)) { rightVersion = new Version(0, 0, 0); }
            return leftVersion.CompareTo(rightVersion);
        }

        private static string ReadVersion()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) { return informational.Split('+')[0]; }

            Version? version = assembly.GetName().Version;
            return version is null ? "0.0.0" : version.ToString(3);
        }
    }
}
