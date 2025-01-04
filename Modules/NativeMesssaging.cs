using System.Text;
using System.Text.Json;
using VRPC.Logging;
using VRPC.ListeningDataManager;

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
                    log.Write("[Main] Possible shutdown requested.");
                    CancellationToken ct = new CancellationToken();
                    ListeningData.Heartbeat(ct, true);
                }
                else if (messageDictionary[1].Contains("Started"))
                {
                    NativeMessaging.SendMessage(NativeMessaging.EncodeMessage("Hello"));
                }
                else if (messageDictionary[1].Contains("Heartbeat"))
                {
                    NativeMessaging.SendMessage(NativeMessaging.EncodeMessage("Alive"));
                }
            }

        }
        static Log log = new Log();

        public static void UpdateRPCFile(string filePath, string content)
        {
            try
            {
                using (FileStream fs = File.Create(filePath))
                {
                    byte[] contentByte = new UTF8Encoding(true).GetBytes(content);
                    fs.Write(contentByte, 0, contentByte.Length);
                }
            }
            catch (Exception e) { SendMessage(EncodeMessage("Something went wrong updating the file RPCInfo.txt " + e.Data)); log.Write($"Something went wrong updating the file RPCInfo.txt, {e.Data}"); }
        }

        public static string GetMessage()
        {
            int byteLength = 4;
            byte[] rawLength = new byte[byteLength];
            Console.OpenStandardInput().Read(rawLength, 0, byteLength);

            // if (byteLength == 0) { Environment.Exit(0); } // Closes the application if standard input is closed (no bytes have been transferred).

            int messageLength = BitConverter.ToInt32(rawLength, 0);
            byte[] messageBytes = new byte[messageLength];
            Console.OpenStandardInput().Read(messageBytes, 0, messageLength);

            string message = Encoding.UTF8.GetString(messageBytes);
            string? finalMessage = "";
            try { finalMessage = JsonSerializer.Deserialize<string>(message); }
            catch { log.Write("[NativeMessaging] Couldn't deserialize received json. Assuming connection is closed. Exiting."); Environment.Exit(0); }
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