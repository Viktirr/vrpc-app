using VRPC.Logging;
using VRPC.Configuration;
using VRPC.ListeningDataManager;
using VRPC.Globals;
using DiscordRPC;

namespace VRPC.DiscordRPCManager.Activities
{
    class YouTubeMusic : SetDiscordActivity
    {
        private const int delayRichPresence = 2000;
        private static void UpdateListeningData(string? songName, string? artistName, string? songStatus)
        {
            if (songName == null || songName == "Browsing" || songName == "" || artistName == null || songStatus == null) { return; }
            bool songPlaying = songStatus == "Playing" ? true : false;
            ListeningData.UpdateListeningData(songName, artistName, songPlaying);
        }
        static Log log = new Log();

        private static bool IsNull(string? str)
        {
            if (str == null || str == "null" || str == "") { return true; }
            else { return false; }
        }

        private static bool IsVideo(string? albumName, string? releaseYear)
        {
            if (albumName == null || releaseYear == null) { return true; }
            try
            {
                if (albumName.Contains("view") && releaseYear.Contains("like"))
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void UpdateImage(string? songId, string? smallSongBanner)
        {
            try
            {
                if (songId == null || songId == "" || songId == "null")
                {
                    if (smallSongBanner == null || smallSongBanner == "" || smallSongBanner == "null")
                    {
                        richPresence.Assets.LargeImageKey = "ytmusic-vrpc";
                    }
                    else
                    {
                        richPresence.Assets.LargeImageKey = $"{smallSongBanner}";
                    }
                }
                else
                {
                    richPresence.Assets.LargeImageKey = $"https://img.youtube.com/vi/{songId}/3.jpg";
                }
            }
            catch (Exception e)
            {
                log.Warn($"[YouTube Music] Couldn't get songId and set LargeImage appropriately. Exception {e.Data}");
                richPresence.Assets.LargeImageKey = "ytmusic-rpc";
            }
        }
        private static void UpdateStatus(string? songStatus, int songInSecondsCurrent, string watermarkString)
        {
            if (VRPCSettings.settingsData.ShowPlayingStatus)
            {
                if (songStatus != "Playing" && songStatus != "Paused")
                {
                    richPresence.Assets.SmallImageKey = "";
                    richPresence.Assets.SmallImageText = "" + watermarkString;
                }
                if (songStatus == "Playing")
                {
                    richPresence.Assets.SmallImageKey = "playing";
                    richPresence.Assets.SmallImageText = "Playing" + watermarkString;
                }
                else if (songStatus == "Paused")
                {
                    richPresence.Assets.SmallImageKey = "paused";
                    richPresence.Assets.SmallImageText = "Paused" + watermarkString;
                    richPresence.Timestamps.Start = DateTime.UtcNow - TimeSpan.FromSeconds(songInSecondsCurrent);
                    richPresence.Timestamps.End = DateTime.UtcNow;
                }
            }
            else
            {
                try
                {
                    if (songStatus != "Playing" && songStatus != "Paused")
                    {
                        richPresence.Assets.SmallImageKey = "";
                        richPresence.Assets.SmallImageText = "";
                    }
                    if (songStatus == "Playing")
                    {
                        if (VRPCGlobalData.MiscellaneousSongData.ContainsKey("status"))
                        {
                            if (songStatus != VRPCGlobalData.MiscellaneousSongData["status"])
                            {
                                VRPCGlobalEvents.SendForceUpdateRPEvent(delayRichPresence);
                            }
                        }
                        VRPCGlobalData.MiscellaneousSongData["status"] = "Playing";
                        richPresence.Assets.SmallImageKey = "";
                        richPresence.Assets.SmallImageText = "";
                    }
                    else if (songStatus == "Paused")
                    {
                        VRPCGlobalData.MiscellaneousSongData["status"] = "Paused";
                        VRPCGlobalEvents.SendRichPresenceClearEvent(delayRichPresence);
                    }
                }
                catch { }
            }
        }

        private static (int songInSecondsCurrent, int songInSeconds, string[] songDuration) UpdateTimestamps(string? songDurationRaw)
        {
            string[] songDuration = Array.Empty<string>();

            int songInSecondsCurrent = 0;
            int songInSeconds = 0;

            try
            {
                if (songDurationRaw != null)
                {
                    songDuration = songDurationRaw.Split("/");
                }
            }
            catch (Exception e)
            {
                log.Error($"[Youtube Music] Couldn't split the song duration into two parts. Exception {e.Data}");
            }

            try
            {
                for (int i = 0; i < songDuration.Length; i++)
                {
                    string currentDuration = songDuration[i];
                    string[] timeSeparator = currentDuration.Split(":");

                    if (i == 0)
                    {
                        for (int j = timeSeparator.Length - 1; j >= 0; j--)
                        {
                            if (j == 1)
                            {
                                songInSecondsCurrent = int.Parse(timeSeparator[j]);
                            }
                            if (j == 0)
                            {
                                songInSecondsCurrent = songInSecondsCurrent + int.Parse(timeSeparator[j]) * 60;
                            }
                        }
                    }

                    if (i == 1)
                    {
                        for (int j = timeSeparator.Length - 1; j >= 0; j--)
                        {
                            if (j == 1)
                            {
                                songInSeconds = int.Parse(timeSeparator[j]);
                            }
                            if (j == 0)
                            {
                                songInSeconds = songInSeconds + int.Parse(timeSeparator[j]) * 60;
                            }
                        }
                    }
                }
            }
            catch (Exception e) { log.Warn($"[Youtube Music] Couldn't get the song duration, will use a previously set value. Error: {e.Data}"); }

            return (songInSecondsCurrent, songInSeconds, songDuration);
        }


        private static void UpdateAlbum(string? albumName, string? releaseYear, bool isVideo = false)
        {
            try
            {
                if (VRPCSettings.settingsData.ShowcaseDataToRPC == true)
                {
                    ListeningDataRPC.ShowcaseTimeListened();
                }
                else if (IsNull(albumName) && IsNull(releaseYear) || isVideo == true)
                {
                    richPresence.Assets.LargeImageText = "Listening on YouTube Music";
                }
                else if (IsNull(releaseYear))
                {
                    richPresence.Assets.LargeImageText = $"{albumName}";
                }
                else if (IsNull(albumName))
                {
                    richPresence.Assets.LargeImageText = $"{releaseYear}";
                }
                else
                {
                    richPresence.Assets.LargeImageText = $"{albumName} | {releaseYear}";
                }
            }
            catch { log.Write("[YouTube Music] Something went wrong updating album to Rich Presence."); }
        }

        private static void UpdateButton(string? songId)
        {
            try
            {
                if (songId == null || songId == "null" || songId == "" || VRPCSettings.settingsData.EnableListeningToButton == false)
                {
                    richPresence.Buttons = null;
                    return;
                }
                else
                {
                    string songURL = $"https://music.youtube.com/watch?v={songId}";
                    VRPCGlobalData.MiscellaneousSongData["songurl"] = songURL;
                    richPresence.Buttons = new Button[]
                    {
                        new Button()
                        {
                            Label = "Listen on YouTube Music", Url=songURL
                        }
                    };
                }
            }
            catch { log.Write("[YouTube Music] Something went wrong updating buttons to Rich Presence"); }
        }

        public static void UpdateRPC()
        {
            string tempRichPresenceDetails = richPresence.Details;
            string tempRichPresenceSmallImageText = richPresence.Assets.SmallImageText;
            DateTime? tempRichPresenceStart = richPresence.Timestamps.Start;

            int timestampTolerance = 5;
            string watermarkString = VRPCSettings.settingsData.ShowAppWatermark ? $" | vrpc v{VRPCGlobalData.appVersion}" : "";
            string songName = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(1, "");

            try
            {
                if (songName == "" || songName == "null" || songName == null)
                {
                    songName = "Browsing";

                    richPresence.Details = "Main menu";
                    richPresence.State = "Browsing";
                    richPresence.Assets.LargeImageKey = "ytmusic-vrpc";
                    richPresence.Assets.LargeImageText = "YouTube Music";
                    return;
                }
            }
            catch { log.Write("[YouTube Music] Something went wrong upon setting Rich Presence to Browsing."); }

            string artistName = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(2, "");
            string? songDurationRaw = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(3);
            string? songStatus = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(4);
            string? songId = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(5);
            string? smallSongBanner = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(6);
            string? albumName = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(7);
            string? releaseYear = VRPCGlobalData.RPCDataLegacyDictionary.GetValueOrDefault(8);

            if (songName != null && songName.Length > 56) { songName = songName.Substring(0, 56); }
            if (artistName != null && artistName.Length > 56) { artistName = artistName.Substring(0, 56); }
            if (songDurationRaw == null) { songDurationRaw = ""; }

            int songInSecondsCurrent;
            int songInSeconds;
            string[] songDuration;

            (songInSecondsCurrent, songInSeconds, songDuration) = UpdateTimestamps(songDurationRaw);

            string cleanSongName = "";
            try
            {
                if (!string.IsNullOrEmpty(songName) && !string.IsNullOrEmpty(artistName))
                {
                    cleanSongName = VRPCGlobalFunctions.RemoveArtistFromTitle(songName, artistName);
                }

                if (!string.IsNullOrEmpty(cleanSongName))
                {
                    richPresence.Details = cleanSongName;
                }
                else if (!string.IsNullOrEmpty(songName))
                {
                    richPresence.Details = songName;
                }

                if (!string.IsNullOrEmpty(artistName))
                {
                    richPresence.State = artistName;
                }
            }
            catch { log.Write("[YouTube Music] Something went wrong setting artist and song name to Rich Presence"); }

            try
            {
                if (songDuration[0] != null || songDuration[1] != null)
                {
                    richPresence.Timestamps.Start = DateTime.UtcNow - TimeSpan.FromSeconds(songInSecondsCurrent);
                    richPresence.Timestamps.End = DateTime.UtcNow + TimeSpan.FromSeconds(songInSeconds - songInSecondsCurrent);
                }
            }
            catch { log.Write("[YouTube Music] Something went wrong setting timestamps to Rich Presence"); }

            bool isVideo = IsVideo(albumName, releaseYear);

            VRPCGlobalData.MiscellaneousSongData["platform"] = "YouTube Music";
            if (isVideo == true) { VRPCGlobalData.MiscellaneousSongData["isvideo"] = "true"; }
            VRPCGlobalData.MiscellaneousSongData["songduration"] = songInSeconds.ToString();

            UpdateListeningData(songName, artistName, songStatus);
            UpdateStatus(songStatus, songInSecondsCurrent, watermarkString);
            UpdateImage(songId, smallSongBanner);
            UpdateAlbum(albumName, releaseYear, isVideo);
            UpdateButton(songId);

            if (tempRichPresenceStart > DateTime.UtcNow - TimeSpan.FromSeconds(songInSecondsCurrent) + TimeSpan.FromSeconds(timestampTolerance) || tempRichPresenceStart < DateTime.UtcNow - TimeSpan.FromSeconds(songInSecondsCurrent) - TimeSpan.FromSeconds(timestampTolerance))
            {
                log.Write("[YouTube Music] Rich Presence update from Timestamp");
                VRPCGlobalEvents.SendForceUpdateRPEvent(delayRichPresence);
                return;
            }

            if (songStatus + watermarkString != tempRichPresenceSmallImageText && VRPCSettings.settingsData.ShowPlayingStatus)
            {
                log.Write("[YouTube Music] Rich Presence update from status");
                VRPCGlobalEvents.SendForceUpdateRPEvent(delayRichPresence);
                return;
            }

            if (cleanSongName != songName)
            {
                if (tempRichPresenceDetails != cleanSongName && tempRichPresenceStart != richPresence.Timestamps.Start)
                {
                    log.Write("[YouTube Music] Rich Presence update from song name or timestamp.");
                    VRPCGlobalData.MiscellaneousSongData.Clear();
                    VRPCGlobalEvents.SendForceUpdateRPEvent(delayRichPresence);
                    return;
                }
            }
            else
            {
                if (tempRichPresenceDetails != songName && tempRichPresenceStart != richPresence.Timestamps.Start)
                {
                    log.Write("[YouTube Music] Rich Presence update from song name or timestamp.");
                    VRPCGlobalData.MiscellaneousSongData.Clear();
                    VRPCGlobalEvents.SendForceUpdateRPEvent(delayRichPresence);
                    return;
                }
            }
        }
    }
}
