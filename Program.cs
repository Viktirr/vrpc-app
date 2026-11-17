using System.Text.Json;
using VRPC;
using VRPC.Application;
using VRPC.DiscordRPCManager;
using VRPC.Host;
using VRPC.Logging;
using VRPC.NativeMessasing;
using VRPC.Configuration;
using VRPC.ListeningDataManager;
using VRPC.Globals;
using VRPC.Setup;

class Program
{
    private static Log log = new Log();
    private static int _shutdownStarted;

    public static SingleInstance? Instance { get; set; }
    public static ControlServer? ControlServer { get; set; }
    public static VRPC.Updates.UpdateCoordinator? Updater { get; set; }

    public static bool runningOnBrowser = false;
    public static bool isDiscordRPCRunning = false;
    public static bool isReceivingRPCData = false;
    private static string currentService = "";
    private static CancellationTokenSource discordCancellationTokenSource = new CancellationTokenSource();
    private static CancellationToken discordCancellationToken = discordCancellationTokenSource.Token;
    private static CancellationTokenSource listeningDataCancellationTokenSource = new CancellationTokenSource();
    private static CancellationToken listeningDataCancellationToken = listeningDataCancellationTokenSource.Token;

    static void StatusUpdate(string message)
    {
        if (message == null || message == "") { return; }

        Dictionary<int, string> messageDictionary = VRPCGlobalFunctions.LinesIntoDictionary(message);

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
        if (messageDictionary[0].Contains("GET_CONFIG_FULL")) { NativeMessagingCommands.SendConfigFull(); return; }
        if (messageDictionary[0].Contains("GET_CONFIG_INFO")) { if (messageDictionary.Count > 1) { NativeMessagingCommands.SendConfigDetailed(messageDictionary[1]); return; } }
        if (messageDictionary[0].Contains("GET_APP_VERSION")) { NativeMessagingCommands.SendAppVersion(); return; }
        if (messageDictionary[0].Contains("GET_LISTENINGDATA")) { NativeMessagingCommands.SendListeningDataStats(); return; }
        if (messageDictionary[0].Contains("SET_CONFIG")) { NativeMessagingCommands.SetConfig(messageDictionary); return; }
        if (messageDictionary[0].Contains("GET_UPDATE_STATUS")) { NativeMessagingCommands.SendUpdateStatus(Updater?.GetStatus()); return; }
        if (messageDictionary[0].Contains("UPDATE_CHECK"))
        {
            if (Updater is null) { NativeMessagingCommands.SendUpdateReply("unsupported"); return; }
            Updater.TryRequestManualCheck(out string updateCheckReply);
            NativeMessagingCommands.SendUpdateReply(updateCheckReply);
            return;
        }
        if (messageDictionary[0].Contains("UPDATE_INSTALL"))
        {
            if (Updater is null) { NativeMessagingCommands.SendUpdateReply("unsupported"); return; }
            Updater.TryRequestInstall(out string updateInstallReply);
            NativeMessagingCommands.SendUpdateReply(updateInstallReply);
            return;
        }

        if (messageDictionary[0].Contains("Program")) { NativeMessaging.ConnectivityStatus(messageDictionary); return; }

        string service = messageDictionary[0];
        if (service is not ("YouTube Music" or "Soundcloud"))
        {
            log.Warn($"[Main] Ignoring an unknown status command: {service}");
            return;
        }

        currentService = service;
        log.Write("[Main] Service selected: " + currentService);

        // Fix The given key '1' was not present in the dictionary.
        if (messageDictionary.Count < 2) { return; }

        // We make 2 tries to start Discord RPC in case the user started a new tab/refreshed the page.
        if (messageDictionary[1].Contains("Opened"))
        {
            if (currentService == "YouTube Music" && VRPCSettings.settingsData.EnableYouTubeMusic == false) { return; }
            if (currentService == "Soundcloud" && VRPCSettings.settingsData.EnableSoundcloud == false) { return; }
            bool OpenDiscordRPCSuccess = OpenDiscordRPC();
            if (!OpenDiscordRPCSuccess) { Thread.Sleep(2000); OpenDiscordRPC(); return; }
        }

        if (messageDictionary[1].Contains("Closed"))
        {
            if (currentService != DiscordRPCData.currentService) { return; }
            try { discordCancellationTokenSource.Cancel(); } catch (Exception e) { log.Warn($"[Main] Couldn't cancel Cancellation Token for Discord RPC, probably already cancelled? Exception {e.Message}"); return; }

            if (VRPCGlobalData.MiscellaneousSongData.Count != 0) { VRPCGlobalData.MiscellaneousSongData.Clear(); }
        }
    }

    static void UseDiscordRPC(CancellationToken token)
    {
        log.Info("[Main] Enabling Rich Presence");
        isReceivingRPCData = true;
        DiscordRPCManager discordRPC = new DiscordRPCManager();
        discordRPC.Init(currentService);
        if (VRPCSettings.settingsData.EnableDiscordRPC == true)
        {
            log.Info("[Main] Attempting to start Discord RPC");
            isDiscordRPCRunning = true;
            discordRPC.Start();
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
            return;
        }
        else
        {
            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(500);
            }
            discordRPC.Dispose();
            discordCancellationTokenSource.TryReset();
            discordCancellationTokenSource.Dispose();
            isDiscordRPCRunning = false;
            isReceivingRPCData = false;
        }
    }

    static void StartDiscordRPC()
    {
        if (isDiscordRPCRunning == true) { log.Info("[Main] DiscordRPC is already running, not creating another one"); return; }
        Thread discordRPCThread;
        discordRPCThread = new Thread(() => UseDiscordRPC(discordCancellationToken));
        discordRPCThread.Start();
    }

    public static void UseNativeMessaging()
    {
        while (true)
        {
            NativeMessaging.ReadResult result = NativeMessaging.ReadMessage(out string receivedMessage);

            if (result == NativeMessaging.ReadResult.Closed) { Shutdown(exit: true); return; }
            if (result == NativeMessaging.ReadResult.Skipped) { continue; }

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

            // log.Write($"[Main] Received {receivedMessage}");

            // A single bad frame must never take the app down: page controlled data (song names,
            // durations, ...) ends up in here, so log it and keep serving the connection.
            try
            {
                if (messageType == "PROTO:") { ProtocolState.SetClientProtocol(messageContent); }
                if (messageType == "RPC:") { NativeMessaging.UpdateRPCDataLegacy(messageContent); }
                if (messageType == "STATUS:") { StatusUpdate(messageContent); }
            }
            catch (Exception e)
            {
                log.Error($"[Main] Couldn't handle a {messageType} message, ignoring it. Exception: {e}");
            }
        }
    }

    public static void UseListeningData(bool ShutdownRequested = false)
    {
        ListeningData.Heartbeat(listeningDataCancellationToken, ShutdownRequested);
    }

    public static void RemoveLock()
    {
        Instance?.Dispose();
        Instance = null;
    }

    // Shared shutdown path: release the lock, flush the current song into the listening data file
    // and refresh the backup. Used when the browser closes the connection and when the extension
    // asks the app to shut down.
    public static void Shutdown(bool exit)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) { return; }

        log.Write("[Main] Shutting down.");
        try { Updater?.Dispose(); } catch { }
        Updater = null;
        ControlServer?.Dispose();
        ControlServer = null;
        RemoveLock();

        ListeningData.UpdateListeningDataFile();

        try
        {
            string data = File.ReadAllText(VRPCSettings.ListeningDataPath);
            JsonSerializer.Deserialize<ListeningData.SongData>(data);
            ListeningDataBackup.CreateBackup();
        }
        catch
        {
            log.Write("[Main] Skipping backup due to corrupted file.");
        }

        if (exit) { Environment.Exit(0); }
    }

    // Browser launches go to the host; everything else goes to setup.
    static int Main(string[] args)
    {
        if (BrowserInvocation.IsHost(args)) { return HostEntry.Run(args); }
        return SetupEntry.Run(args);
    }
}