using VRPC.Globals;
using VRPC.ListeningDataManager;
using VRPC.Logging;

namespace VRPC.DiscordRPCManager.Activities
{
    class Soundcloud : SetDiscordActivity
    {
        private static void UpdateListeningData(string? songName, string? artistName, string? songStatus)
        {
            if (songName == null || artistName == null || songStatus == null) { return; }
            bool songPlaying = songStatus == "Playing" ? true : false;
            ListeningData.UpdateListeningData(songName, artistName, songPlaying);
        }

        private static void UpdateImage(string? smallSongBanner)
        {
            if (VRPCGlobalFunctions.IsRPCStringNull(smallSongBanner))
            {
                richPresence.Assets.LargeImageKey = "soundcloud-vrpc";
                richPresence.Assets.LargeImageText = "Listening on Soundcloud";
            }
            else
            {
                richPresence.Assets.LargeImageKey = $"{smallSongBanner}";
                richPresence.Assets.LargeImageText = "Listening on Soundcloud";
            }
        }

        private static void UpdateStatus(string? songStatus, int? currentTime)
        {
            if (songStatus != "Playing" && songStatus != "Paused")
            {
                richPresence.Assets.SmallImageKey = "";
                richPresence.Assets.SmallImageText = "";
            }
            if (songStatus == "Playing")
            {
                richPresence.Assets.SmallImageKey = "playing";
                richPresence.Assets.SmallImageText = "Playing";
            }
            else if (songStatus == "Paused")
            {
                richPresence.Assets.SmallImageKey = "paused";
                richPresence.Assets.SmallImageText = "Paused";

                if (currentTime.HasValue)
                {
                    richPresence.Timestamps.Start = DateTime.UtcNow - TimeSpan.FromSeconds(currentTime.Value);
                }
                richPresence.Timestamps.End = DateTime.UtcNow;
            }
        }

        private static void UpdateButton(string? songUrl)
        {
            if (VRPCGlobalFunctions.IsRPCStringNull(songUrl))
            {
                richPresence.Buttons = null;
            }
            else
            {
                richPresence.Buttons = new DiscordRPC.Button[]
                {
                    new DiscordRPC.Button() { Label = "Listen on Soundcloud", Url = songUrl }
                };
            }
        }

        public static void UpdateRPC()
        {
            Log log = new Log();

            string? songName = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(1);
            string? artistName = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(2);
            int currentTime = int.Parse(VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(3, "0"));
            int totalTime = int.Parse(VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(4, "0"));
            string? songStatus = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(5);
            string? smallSongBanner = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(6);
            string? songUrl = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(7);

            if (VRPCGlobalFunctions.IsRPCStringNull(songName))
            {
                richPresence.Details = "Main menu";
                richPresence.State = "Browsing";
                richPresence.Assets.LargeImageKey = "soundcloud-vrpc";
                richPresence.Assets.LargeImageText = "Soundcloud";
                return;
            }

            if (!string.IsNullOrEmpty(songName))
            {
                richPresence.Details = songName;
                if (richPresence.Details != songName) { DiscordRPCData.forceUpdateDiscordRPC = true; }
            }

            if (!string.IsNullOrEmpty(artistName))
            {
                richPresence.State = artistName;
            }

            richPresence.Timestamps.Start = DateTime.UtcNow - TimeSpan.FromSeconds(currentTime);
            richPresence.Timestamps.End = DateTime.UtcNow + TimeSpan.FromSeconds(totalTime - currentTime);

            UpdateListeningData(songName, artistName, songStatus);
            UpdateStatus(songStatus, currentTime);
            UpdateImage(smallSongBanner);
            UpdateButton(songUrl);
        }
    }
}