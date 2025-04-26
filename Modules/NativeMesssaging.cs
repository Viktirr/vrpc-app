using System.Text;
using System.Text.Json;
using VRPC.Logging;
using VRPC.ListeningDataManager;
using VRPC.Globals;
using VRPC.Configuration;

namespace VRPC.NativeMessasing
{
    class NativeMessaging
    {
        public static void ConnectivityStatus(Dictionary<int, string> messageDictionary)
        {
            if (messageDictionary[0].Contains("Program"))
            {
                if (messageDictionary[1].Contains("Shutdown"))
                {
                    log.Write("[NativeMessaging] Possible shutdown requested.");
                    Program.RemoveLock();

                    ListeningData.UpdateListeningDataFile();

                    try
                    {
                        var data = File.ReadAllText(VRPCSettings.ListeningDataPath);
                        JsonSerializer.Deserialize<ListeningData.SongData>(data);
                        ListeningDataBackup.CreateBackup();
                    }
                    catch
                    {
                        log.Write("[NativeMessaging] Skipping backup due to corrupted file.");
                    }
                }
                else if (messageDictionary[1].Contains("Started"))
                {
                    log.Write("[NativeMessaging] Sent Hello status heartbeat to the extension");
                    NativeMessaging.SendMessage(NativeMessaging.EncodeMessage("Hello"));
                }
                else if (messageDictionary[1].Contains("Heartbeat"))
                {
                    log.Write("[NativeMessaging] Sent Alive status heartbeat to the extension");
                    NativeMessaging.SendMessage(NativeMessaging.EncodeMessage("Alive"));
                }
            }

        }
        static Log log = new Log();

        public static void UpdateRPCDataLegacy(string content)
        {
            // Legacy stored because this is how it was first built, later needs to get changed to json. Once the transition happens, I'm deleting this.
            VRPCGlobalData.RPCDataLegacyString = content;
            VRPCGlobalData.RPCDataLegacyDictionary = VRPCGlobalFunctions.LinesIntoDictionary(content);

            VRPCGlobalEvents.SendRichPresenceEvent();
        }

        public static string GetMessage()
        {
            int byteLength = 4;
            byte[] rawLength = new byte[byteLength];
            Console.OpenStandardInput().Read(rawLength, 0, byteLength);

            int messageLength = BitConverter.ToInt32(rawLength, 0);
            byte[] messageBytes = new byte[messageLength];
            Console.OpenStandardInput().Read(messageBytes, 0, messageLength);

            string message = Encoding.UTF8.GetString(messageBytes);
            string? finalMessage = "";
            try
            {
                finalMessage = JsonSerializer.Deserialize<string>(message);
            }
            catch 
            {
                log.Write("[NativeMessaging] Couldn't deserialize received json. Assuming connection is closed. Exiting.");
                Program.RemoveLock();

                ListeningData.UpdateListeningDataFile();
                try
                {
                    var data = File.ReadAllText(VRPCSettings.ListeningDataPath);
                    JsonSerializer.Deserialize<ListeningData.SongData>(data);
                    ListeningDataBackup.CreateBackup();
                }
                catch
                {
                    log.Write("[NativeMessaging] Skipping backup due to corrupted file.");
                }

                Environment.Exit(0);
            }
            return finalMessage;
        }

        public static byte[] EncodeMessage(string messageContent)
        {
            byte[] encodedContent = JsonSerializer.SerializeToUtf8Bytes(messageContent, new JsonSerializerOptions { WriteIndented = false });
            byte[] encodedLength = BitConverter.GetBytes(encodedContent.Length);
            byte[] result = new byte[encodedLength.Length + encodedContent.Length];
            Buffer.BlockCopy(encodedLength, 0, result, 0, encodedLength.Length);
            Buffer.BlockCopy(encodedContent, 0, result, encodedLength.Length, encodedContent.Length);
            return result;
        }

        public static void SendMessage(byte[] encodedMessage)
        {
            Console.OpenStandardOutput().Write(encodedMessage, 0, encodedMessage.Length);
            Console.OpenStandardOutput().Flush();
        }
    }
}