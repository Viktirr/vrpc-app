using System.Text;
using System.Text.Json;
using VRPC.Logging;
using VRPC.Globals;

namespace VRPC.NativeMessasing
{
    class NativeMessaging
    {
        // The browser frames every message as a 4 byte little endian length followed by that many
        // bytes of UTF-8 JSON. Our own frames are a few KB at most, so anything above
        // MaxMessageBytes is a corrupted length prefix rather than a message we should allocate for.
        private const int LengthPrefixBytes = 4;
        private const int MaxMessageBytes = 1024 * 1024;

        public enum ReadResult
        {
            Message,
            Skipped,
            Closed
        }

        private static readonly Stream StandardInput = Console.OpenStandardInput();
        private static readonly Stream StandardOutput = Console.OpenStandardOutput();
        private static readonly object SendLock = new object();

        public static void ConnectivityStatus(Dictionary<int, string> messageDictionary)
        {
            if (messageDictionary[0].Contains("Program"))
            {
                // A "Program" status without a second line carries no command.
                if (messageDictionary.Count < 2) { return; }

                if (messageDictionary[1].Contains("Shutdown"))
                {
                    log.Write("[NativeMessaging] Possible shutdown requested.");
                    Program.Shutdown(exit: false);
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

        // Reads one native messaging frame. Every failure mode is reported to the caller instead of
        // killing the process: a closed pipe, a corrupt length prefix, invalid JSON and a payload
        // that isn't a JSON string are all either expected on shutdown or safe to skip.
        public static ReadResult ReadMessage(out string message)
        {
            message = "";

            byte[] rawLength = new byte[LengthPrefixBytes];
            if (!ReadExactly(rawLength))
            {
                log.Write("[NativeMessaging] Input stream ended, the browser closed the connection.");
                return ReadResult.Closed;
            }

            int messageLength = BitConverter.ToInt32(rawLength, 0);
            if (messageLength <= 0 || messageLength > MaxMessageBytes)
            {
                // The length prefix can't be trusted, so every following read would be misaligned.
                log.Warn($"[NativeMessaging] Frame length of {messageLength} is out of range (1 - {MaxMessageBytes} bytes). Closing the connection.");
                return ReadResult.Closed;
            }

            byte[] messageBytes = new byte[messageLength];
            if (!ReadExactly(messageBytes))
            {
                log.Write("[NativeMessaging] Input stream ended in the middle of a message, the browser closed the connection.");
                return ReadResult.Closed;
            }

            string payload = Encoding.UTF8.GetString(messageBytes);

            string? decoded;
            try
            {
                decoded = JsonSerializer.Deserialize<string>(payload);
            }
            catch (Exception e)
            {
                log.Warn($"[NativeMessaging] Ignoring a frame that isn't valid JSON. Exception: {e.Message}");
                return ReadResult.Skipped;
            }

            if (decoded == null)
            {
                // Valid JSON, but not a string (a bare null, a number, an object, ...). Nothing to do.
                log.Warn($"[NativeMessaging] Ignoring a frame that isn't a JSON string: {payload}");
                return ReadResult.Skipped;
            }

            message = decoded;
            return ReadResult.Message;
        }

        // Stream.Read may return fewer bytes than requested, so keep reading until the buffer is
        // full. Returns false when the stream ends before that happens.
        private static bool ReadExactly(byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = StandardInput.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) { return false; }
                offset += read;
            }
            return true;
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
            // The browser can close the pipe at any moment, so a failed write must not take the app
            // down. The reader loop notices the closed connection on its next read.
            try
            {
                lock (SendLock)
                {
                    StandardOutput.Write(encodedMessage, 0, encodedMessage.Length);
                    StandardOutput.Flush();
                }
            }
            catch (Exception e)
            {
                log.Warn($"[NativeMessaging] Couldn't write to the browser, the connection is probably gone. Exception: {e.Message}");
            }
        }
    }
}