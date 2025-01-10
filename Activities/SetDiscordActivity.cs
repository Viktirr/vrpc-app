using VRPC.Globals;

namespace VRPC.DiscordRPCManager.Activities
{

    class SetDiscordActivity : DiscordRPCManager
    {
        public static void UpdateActivity()
        {
            string? serviceName;
            serviceName = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(0);

            if (serviceName == "YouTube Music")
            {
                YouTubeMusic.UpdateRPC();
            }
        }
    }
}