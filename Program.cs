using System;
using System.Linq;
using VRPC.DiscordRPCManager;
using VRPC.Logging;
using VRPC.NativeMessasing;
using VRPC.Configuration;
using DiscordRPC;

class Program
{
    private static Log log = new Log();
    public static bool isDiscordRPCRunning = false;
    private static string currentService = "";
    private static Thread? discordRPCThread;
    private static CancellationTokenSource discordCancellationTokenSource = new CancellationTokenSource();
    private static CancellationToken discordCancellationToken = discordCancellationTokenSource.Token;

    static void StatusUpdate(string message)
    {
        int newLineCount = message.Count(c => c == '\n');
        int seekFrom = 0;
        string currentLine;

        for (int i = 0; i < (newLineCount + 1); i++) {
            int nextNewLine = message.IndexOf("\n", seekFrom);

            if (nextNewLine == -1) { currentLine = message.Substring(seekFrom); }
            else { currentLine = message.Substring(seekFrom, nextNewLine); }
            
            seekFrom = nextNewLine + 1;

            if (i == 0) { currentService = currentLine; }
            if (currentLine == "Opened" && i == 1) {
                if (isDiscordRPCRunning != true)
                {
                    discordCancellationTokenSource = new CancellationTokenSource();
                    discordCancellationToken = discordCancellationTokenSource.Token;
                    log.Write("Calling StartDiscordRPC");
                    StartDiscordRPC();
                    return;
                }
                log.Write($"Got status opened for {currentService}, however Discord RPC is already running. Try again later.");
            }
            if (currentLine == "Closed" && i == 1) {
                try { discordCancellationTokenSource.Cancel(); } catch (Exception e) { log.Write($"Couldn't cancel Cancellation Token for Discord RPC, probably already cancelling? Exception {e.Data}"); }
            }
        }
    }

    static void UseDiscordRPC(CancellationToken token)
    {
        log.Write("Attempting to start Discord RPC");
        isDiscordRPCRunning = true;
        DiscordRPCManager discordRPC = new DiscordRPCManager();
        discordRPC.Init(currentService);
        discordRPC.Start(token);
        while (!token.IsCancellationRequested) {
            Thread.Sleep(500);
        }
        log.Write("Closing Discord RPC");
        discordRPC.Dispose();
        discordCancellationTokenSource.TryReset();
        discordCancellationTokenSource.Dispose();
        isDiscordRPCRunning = false;
    }

    static void StartDiscordRPC()
    {
        if (isDiscordRPCRunning == true) { log.Write("DiscordRPC is already running, not creating another one"); return; }
        discordRPCThread = new Thread(() => UseDiscordRPC(discordCancellationToken));
        discordRPCThread.Start();
    }

    static void UseNativeMessaging()
    {
        while (true)
        {
            var receivedMessage = NativeMessaging.GetMessage();
            var messageType = receivedMessage.Substring(0, receivedMessage.IndexOf("\n")).ToUpper().Trim();
            var messageContent = receivedMessage.Substring(receivedMessage.IndexOf("\n") + 1);

            log.Write($"Received {receivedMessage}");

            if (messageType == "RPC:") { NativeMessaging.UpdateRPCFile(VRPCSettings.RPCInfoPath, messageContent); }
            if (messageType == "STATUS:") { StatusUpdate(messageContent); }
        }
    }
    static void Main(string[] args)
    {
        VRPCSettings.checkIfApplicationDataFolderExists();

        try { Log log = new Log(); }
        catch (Exception e) { Console.WriteLine($"Could not initialize logging. Exception {e.Data}"); }

        Thread nativeMessagingThread = new Thread(UseNativeMessaging);
        nativeMessagingThread.Start();
        log.Write("Starting Native Messaging.");
    }
}