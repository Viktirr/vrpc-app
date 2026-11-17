namespace VRPC.Updates
{
    // Public keys trusted to sign update manifests. The private keys live
    // offline outside every repository (see packaging/update-keygen.mjs); a
    // public key is not a secret. Never add a test key here.
    internal static class TrustedUpdateKeys
    {
        internal static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vrpc-updates-2026-11"] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE+2dUQnkqfAG0VHMYLSv9frZ5FMR3oo/XNwyXUVMYEo6Jo/x+PzlQ2VfmHYp9rqSAXJcbLT/5qnLNY9P3tZskMw=="
        };
    }
}
