using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using DiscordRPC;
using DiscordRPC.Helper;
using VRPC.DiscordRPCManager;
using VRPC.Logging;

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

                try { RPCString = $"RPC: {richPresence.Details}{separator}{richPresence.State}{separator}{richPresence.Assets.LargeImageText}{separator}{richPresence.Assets.LargeImageKey}{separator}{Program.isDiscordRPCRunning}{separator}{startTime}{separator}{endTime}{separator}{Program.isReceivingRPCData}"; }
                catch
                {
                    try { RPCString = $"RPC: EMPTY{separator}{Program.isDiscordRPCRunning}{separator}{Program.isReceivingRPCData}"; }
                    catch { log.Write("[NativeMessagingCommands] Couldn't create a string to send RPC data."); }
                }

                NativeMessaging.SendMessage(NativeMessaging.EncodeMessage(RPCString));
            }
            catch (Exception e) { log.Write($"Couldn't send RPC data to extension. Exception: {e.Data}. RPCString: {RPCString}"); }
        }
    }
}