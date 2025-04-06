using System.Text.RegularExpressions;
using VRPC.DiscordRPCManager;
using VRPC.Logging;

namespace VRPC.ListeningDataManager
{
    class ListeningDataRPC : ListeningData
    {
        private static string GetSongDataKey(string songName, string artistName)
        {
            string pattern = @"[^a-zA-Z.,!?']";
            string songNameClean = Regex.Replace(songName, pattern, "");
            string artistNameClean = Regex.Replace(artistName, pattern, "");

            string key = $"{songNameClean}_{artistNameClean}";

            if (key == "_") { key = $"{songName}_{artistName}"; }

            return key;
        }

        public static void UpdateRPC()
        {
            Log log = new Log();
            SongData songData = ReadDataFile();

            string CurrentLargeImageText;
            try { CurrentLargeImageText = DiscordRPCData.richPresenceData.Assets.LargeImageText; } catch { log.Info("[ListeningDataRPC] No album text found, not changing anything."); return; }

            string songName = DiscordRPCData.richPresenceData.Details;
            string artistName = DiscordRPCData.richPresenceData.State;

            string key = GetSongDataKey(songName, artistName);
            DiscordRPCData.richPresenceData.Assets.LargeImageText = $"Listened to {songName} for {float.Parse(songData.SongsData[key]["timelistened"]) / 3600:0.0} hours in total";
            log.Write("[ListeningDataRPC] Updated large image text to show time listened");
        }
    }
}