using DiscordRPC;
using VRPC.Logging;
using VRPC.DiscordRPCManager.Activities;

namespace VRPC.DiscordRPCManager
{
    public static class DiscordRPCData
    {
        public static bool forceUpdateDiscordRPC = false;
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
            ["Default"] = "1257441643691380828"
        };

        public void Init(string service)
        {
            try
            {
                log = new Log();
            }
            catch (Exception e) { Console.WriteLine($"Could not initialize logging. {e.Data}"); }

            string currentServiceDiscordId;
            try { currentServiceDiscordId = serviceList[service]; DiscordRPCData.currentService = service; } catch (Exception e) { log.Warn($"[DiscordRPC] Couldn't get current service, using vrpc. Exception: {e.Data}. Service which was not found: {service}"); currentServiceDiscordId = serviceList["Default"]; }

            try
            {
                client = new DiscordRpcClient(currentServiceDiscordId, pipe: -1);

                richPresence = new RichPresence()
                {
                    Type = DiscordRPC.ActivityType.Listening,
                    Details = "---",
                    State = "---",
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
                log.Write("[DiscordRPC] Discord RPC initialized successfully.");
            }
            catch (Exception e)
            {
                log.Error($"[DiscordRPC] Error initializing Discord RPC: {e.Message + e.StackTrace}");
            }

            Thread updateActivity = new Thread(() => {
                while (true)
                {
                    SetDiscordActivity.UpdateActivityFromFile();
                    Thread.Sleep(1000);
                }
            });
            updateActivity.Start();
        }

        public void Start(CancellationToken token)
        {
            if (client == null)
            {
                log.Info("[DiscordRPC] Discord RPC not initialized. Call Init() before Start().");
                return;
            }
            client.Initialize();

            const int checkDelay = 1000;
            const int attemptsPerFileChecks = 5;

            try
            {
                while (true)
                {
                    while (!token.IsCancellationRequested)
                    {
                        client.SetPresence(richPresence);
                        log.Info($"[DiscordRPC] Discord Rich Presence updated. Now {richPresence.Type} {richPresence.Details} by {richPresence.State}.");

                        for (int i = 0; i < attemptsPerFileChecks; i++)
                        {
                            if (DiscordRPCData.forceUpdateDiscordRPC == true) { DiscordRPCData.forceUpdateDiscordRPC = false; break; }
                            Thread.Sleep(checkDelay);
                        }
                    }
                    return;
                }
            }
            catch (Exception e)
            {
                log.Error($"[DiscordRPC] Error in Start loop for Discord RPC: {e.Message + e.StackTrace}");
                Dispose();
            }
        }

        public void Dispose()
        {
            client?.Dispose();
            log.Info("[DiscordRPC] Discord RPC disposed.");
        }
    }
}