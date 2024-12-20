using System.Text.Encodings;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using VRPC.Configuration;
using VRPC.Logging;
using System.Text;

namespace VRPC.ListeningDataManager
{
    class ListeningData
    {
        private static string SongName { get; set; } = string.Empty;
        private static string ArtistName { get; set; } = string.Empty;
        private static string PreviousSongName = string.Empty;
        private static string PreviousArtistName = string.Empty;
        private static int activeSongInSeconds = 0;
        private static Log log = new Log();
        private static bool SongPlaying = false;

        private class SongData
        {
            // "TotalListened": 0
            // "SongsData": {
            // ["SongName_ArtistName"]: ["SongName","ArtistName","SongTotalSeconds"]
            // }
            public int TotalListened { get; set; } = 0;
            public Dictionary<string, string[]> SongsData { get; set; } = new Dictionary<string, string[]>();

            public void AddSong(string songName, string artistName, int songTotalSeconds)
            {
                if (string.IsNullOrEmpty(songName)) { return; }
                string pattern = @"[^a-zA-Z.,!?']";
                string songNameClean = Regex.Replace(songName, pattern, "");
                string artistNameClean = Regex.Replace(artistName, pattern, "");

                string key = $"{songNameClean}_{artistNameClean}";

                if (SongsData.ContainsKey(key))
                {
                    SongsData[key][2] = (int.Parse(SongsData[key][2]) + songTotalSeconds).ToString();
                }
                else
                {
                    SongsData[key] = new string[] { songName, artistName, songTotalSeconds.ToString() };
                }
            }
        }

        private static void UpdateListeningDataFile()
        {
            string filePath = VRPCSettings.ListeningDataPath;

            SongData songData = new SongData();
            try
            {
                string json = System.IO.File.ReadAllText(filePath, Encoding.UTF8);
                songData = JsonSerializer.Deserialize<SongData>(json) ?? new SongData();
            }
            catch
            {
                log.Warn("[ListeningData] Couldn't read from file, creating new SongData object");
                songData = new SongData();
            }

            songData.AddSong(PreviousSongName, PreviousArtistName, activeSongInSeconds);
            songData.TotalListened += activeSongInSeconds;

            try
            {
                string jsonString = JsonSerializer.Serialize(songData, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(filePath, jsonString, Encoding.UTF8);
            }
            catch { log.Error("[ListeningData] Couldn't write to file"); }
        }

        public static void UpdateListeningData(string _songName, string _artistName, bool _songPlaying = false)
        {
            SongName = _songName;
            ArtistName = _artistName;
            SongPlaying = _songPlaying;

            if (SongName != PreviousSongName || ArtistName != PreviousArtistName)
            {
                UpdateListeningDataFile();
                activeSongInSeconds = 0;
            }

            PreviousSongName = SongName;
            PreviousArtistName = ArtistName;
        }

        public static void Heartbeat(CancellationToken token)
        {
            while (true)
            {
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(1000);
                    log.Write($"[Listening Data - Heartbeat] {SongName} - {ArtistName} - {activeSongInSeconds}");
                    if (string.IsNullOrEmpty(SongName) || string.IsNullOrEmpty(ArtistName)) { continue; }
                    if (SongPlaying)
                    {
                        activeSongInSeconds++;
                    }
                }
            }
        }
    }
}