using System.Text.RegularExpressions;

namespace VRPC.Updates
{
    // Strict numeric dotted-version handling for the update feed. Deliberately
    // separate from ProductInfo, which tolerates malformed versions and drops
    // prerelease suffixes.
    public static class StrictVersion
    {
        private static readonly Regex Pattern = new(UpdateContract.VersionPattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsValid(string? version) => version is not null && Pattern.IsMatch(version);

        public static bool TryParse(string? version, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            if (!IsValid(version)) { return false; }

            string[] parts = version!.Split('.');
            return int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor) && int.TryParse(parts[2], out patch);
        }

        // -1 when left < right, 0 when equal, 1 when left > right. Malformed
        // input is not comparable: callers must validate first.
        public static int Compare(string left, string right)
        {
            if (!TryParse(left, out int leftMajor, out int leftMinor, out int leftPatch) ||
                !TryParse(right, out int rightMajor, out int rightMinor, out int rightPatch))
            {
                throw new ArgumentException("Both versions must be strict numeric dotted versions.");
            }

            int result = leftMajor.CompareTo(rightMajor);
            if (result != 0) { return Math.Sign(result); }
            result = leftMinor.CompareTo(rightMinor);
            if (result != 0) { return Math.Sign(result); }
            return Math.Sign(leftPatch.CompareTo(rightPatch));
        }

        public static bool IsStrictlyNewer(string candidate, string current)
            => IsValid(candidate) && IsValid(current) && Compare(candidate, current) > 0;
    }
}
