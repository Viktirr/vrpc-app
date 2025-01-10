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