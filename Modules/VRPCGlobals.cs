using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;

namespace VRPC.Globals
{
    public static class VRPCGlobalData
    {
        public static Dictionary<string, string> RPCData = new Dictionary<string, string>();
        public static Dictionary<int, string> RPCDataLegacyDictionary = new Dictionary<int, string>();
        public static string? RPCDataLegacyString;

        public static string appVersion = "1.10dev";
        public static string appName = "VRPCApp";
        public static string exeName = "VRPC.exe";

        public static Dictionary<string, string> MiscellaneousSongData = new Dictionary<string, string>();
        public static Dictionary<string, string> LastListeningDataStats = new Dictionary<string, string>();
    }

    public static class VRPCGlobalFunctions
    {
        public static Dictionary<int, string> LinesIntoDictionary(string content)
        {
            Dictionary<int, string> dictionary = new Dictionary<int, string>();
            string[] lines = content.Split("\n");

            foreach (string line in lines)
            {
                dictionary.Add(dictionary.Count, line);
            }

            return dictionary;
        }

        public static bool IsRPCStringNull(string? value)
        {
            if (value == null || value == "" || value == "null") { return true; }
            else { return false; }
        }

        // Longest value we accept for a scraped time or duration (7 days). Anything above that is a
        // broken page value rather than a song, and clamping keeps the timestamp math in range.
        private const int MaxSeconds = 7 * 24 * 60 * 60;

        // Parses a time or duration that came from a web page. Those can be missing, empty, "null",
        // "NaN" or plain nonsense, so anything unparsable becomes 0 instead of throwing.
        public static int ParseSeconds(string? value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)) { return 0; }
            if (seconds < 0) { return 0; }
            if (seconds > MaxSeconds) { return MaxSeconds; }
            return seconds;
        }

        public static string RemoveArtistFromTitle(string songName, string artistName)
        {
            string value = songName;
            string escapedArtist = Regex.Escape(artistName);

            string[] patterns = new string[]
            {
                $@"^{escapedArtist}\s*[-|:]\s*",  // "Artist - Song Name" or "Artist : Song Name"
                $@"\s*[-|:]\s*{escapedArtist}$", // "Song Name - Artist" or "Song Name | Artist"
                $@"\|\s*{escapedArtist}$",       // "Song Name | Artist"
                $@"^{escapedArtist}\s*\|\s*",    // "Artist | Song Name"
            };

            foreach (string pattern in patterns)
            {
                value = Regex.Replace(value, pattern, "", RegexOptions.IgnoreCase).Trim();
            }

            return value;
        }

        public static float PercentageMatchingString(string string1, string string2)
        {
            // Should check how well 2 strings overlap, initially supposed to be used in identifying various versions of a song (i.e. YouTube's video/song switcher) in ListeningData.

            // Compares 2 words ahead from string2 against the current selected word in string1
            int amountOfWordsToCompare = 2;

            float percentage;
            Dictionary<int, float> string1PercentagesMatching = new Dictionary<int, float>();

            string string1_lower = string1.ToLower();
            string string2_lower = string2.ToLower();

            string[] string1_words = string1_lower.Split(" ");
            string[] string2_words = string2_lower.Split(" ");

            List<int> string1_charfoundchars_list;
            List<int> string2_charusedchars_list = new List<int>();

            for (int string1_currentword = 0; string1_currentword < string1_words.Length; string1_currentword++)
            {
                string1_charfoundchars_list = new List<int>();

                for (int string2_currentword = 0; string2_currentword < string2_words.Length; string2_currentword++)
                {
                    for (int string1_currentchar = 0; string1_currentchar < string1_words[string1_currentword].Length; string1_currentchar++)
                    {
                        for (int string2_currentchar = 0; string2_currentchar < string2_words[string2_currentword].Length; string2_currentchar++)
                        {
                            if (string2_words[string2_currentword][string2_currentchar] == string1_words[string1_currentword][string1_currentchar])
                            {
                                // Creates value
                                if (!string1PercentagesMatching.ContainsKey(string1_currentword))
                                {
                                    string1PercentagesMatching.Add(string1_currentword, 0f);
                                }

                                // Checks duplicates
                                if (string1_charfoundchars_list.Contains(string1_currentchar)) { continue; }

                                int currentCharTotalCheck = 0;
                                if (string2_currentword == 0) { currentCharTotalCheck = string2_currentchar; }
                                for (int string2_currentwordloop = string2_currentword - 1; string2_currentwordloop >= 0; string2_currentwordloop--)
                                {
                                    currentCharTotalCheck += string2_words[string2_currentwordloop].Length + string2_currentchar;
                                }

                                if (string2_charusedchars_list.Contains(currentCharTotalCheck)) { continue; }

                                // Selects 2 words for comparing
                                if (!(string2_currentword >= string1_currentword && string2_currentword <= string1_currentword + (amountOfWordsToCompare - 1))) { continue; }

                                // If passed checks above, this is a good character
                                // Console.WriteLine($"{string1_currentchar}({string1_words[string1_currentword][string1_currentchar]}) - {string2_currentchar}({string2_words[string2_currentword][string2_currentchar]})");

                                string1_charfoundchars_list.Add(string1_currentchar);

                                // Adds string2 used characters to a list so we don't repeat them
                                int currentCharTotal = 0;

                                if (string2_currentword == 0) { currentCharTotal = string2_currentchar; }
                                for (int string2_currentwordloop = string2_currentword - 1; string2_currentwordloop >= 0; string2_currentwordloop--)
                                {
                                    currentCharTotal += string2_words[string2_currentwordloop].Length + string2_currentchar;
                                }
                                string2_charusedchars_list.Add(currentCharTotal);

                                string1PercentagesMatching[string1_currentword] += 1f / string1_words[string1_currentword].Length;
                            }
                        }
                    }
                }
            }

            int counterPercentages = 0;
            float totalPercentages = 0f;
            foreach (float i in string1PercentagesMatching.Values)
            {
                counterPercentages++;
                totalPercentages += i;
            }

            percentage = totalPercentages / counterPercentages;

            return percentage;
        }

        public static string TruncateToBytes(string input, int maxBytes, Encoding? encoding = null)
        {
            if (input == null)
            {
                return null;
            }

            if (maxBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "Maximum bytes must be non-negative.");
            }

            if (maxBytes == 0)
            {
                return string.Empty;
            }

            encoding ??= Encoding.UTF8;

            if (encoding.GetByteCount(input) <= maxBytes)
            {
                return input;
            }

            int byteCount = 0;
            for (int i = 0; i < input.Length; i++)
            {
                int charByteCount = encoding.GetByteCount(input, i, 1);

                if (byteCount + charByteCount > maxBytes)
                {
                    return input.Substring(0, i);
                }

                byteCount += charByteCount;
            }

            return input;
        }
    }

    public static class VRPCGlobalEvents
    {
        public static event EventHandler? RPCEvent;

        public static void SendRichPresenceEvent()
        {
            RPCEvent?.Invoke(RPCEvent, EventArgs.Empty);
        }

        public static event EventHandler? RPForceUpdate;

        public static void SendForceUpdateRPEvent(int delay = 0)
        {
            if (delay == 0)
            {
                RPForceUpdate?.Invoke(RPForceUpdate, EventArgs.Empty);
            }
            else
            {
                Thread t = new Thread(() =>
                {
                    Thread.Sleep(delay);
                    RPForceUpdate?.Invoke(RPForceUpdate, EventArgs.Empty);
                });
                t.Start();
            }
        }

        public static event EventHandler? RPClear;

        public static void SendRichPresenceClearEvent(int delay = 0)
        {
            if (delay == 0)
            {
                RPClear?.Invoke(RPClear, EventArgs.Empty);
            }
            else
            {
                Thread t = new Thread(() =>
                {
                    Thread.Sleep(delay);
                    RPClear?.Invoke(RPClear, EventArgs.Empty);
                });
                t.Start();
            }
        }
    }
}