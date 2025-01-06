using System.IO;
using System.Text.Encodings;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using VRPC.Configuration;
using VRPC.Logging;
using System.Text;
using System.Reflection.Metadata;

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

        private static string filePath = VRPCSettings.ListeningDataPath;
        private static bool ErrorReadingFromFile = true;
        private static DateTime LastDataUpdate;

        private class SongData
        {
            // "TotalListened": 0
            // "SongsData": {
            // ["SongName_ArtistName"]: [{"name":"SongName"},{"author":"ArtistName"},{"timelistened":"SongTotalSeconds"}]
            // }
            public string versionNumber { get; set; } = "0.11";

            public int TotalListened { get; set; } = 0;
            public Dictionary<string, Dictionary<string, string>> SongsData { get; set; } = new Dictionary<string, Dictionary<string, string>>();

            public void AddSong(string songName, string artistName, int songTotalSeconds)
            {
                if (string.IsNullOrEmpty(songName)) { return; }
                string pattern = @"[^a-zA-Z.,!?']";
                string songNameClean = Regex.Replace(songName, pattern, "");
                string artistNameClean = Regex.Replace(artistName, pattern, "");

                string key = $"{songNameClean}_{artistNameClean}";

                if (SongsData.ContainsKey(key))
                {
                    try
                    {
                        SongsData[key]["timelistened"] = (int.Parse(SongsData[key]["timelistened"]) + songTotalSeconds).ToString();
                    }
                    catch { log.Error($"[ListeningData] Couldn't get Time Listened for {key}. It's time won't be updated."); }
                }
                else
                {
                    SongsData[key] = new Dictionary<string, string>
                    {
                        { "name", songName },
                        { "author", artistName },
                        { "timelistened", songTotalSeconds.ToString() }
                    };
                }
            }
        }

        private static SongData ReadDataFile()
        {
            SongData songData = new SongData();
            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                songData = JsonSerializer.Deserialize<SongData>(json) ?? new SongData();
                ErrorReadingFromFile = false;
            }
            catch
            {
                log.Warn("[ListeningData] Couldn't read from file. Won't update, maybe the file is corrupted?");
                ErrorReadingFromFile = true;
                return songData;
            }
            return songData;
        }

        private static bool CheckDataFileExists()
        {
            if (File.Exists(filePath)) { return true; }
            return false;
        }

        private static SongData UpdateSongData(SongData songData)
        {
            songData.AddSong(PreviousSongName, PreviousArtistName, activeSongInSeconds);
            songData.TotalListened += activeSongInSeconds;
            return songData;
        }

        private static SongData SaveDataFile(SongData songData)
        {
            try
            {
                string jsonString = JsonSerializer.Serialize(songData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, jsonString, Encoding.UTF8);
            }
            catch { log.Error("[ListeningData] Couldn't write to file"); }
            return songData;
        }

        public static void UpdateListeningDataFile()
        {
            SongData songData = ReadDataFile();
            bool fileExists = CheckDataFileExists();
            if (!fileExists) {
                songData = UpdateSongData(songData);
                SaveDataFile(songData);
                ErrorReadingFromFile = false;
                activeSongInSeconds = 0;
                return;
            }

            if (ErrorReadingFromFile)
            {
                ErrorReadingFromFile = false;
                return;
            }
            log.Info($"[ListeningData] Attempting to save to file.");

            songData = UpdateSongData(songData);
            SaveDataFile(songData);
            activeSongInSeconds = 0;
        }

        public static void UpdateListeningData(string _songName, string _artistName, bool _songPlaying = false)
        {
            LastDataUpdate = DateTime.UtcNow;
            SongName = _songName;
            ArtistName = _artistName;
            SongPlaying = _songPlaying;

            if (SongName != PreviousSongName || ArtistName != PreviousArtistName)
            {
                UpdateListeningDataFile();
            }

            PreviousSongName = SongName;
            PreviousArtistName = ArtistName;
        }

        public static void Heartbeat(CancellationToken token, bool ShutdownRequested = false)
        {
            bool SavingEnabled = true;

            while (true)
            {
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(1000);
                    if (string.IsNullOrEmpty(SongName) || string.IsNullOrEmpty(ArtistName)) { continue; }
                    if (SongPlaying && ((DateTime.UtcNow - LastDataUpdate) < TimeSpan.FromSeconds(6)))
                    {
                        activeSongInSeconds++;
                        log.Write($"[ListeningData - Heartbeat] {SongName} - {ArtistName} - {activeSongInSeconds} - Active");
                        SavingEnabled = true;
                        if (activeSongInSeconds >= 60) { UpdateListeningDataFile(); }
                        continue;
                    }
                    log.Write($"[ListeningData - Heartbeat] {SongName} - {ArtistName} - {activeSongInSeconds} - Inactive");
                    if (SavingEnabled)
                    {
                        UpdateListeningDataFile();
                        SavingEnabled = false;
                    }
                }
                return;
            }
        }
    }
}