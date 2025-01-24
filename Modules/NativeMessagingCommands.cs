using DiscordRPC;
using VRPC.DiscordRPCManager;
using VRPC.Logging;
using VRPC.Configuration;
using Newtonsoft.Json;
using VRPC.Globals;

namespace VRPC.NativeMessasing
{
    class NativeMessagingCommands
    {
        public static void SendRichPresence()
        {
            Log log = new Log();

            string RPCString = "";
            try
            {
                log.Write("[NativeMessagingCommands] Sending RPC data.");
                RichPresence richPresence = DiscordRPCData.richPresenceData;
                string separator = "  .  ";

                string startTime = "0";
                string endTime = "0";

                try
                {
                    startTime = richPresence.Timestamps.Start.HasValue ? ((DateTimeOffset)richPresence.Timestamps.Start.Value).ToUnixTimeSeconds().ToString() : "0";
                    endTime = richPresence.Timestamps.End.HasValue ? ((DateTimeOffset)richPresence.Timestamps.End.Value).ToUnixTimeSeconds().ToString() : "0";
                } catch { log.Write("[NativeMessagingCommands] Couldn't fetch timestamps from rich presence."); }

                try { RPCString = $"RPC: {richPresence.Details}{separator}{richPresence.State}{separator}{richPresence.Assets.LargeImageText}{separator}{richPresence.Assets.LargeImageKey}{separator}{Program.isDiscordRPCRunning}{separator}{startTime}{separator}{endTime}{separator}{Program.isReceivingRPCData}{separator}{DiscordRPCData.currentService}"; }
                catch
                {
                    try { RPCString = $"RPC: EMPTY{separator}{Program.isDiscordRPCRunning}{separator}{Program.isReceivingRPCData}"; }
                    catch { log.Write("[NativeMessagingCommands] Couldn't create a string to send RPC data."); }
                }

                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage(RPCString));
            }
            catch (Exception e) { log.Write($"[NativeMessagingCommands] Couldn't send RPC data to extension. Exception: {e.Data}. RPCString: {RPCString}"); }
        }

        public static void SendConfigFull() {
            Log log = new Log();

            try
            {
                log.Write("[NativeMessagingCommands] Sending full configuration data.");
                var settingsData = VRPCSettings.settingsData;
                
                string jsonSettings = JsonConvert.SerializeObject(settingsData);
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"CONFIG: {jsonSettings}"));
            }
            catch (Exception e)
            {
                log.Write($"[NativeMessagingCommands] Couldn't send configuration data to extension. Exception: {e.Message}");
            }
        }

        public static void SendConfigDetailed(string config) {
            Log log = new Log();

            try
            {
                log.Write($"[NativeMessagingCommands] Sending detailed configuration data for {config}.");
                var settingsData = VRPCSettings.GetSetting(config);
                
                string jsonSettings = JsonConvert.SerializeObject(settingsData);
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"CONFIGINFO: {jsonSettings}"));
            }
            catch (Exception e)
            {
                log.Write($"Couldn't send detailed configuration data to extension. Exception: {e.Message}");
            }
        }

        public static void SetConfig(Dictionary<int,string> config) {
            // Receiving message looks like this:
            // SET_CONFIG
            // ConfigName
            // Value

            Log log = new Log();

            try
            {
                log.Write($"[NativeMessagingCommands] Setting configuration data.");
                string configName = config[1].ToString();
                string configValue = config[2].ToString();

                VRPCSettings.SetSetting(configName, configValue);
            }
            catch (Exception e)
            {
                log.Write($"[NativeMessagingCommands] Couldn't set configuration data. Exception: {e.Message}");
            }
        }

        public static void SendAppVersion()
        {
            Log log = new Log();

            try
            {
                log.Write($"[NativeMessagingCommands] Sending version from application to extension.");
                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage($"APPVERSION: {VRPCGlobalData.appVersion}"));
            }
            catch (Exception e)
            {
                log.Write($"[NativeMessagingCommands] Failed to send version from application to extension. Exception: {e.Data}");
            }
        }
    }
}