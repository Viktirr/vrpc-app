using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using DiscordRPC;
using VRPC.Logging;
using VRPC.DiscordRPCManager.Activities;
using System.Collections.ObjectModel;

namespace VRPC.DiscordRPCManager
{
    class DiscordRPCManager : IDisposable
    {
        protected static DiscordRpcClient? client;
        protected static RichPresence richPresence = new RichPresence() { Details = "---" };
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
            try { currentServiceDiscordId = serviceList[service]; } catch (Exception e) { log.Write($"Couldn't get current service, using vrpc. Exception: {e.Data}"); currentServiceDiscordId = serviceList[service]; }

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
                log.Write("Discord RPC initialized successfully.");
            }
            catch (Exception e)
            {
                log.Write($"Error initializing Discord RPC: {e.Message + e.StackTrace}");
            }
        }

        public void Start(CancellationToken token)
        {
            if (client == null)
            {
                log.Write("Discord RPC not initialized. Call Init() before Start().");
                return;
            }
            client.Initialize();

            const int checkDelay = 500;
            const int attemptsPerFileChecks = 10;

            try
            {
                while (true)
                {
                    while (!token.IsCancellationRequested)
                    {
                        SetDiscordActivity.UpdateActivityFromFile();

                        client.SetPresence(richPresence);
                        log.Write($"Discord Rich Presence updated. Now {richPresence.Type} {richPresence.Details} by {richPresence.State}.");

                        for (int i = 0; i < attemptsPerFileChecks; i++)
                        {
                            Thread.Sleep(checkDelay);
                        }
                    }
                    return;
                }
            }
            catch (Exception e)
            {
                log.Write($"Error in Start loop for Discord RPC: {e.Message + e.StackTrace}");
                Dispose();
            }
        }

        public void Dispose()
        {
            client?.Dispose();
            log.Write("Discord RPC disposed.");
        }
    }
}