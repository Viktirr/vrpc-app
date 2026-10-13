using DiscordRPC;
using VRPC.Logging;
using VRPC.DiscordRPCManager.Activities;
using VRPC.Globals;

namespace VRPC.DiscordRPCManager
{
    public static class DiscordRPCData
    {
        public static bool forceUpdateDiscordRPC;
        public static string currentService = "Default";
        public static RichPresence richPresenceData = new RichPresence() { Details = "---" };
    }
    class DiscordRPCManager : IDisposable
    {
        public static RichPresence richPresence
        {
            get => DiscordRPCData.richPresenceData;
            set => DiscordRPCData.richPresenceData = value;
        }
        protected static DiscordRpcClient? client;
        private static Log log = new Log();
        private Dictionary<string, string> serviceList = new Dictionary<string, string>
        {
            ["YouTube Music"] = "1318269449744154664",
            ["Soundcloud"] = "1327613957975969823",
            ["Default"] = "1257441643691380828"
        };

        public void Init(string service)
        {
            try
            {
                log = new Log();
            }
            catch (Exception e) { Console.Error.WriteLine($"Could not initialize logging. {e.Message}"); }

            EnsureDiscordSocketLinux();

            string currentServiceDiscordId;
            try { currentServiceDiscordId = serviceList[service]; DiscordRPCData.currentService = service; } catch (Exception e) { log.Warn($"[DiscordRPC] Couldn't get current service, using vrpc. Exception: {e.Message}. Service which was not found: {service}"); currentServiceDiscordId = serviceList["Default"]; }

            try
            {
                client = new DiscordRpcClient(currentServiceDiscordId, pipe: -1);

                richPresence = new RichPresence()
                {
                    Type = DiscordRPC.ActivityType.Listening,
                    Details = "",
                    State = "",
                    Assets = new Assets()
                    {
                        LargeImageKey = "",
                        LargeImageText = "",
                        SmallImageKey = "",
                        SmallImageText = ""
                    },
                    Timestamps = new Timestamps()
                    {
                        Start = DateTime.UtcNow
                    }
                };

                client.SkipIdenticalPresence = true;

                log.Write("[DiscordRPC] Discord RPC initialized successfully.");
            }
            catch (Exception e)
            {
                log.Error($"[DiscordRPC] Error initializing Discord RPC: {e.Message + e.StackTrace}");
            }


            VRPCGlobalEvents.RPCEvent += (sender, e) =>
            {
                SetDiscordActivity.UpdateActivity();
            };
            SetDiscordActivity.UpdateActivity();
        }

        public void Start()
        {
            if (client == null)
            {
                log.Info("[DiscordRPC] Discord RPC not initialized. Call Init() before Start().");
                return;
            }
            client.Initialize();

            VRPCGlobalEvents.RPForceUpdate += (sender, e) =>
            {
                log.Write("[DiscordRPC] Discord RPC force update called.");
                try
                {
                    client.SetPresence(richPresence);
                    log.Info($"[DiscordRPC] Discord Rich Presence updated. Now {richPresence.Type} {richPresence.Details} by {richPresence.State} at {richPresence.Timestamps.Start} to {richPresence.Timestamps.End}.");
                }
                catch (Exception ex)
                {
                    log.Error($"[DiscordRPC] Couldn't update rich presence: {ex.Message}");
                }
            };

            VRPCGlobalEvents.RPClear += (sender, e) =>
            {
                try
                {
                    client.ClearPresence();
                    log.Info($"[DiscordRPC] Discord Rich Presence removed.");
                }
                catch (Exception ex)
                {
                    log.Error($"[DiscordRPC] Couldn't clear rich presence: {ex.Message}");
                }
            };
        }

        public void Dispose()
        {
            client?.Dispose();
            log.Info("[DiscordRPC] Discord RPC disposed.");
        }

        // On Linux, discord-rpc-csharp only scans $XDG_RUNTIME_DIR/discord-ipc-N for the IPC socket.
        // Flatpak and Snap Discord sandbox the socket into a subdirectory, so bridge it with a symlink
        // before connecting. Native Discord already exposes the socket at the top level (no-op then).
        private static void EnsureDiscordSocketLinux()
        {
            if (!OperatingSystem.IsLinux()) { return; }

            string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtimeDir)) { return; }

            string link = Path.Combine(runtimeDir, "discord-ipc-0");
            if (Path.Exists(link)) { return; }

            string[] sandboxSubdirs =
            {
                "app/com.discordapp.Discord", // Flatpak
                "snap.discord",               // Snap
            };

            foreach (string subdir in sandboxSubdirs)
            {
                string sandboxSocket = Path.Combine(runtimeDir, subdir, "discord-ipc-0");
                if (!Path.Exists(sandboxSocket)) { continue; }

                try
                {
                    File.CreateSymbolicLink(link, sandboxSocket);
                    log.Write($"[DiscordRPC] Bridged Discord IPC socket: {link} -> {sandboxSocket}");
                }
                catch (Exception e)
                {
                    log.Warn($"[DiscordRPC] Couldn't bridge Discord IPC socket: {e.Message}");
                }
                return;
            }
        }
    }
}