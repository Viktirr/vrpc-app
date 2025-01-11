using VRPC.Globals;

namespace VRPC.DiscordRPCManager.Activities
{
    class SetDiscordActivity : DiscordRPCManager
    {
        public static void UpdateActivity()
        {
            string? serviceName;
            serviceName = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(0);

            string? currentServiceName;
            currentServiceName = DiscordRPCData.currentService;

            if (serviceName == "YouTube Music" && currentServiceName == "YouTube Music")
            {
                YouTubeMusic.UpdateRPC();
            }
            else if (serviceName == "Soundcloud" && currentServiceName == "Soundcloud")
            {
                Soundcloud.UpdateRPC();
            }
        }
    }
}