using System;
using VRPC.Logging;
using VRPC.Configuration;

namespace VRPC.DiscordRPCManager.Activities
{
    class YouTubeMusic : SetDiscordActivity
    {
        static Log log = new Log();
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
                log.Write($"Couldn't get songId and set LargeImage appropriately. Exception {e.Data}");
                richPresence.Assets.LargeImageKey = "ytmusic-rpc";
            }
        }
        private static void UpdateStatus(string? songStatus, int songInSecondsCurrent)
        {
            if (songStatus != "Playing" && songStatus != "Paused")
            {
                richPresence.Assets.SmallImageKey = "";
                richPresence.Assets.SmallImageText = "";
            }
            try
            {
                if (songStatus == "Playing")
                {
                    richPresence.Assets.SmallImageKey = "playing";
                    richPresence.Assets.SmallImageText = "Playing";
                }
                else if (songStatus == "Paused")
                {
                    richPresence.Assets.SmallImageKey = "paused";
                    richPresence.Assets.SmallImageText = "Paused";
                    richPresence.Timestamps.Start = DateTime.UtcNow - TimeSpan.FromSeconds(songInSecondsCurrent);
                    richPresence.Timestamps.End = DateTime.UtcNow;
                }
            }
            catch { }
        }

        private static string[] UpdateTimestamps(string? songDurationRaw)
        {
            // TBD: Switch Timestamps to this function
            string[] songDuration = Array.Empty<string>();
            return songDuration;
        }

        public static void UpdateRPC()
        {
            using (StreamReader sr = new StreamReader(VRPCSettings.RPCInfoPath))
            {
                sr.ReadLine();
                string? songName = sr.ReadLine()?.Trim();

                if (songName == "" || songName == "null" || songName == null)
                {
                    richPresence.Details = "Main menu";
                    richPresence.State = "Browsing";
                    richPresence.Assets.LargeImageKey = "ytmusic-vrpc";
                    richPresence.Assets.LargeImageText = "YouTube Music";
                    return;
                }

                string? artistName = sr.ReadLine()?.Trim();
                string? songDurationRaw = sr.ReadLine()?.Trim();
                string? songStatus = sr.ReadLine()?.Trim();
                string? songId = "";
                string? smallSongBanner = "";
                try { songId = sr.ReadLine()?.Trim(); } catch (Exception e) { log.Write($"No song id found while attempting to read songId. Exception {e.Data}"); }
                try { smallSongBanner = sr.ReadLine()?.Trim(); } catch (Exception e) { log.Write($"No banner found while attempting to read banner. Exception {e.Data}"); }

                if (songName != null && songName.Length > 64) { songName = songName.Substring(0, 64); }
                if (artistName != null && artistName.Length > 64) { artistName = artistName.Substring(0, 64); }
                if (songDurationRaw == null) { songDurationRaw = ""; }

                string[] songDuration = { };

                int songInSecondsCurrent = 0;
                int songInSeconds = 0;

                try { songDuration = songDurationRaw.Split("/"); } catch (Exception e) { log.Write($"Couldn't split the song duration into two parts. Exception {e.Data}"); return; }

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
                                    songInSecondsCurrent = int.Parse((timeSeparator[j]));
                                }
                                if (j == 0)
                                {
                                    songInSecondsCurrent = songInSecondsCurrent + int.Parse((timeSeparator[j])) * 60;
                                }
                            }
                        }

                        if (i == 1)
                        {
                            for (int j = timeSeparator.Length - 1; j >= 0; j--)
                            {
                                if (j == 1)
                                {
                                    songInSeconds = int.Parse((timeSeparator[j]));
                                }
                                if (j == 0)
                                {
                                    songInSeconds = songInSeconds + int.Parse((timeSeparator[j])) * 60;
                                }
                            }
                        }
                    }
                }
                catch (Exception e) { log.Write($"Couldn't get the song duration, will use a previously set value. Error: {e.Data}"); }

                if (!string.IsNullOrEmpty(songName))
                {
                    richPresence.Details = songName;
                }

                if (!string.IsNullOrEmpty(artistName))
                {
                    richPresence.State = artistName;
                }

                try
                {
                    if (songDuration[0] != null || songDuration[1] != null)
                    {
                        richPresence.Timestamps.Start = DateTime.UtcNow - TimeSpan.FromSeconds(songInSecondsCurrent);
                        richPresence.Timestamps.End = DateTime.UtcNow + TimeSpan.FromSeconds(songInSeconds - songInSecondsCurrent);
                    }
                }
                catch { }

                UpdateStatus(songStatus, songInSecondsCurrent);
                UpdateImage(songId, smallSongBanner);
                richPresence.Assets.LargeImageText = "Listening on YouTube Music";
            }
        }
    }
}