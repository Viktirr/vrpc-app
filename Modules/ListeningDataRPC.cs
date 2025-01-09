using System.Text.RegularExpressions;
using VRPC.DiscordRPCManager;
using VRPC.Logging;

namespace VRPC.ListeningDataManager
{
    class ListeningDataRPC : ListeningData
    {
        public static bool RPCListeningDataRunning = false;
        public static int duration = 20;
        private static string GetSongDataKey(string songName, string artistName)
        {
            string pattern = @"[^a-zA-Z.,!?']";
            string songNameClean = Regex.Replace(songName, pattern, "");
            string artistNameClean = Regex.Replace(artistName, pattern, "");

            string key = $"{songNameClean}_{artistNameClean}";
            return key;
        }
        public static void UpdateRPC()
        {
            Log log = new Log();
            SongData songData = ReadDataFile();

            string CurrentLargeImageText = DiscordRPCData.richPresenceData.Assets.LargeImageText;

            string songName = DiscordRPCData.richPresenceData.Details;
            string artistName = DiscordRPCData.richPresenceData.State;
            for (int counter = 0; counter < duration; counter++)
            {
                string currentSongName = DiscordRPCData.richPresenceData.Details;

                if (currentSongName != songName) { break; }

                string key = GetSongDataKey(songName, artistName);

                if (!songData.SongsData.ContainsKey(key)) { return; }
                if (SongPlaying == true)
                {
                    RPCListeningDataRunning = true;

                    if (songData.SongsData[key].ContainsKey("timelistened"))
                    {
                        DiscordRPCData.richPresenceData.Assets.LargeImageText = $"Listened to {songName} for {float.Parse(songData.SongsData[key]["timelistened"]) / 3600:0.0} hours in total";
                        log.Write("Updated large image text to show time listened");
                    }
                    Thread.Sleep(1000);
                }
            }
            RPCListeningDataRunning = false;
            DiscordRPCData.richPresenceData.Assets.LargeImageText = CurrentLargeImageText;
        }
    }
}