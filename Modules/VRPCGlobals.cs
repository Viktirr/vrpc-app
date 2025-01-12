using System.Text.RegularExpressions;

namespace VRPC.Globals
{
    public static class VRPCGlobalData
    {
        public static Dictionary<string, string> RPCData = new Dictionary<string, string>();
        public static Dictionary<int, string> RPCDataLegacyDictionary = new Dictionary<int, string>();
        public static string? RPCDataLegacyString;
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
                // Use regex to replace the matched pattern with an empty string
                value = Regex.Replace(value, pattern, "", RegexOptions.IgnoreCase).Trim();
            }

            return value;
        }
    }

    public static class VRPCGlobalEvents
    {
        public static event EventHandler? RPCEvent;

        public static void SendRichPresenceEvent()
        {
            RPCEvent?.Invoke(RPCEvent, EventArgs.Empty);
        }
    }
}