using System.Text.Json;
using System.Text.RegularExpressions;
using VRPC.Configuration;
using VRPC.Logging;
using System.Text;
using VRPC.Globals;

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
        protected static bool SongPlaying = false;

        private static string filePath = VRPCSettings.ListeningDataPath;
        private static bool ErrorReadingFromFile = true;
        private static bool ErrorWritingToFile = true;
        private static DateTime LastDataUpdate;

        private static float matchingTextThreshold = 0.8f;

        protected class SongData
        {
            // "TotalListened": 0
            // "SongsData": {
            // ["SongName_ArtistName"]: [{"name":"SongName"},{"author":"ArtistName"},{"timelistened":"SongTotalSeconds"}]
            // }
            public string versionNumber { get; set; } = VRPCGlobalData.appVersion;
            public int TotalListened { get; set; } = 0;
            public Dictionary<string, Dictionary<string, string>> SongsData { get; set; } = new Dictionary<string, Dictionary<string, string>>();

            public void AddSong(string songName, string artistName, int songTotalSeconds, bool recursive = false)
            {
                if (string.IsNullOrEmpty(songName)) { return; }
                string pattern = @"[^a-zA-Z.,!?']";
                string songNameClean = Regex.Replace(songName, pattern, "");
                string artistNameClean = Regex.Replace(artistName, pattern, "");

                string key = $"{songNameClean}_{artistNameClean}";

                if (key == "_") { key = $"{songName}_{artistName}"; }

                int currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                string isVideo = "false";
                if (VRPCGlobalData.MiscellaneousSongData.ContainsKey("isvideo")) { isVideo = VRPCGlobalData.MiscellaneousSongData["isvideo"]; }

                string songURL = "Unknown";
                if (VRPCGlobalData.MiscellaneousSongData.ContainsKey("songurl")) { songURL = VRPCGlobalData.MiscellaneousSongData["songurl"]; }

                string platform = "Unknown";
                if (VRPCGlobalData.MiscellaneousSongData.ContainsKey("platform")) { platform = VRPCGlobalData.MiscellaneousSongData["platform"]; }

                if (SongsData.ContainsKey(key))
                {
                    try
                    {
                        SongsData[key]["timelistened"] = (int.Parse(SongsData[key]["timelistened"]) + songTotalSeconds).ToString();
                    }
                    catch { log.Error($"[ListeningData] Couldn't get Time Listened for {key}. It's time won't be updated if it doesn't exist."); }

                    try
                    {
                        if (int.Parse(SongsData[key]["lastplayed"]) / 86400 != currentTime / 86400)
                        {
                            if (int.Parse(SongsData[key]["daylastplayedtimelistened"]) > int.Parse(SongsData[key]["daymostplayedtimelistened"]))
                            {
                                SongsData[key]["daymostplayed"] = (int.Parse(SongsData[key]["lastplayed"]) / 86400).ToString();
                                SongsData[key]["daymostplayedtimelistened"] = SongsData[key]["daylastplayedtimelistened"];
                            }
                            SongsData[key]["daylastplayedtimelistened"] = activeSongInSeconds.ToString();
                            SongsData[key]["lastplayed"] = currentTime.ToString();
                        }
                        else
                        {
                            SongsData[key]["daylastplayedtimelistened"] = (int.Parse(SongsData[key]["daylastplayedtimelistened"]) + songTotalSeconds).ToString();
                        }
                    }
                    catch { log.Warn($"[ListeningData] Couldn't change most played or last played. If this message is shown only once per song, there's nothing to worry about."); }

                    if (!SongsData[key].ContainsKey("name")) { SongsData[key]["name"] = songName; }
                    if (!SongsData[key].ContainsKey("author")) { SongsData[key]["author"] = artistName; }
                    if (!SongsData[key].ContainsKey("key")) { SongsData[key]["key"] = key; }
                    if (!SongsData[key].ContainsKey("timelistened")) { SongsData[key]["timelistened"] = songTotalSeconds.ToString(); }
                    if (!SongsData[key].ContainsKey("firstlistened")) { SongsData[key]["firstlistened"] = (currentTime - songTotalSeconds).ToString(); }
                    if (!SongsData[key].ContainsKey("lastplayed")) { SongsData[key]["lastplayed"] = currentTime.ToString(); }
                    if (!SongsData[key].ContainsKey("daylastplayedtimelistened")) { SongsData[key]["daylastplayedtimelistened"] = songTotalSeconds.ToString(); }
                    if (!SongsData[key].ContainsKey("daymostplayed")) { SongsData[key]["daymostplayed"] = (currentTime / 86400).ToString(); }
                    if (!SongsData[key].ContainsKey("daymostplayedtimelistened")) { SongsData[key]["daymostplayedtimelistened"] = songTotalSeconds.ToString(); }
                    SongsData[key]["lastplatformlistenedon"] = platform;
                    if (!SongsData[key].ContainsKey("isvideo")) { SongsData[key]["isvideo"] = isVideo; }
                    if (!SongsData[key].ContainsKey("songurl")) { SongsData[key]["songurl"] = songURL; }

                    if (SongsData[key].ContainsKey("songurl")) { if (SongsData[key]["songurl"].Contains("Unknown")) { SongsData[key]["songurl"] = songURL; } }

                    SongsData[key]["lastplayed"] = currentTime.ToString();
                }
                else if (isVideo == "true" && recursive == false)
                {
                    foreach (string currentKey in SongsData.Keys)
                    {
                        float currentMatchingPercentage1 = VRPCGlobalFunctions.PercentageMatchingString(key, currentKey);
                        float currentMatchingPercentage2 = VRPCGlobalFunctions.PercentageMatchingString(currentKey, key);
                        float currentMatchingPercentage = (currentMatchingPercentage1 + currentMatchingPercentage2) / 2;
                        if (currentMatchingPercentage >= matchingTextThreshold)
                        {
                            log.Info($"[ListeningData] Found {key} in {currentKey} with {currentMatchingPercentage * 100} percent matching. Using the latter to save values instead.");

                            string _songName = "";
                            if (SongsData[currentKey].ContainsKey("name"))
                            {
                                _songName = SongsData[currentKey]["name"];
                            }
                            else { return; }

                            string _artistName = "";
                            if (SongsData[currentKey].ContainsKey("author"))
                            {
                                _artistName = SongsData[currentKey]["author"];
                            }
                            else { return; }

                            log.Write($"[ListeningData] Song name: {_songName}, Artist Name: {_artistName}");
                            AddSong(_songName, _artistName, songTotalSeconds, true);
                            return;
                        }
                    }

                    CreateNewSongData();
                }
                else
                {
                    CreateNewSongData();
                }

                void CreateNewSongData()
                {
                    SongsData[key] = new Dictionary<string, string>
                    {
                        { "name", songName },
                        { "author", artistName },
                        { "key", key },
                        { "timelistened", songTotalSeconds.ToString() },
                        { "firstlistened", (currentTime - songTotalSeconds).ToString() },
                        { "lastplayed", currentTime.ToString() },
                        { "daylastplayedtimelistened", songTotalSeconds.ToString() },
                        { "daymostplayed", (currentTime / 86400).ToString() },
                        { "daymostplayedtimelistened", songTotalSeconds.ToString() },
                        { "isvideo", isVideo },
                        { "songurl", songURL }
                    };
                }

                VRPCGlobalData.LastListeningDataStats = SongsData[key];
            }
        }

        protected static SongData ReadDataFile()
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
                log.Warn("[ListeningData] Couldn't read from file. Maybe the file is corrupted?");
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
            songData.versionNumber = VRPCGlobalData.appVersion;
            return songData;
        }

        private static SongData SaveDataFile(SongData songData)
        {
            try
            {
                string jsonString = JsonSerializer.Serialize(songData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, jsonString, Encoding.UTF8);
                ErrorWritingToFile = false;
            }
            catch { log.Error("[ListeningData] Couldn't write to file"); ErrorWritingToFile = true; }
            return songData;
        }

        public static void UpdateListeningDataFile()
        {
            if (VRPCSettings.settingsData.EnableListeningData == false) { activeSongInSeconds = 0; return; }
            SongData songData = ReadDataFile();
            bool fileExists = CheckDataFileExists();
            if (!fileExists)
            {
                songData = UpdateSongData(songData);
                SaveDataFile(songData);
                ErrorReadingFromFile = false;
                activeSongInSeconds = 0;
                return;
            }

            if (ErrorReadingFromFile == true)
            {
                ErrorReadingFromFile = false;
                return;
            }

            if (ErrorWritingToFile == true)
            {
                ErrorWritingToFile = false;
                return;
            }

            songData = UpdateSongData(songData);
            SaveDataFile(songData);
            log.Info($"[ListeningData] Saved/updated.");
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

        public static void UpdateListeningDataRPC()
        {
            if (VRPCSettings.settingsData.ShowcaseDataToRPC == false) { return; }

            ListeningDataRPC.UpdateRPC();
        }

        public static void Heartbeat(CancellationToken token, bool ShutdownRequested = false)
        {
            bool SavingEnabled = true;

            while (true)
            {
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(1000);

                    UpdateListeningDataRPC();

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