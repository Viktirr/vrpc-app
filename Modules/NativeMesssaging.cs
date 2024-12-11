using System;
using System.Text;
using System.Text.Json;
using VRPC.Logging;


// Copy from Program.cs and also add a connected flag
namespace VRPC.NativeMessasing
{
    class NativeMessaging
    {
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
            return JsonSerializer.Deserialize<string>(message);
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