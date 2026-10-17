using VRPC.Application;
using VRPC.Install;

namespace VRPC.Setup
{
    // Post-install check: files present, Discord DLL present, data writable.
    public static class SelfTest
    {
        // Exit 0 plus the "selftest ok" marker means usable.
        public static int Run()
        {
            if (!AppPaths.IsUnderApp(AppContext.BaseDirectory))
            {
                Console.Error.WriteLine("Self-test must run from the installed app folder.");
                return 1;
            }

            string exe = Path.Combine(AppContext.BaseDirectory, AppPaths.AppExeName);
            if (!File.Exists(exe))
            {
                Console.Error.WriteLine("Self-test failed: VRPC.exe is missing.");
                return 1;
            }

            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "DiscordRPC.dll")))
            {
                Console.Error.WriteLine("Self-test failed: DiscordRPC.dll is missing.");
                return 1;
            }

            try
            {
                Directory.CreateDirectory(AppPaths.Data);
                string probe = Path.Combine(AppPaths.Data, ".selftest-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Self-test failed: the data folder is not writable. " + e.Message);
                return 1;
            }

            Console.WriteLine($"selftest ok version={ProductInfo.Version} root={AppPaths.Root}");
            return 0;
        }
    }
}
