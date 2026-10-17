namespace VRPC.Host
{
    // Distinguishes browser launches from setup invocations.
    public static class BrowserInvocation
    {
        // Accepts Chrome origin, Firefox manifest path and --host.
        public static bool IsHost(string[] args)
        {
            if (args.Length == 0) { return false; }
            if (args.Any(arg => string.Equals(arg, "--host", StringComparison.OrdinalIgnoreCase))) { return true; }

            string first = args[0];
            return first.Contains("chrome-extension", StringComparison.OrdinalIgnoreCase) ||
                   first.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }
    }
}
