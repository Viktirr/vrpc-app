using System;
using DiscordRPC;
using VRPC.Logging;
using VRPC.Configuration;

namespace VRPC.DiscordRPCManager.Activities {

    class SetDiscordActivity : DiscordRPCManager {
        private static Log log = new Log();

        public static void UpdateActivityFromFile()
        {
            try
            {
                if (File.Exists(VRPCSettings.RPCInfoPath))
                {
                    string? serviceName;
                    using (StreamReader sr = new StreamReader(VRPCSettings.RPCInfoPath))
                    {
                        serviceName = sr.ReadLine()?.Trim();
                    }

                    if (serviceName == "Youtube Music")
                    {
                        YouTubeMusic.UpdateRPC();
                    }
                }
                else
                {
                    log.Error($"[DiscordRPC - SetDiscordActivity] File not found: {VRPCSettings.RPCInfoPath}");
                }
            }
            catch (Exception e)
            {
                log.Error($"[DiscordRPC - SetDiscordActivity] Error reading activity file: {e.Data + e.StackTrace}");
            }
        }
    }
}