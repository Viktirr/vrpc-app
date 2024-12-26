﻿using System;
using System.Linq;
using VRPC.DiscordRPCManager;
using VRPC.Logging;
using VRPC.NativeMessasing;
using VRPC.Configuration;
using DiscordRPC;
using VRPC.ListeningDataManager;

class Program
{
    private static Log log = new Log();
    public static bool isDiscordRPCRunning = false;
    public static bool isReceivingRPCData = false;
    private static string currentService = "";
    private static CancellationTokenSource discordCancellationTokenSource = new CancellationTokenSource();
    private static CancellationToken discordCancellationToken = discordCancellationTokenSource.Token;
    private static CancellationTokenSource listeningDataCancellationTokenSource = new CancellationTokenSource();
    private static CancellationToken listeningDataCancellationToken = listeningDataCancellationTokenSource.Token;

    static void StatusUpdate(string message)
    {
        int newLineCount = message.Count(c => c == '\n');
        int seekFrom = 0;
        string currentLine;

        Dictionary<int, string> messageDictionary = new Dictionary<int, string>();
        for (int i = 0; i < (newLineCount + 1); i++)
        {
            int nextNewLine = message.IndexOf("\n", seekFrom);

            if (nextNewLine < 0) { currentLine = message.Substring(seekFrom); }
            else { currentLine = message.Substring(seekFrom, nextNewLine); }

            messageDictionary.Add(i, currentLine);

            seekFrom = nextNewLine + 1;
        }

        static bool OpenDiscordRPC()
        {
            if (isDiscordRPCRunning != true)
            {
                discordCancellationTokenSource = new CancellationTokenSource();
                discordCancellationToken = discordCancellationTokenSource.Token;
                log.Write("Calling StartDiscordRPC");
                StartDiscordRPC();
                return true;
            }
            log.Info($"[Main] Got status opened for {currentService}, however Discord RPC is already running. Try again later.");
            return false;
        }

        if (messageDictionary[0].Contains("GET_RPC_INFO")) { NativeMessagingCommands.SendRichPresence(); return; }
        
        if (messageDictionary[0].Contains("Program")) { NativeMessaging.ConnectivityStatus(messageDictionary); return; }

        // We make 2 tries to start Discord RPC in case the user started a new tab/refreshed the page.
        currentService = messageDictionary[0];
        log.Write("[Main] Service selected: " + currentService);
        if (messageDictionary[1] == "Opened")
        {
            bool OpenDiscordRPCSuccess = OpenDiscordRPC();
            if (!OpenDiscordRPCSuccess) { Thread.Sleep(1000); OpenDiscordRPC(); return; }
        }

        if (messageDictionary[1] == "Closed")
        {
            try { discordCancellationTokenSource.Cancel(); return; } catch (Exception e) { log.Warn($"[Main] Couldn't cancel Cancellation Token for Discord RPC, probably already cancelled? Exception {e.Data}"); return; }
        }
    }

    static void UseDiscordRPC(CancellationToken token)
    {
        log.Info("[Main] Attempting to start Discord RPC");
        isDiscordRPCRunning = true;
        isReceivingRPCData = true;
        DiscordRPCManager discordRPC = new DiscordRPCManager();
        discordRPC.Init(currentService);
        discordRPC.Start(token);
        while (!token.IsCancellationRequested)
        {
            Thread.Sleep(500);
        }
        log.Info("[Main] Closing Discord RPC");
        discordRPC.Dispose();
        discordCancellationTokenSource.TryReset();
        discordCancellationTokenSource.Dispose();
        isDiscordRPCRunning = false;
        isReceivingRPCData = false;
    }

    static void StartDiscordRPC()
    {
        if (isDiscordRPCRunning == true) { log.Info("[Main] DiscordRPC is already running, not creating another one"); return; }
        Thread discordRPCThread;
        discordRPCThread = new Thread(() => UseDiscordRPC(discordCancellationToken));
        discordRPCThread.Start();
    }

    static void UseNativeMessaging()
    {
        while (true)
        {
            string receivedMessage = NativeMessaging.GetMessage();
            string messageType;
            string messageContent;
            if (receivedMessage.IndexOf("\n") == -1)
            {
                messageType = "NONE:";
                messageContent = receivedMessage;
            }
            else
            {
                messageType = receivedMessage.Substring(0, receivedMessage.IndexOf("\n")).ToUpper().Trim();
                messageContent = receivedMessage.Substring(receivedMessage.IndexOf("\n") + 1);
            }

            log.Write($"Received {receivedMessage}");

            if (messageType == "RPC:") { NativeMessaging.UpdateRPCFile(VRPCSettings.RPCInfoPath, messageContent); }
            if (messageType == "STATUS:") { StatusUpdate(messageContent); }
        }
    }

    static void UseListeningData(bool ShutdownRequested = false)
    {
        ListeningData.Heartbeat(listeningDataCancellationToken, ShutdownRequested);
    }

    static void Main(string[] args)
    {
        VRPCSettings.checkIfApplicationDataFolderExists();
        VRPCSettings.checkSettings();
        log.Write("[Main] Reading Settings from file.");

        log.Clear();

        try { Log log = new Log(); }
        catch (Exception e) { Console.WriteLine($"Could not initialize logging. Exception {e.Data}"); }

        Thread nativeMessagingThread = new Thread(UseNativeMessaging);
        nativeMessagingThread.Start();
        log.Write("[Main] Starting Native Messaging.");

        Thread listeningDataThread = new Thread(new ParameterizedThreadStart((obj) => UseListeningData((bool)(obj ?? false))));
        listeningDataThread.Start();
        log.Write("[Main] Starting Listening Data.");
    }
}