using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using DiscordRPC;
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

            log.Write("[NativeMessagingCommands] Sending RPC data.");
            RichPresence richPresence = DiscordRPCData.richPresenceData;
            string separator = "  .  ";
            try { RPCString = $"RPC: {richPresence.Details}{separator}{richPresence.State}{separator}{richPresence.Assets.LargeImageText}{separator}{richPresence.Assets.LargeImageKey}"; }
            catch { log.Write("[NativeMessagingCommands] Couldn't create a string to send RPC data."); }

            NativeMessaging.SendMessage(NativeMessaging.EncodeMessage(RPCString));
        }
    }
}