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

        public static void ShowcaseTimeListened()
        {
            string songName = DiscordRPCData.richPresenceData.Details;
            string artistName = DiscordRPCData.richPresenceData.State;

            SongData songData = ReadDataFile();

            string key = GetSongDataKey(songName, artistName);

            if (songData.SongsData.ContainsKey(key))
            {
                DiscordRPCData.richPresenceData.Assets.LargeImageText = $"Listened for {float.Parse(songData.SongsData[key]["timelistened"]) / 3600:F1} hours total.";
            }
        }
    }
}