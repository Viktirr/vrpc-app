using VRPC.DiscordRPCManager;
using VRPC.Logging;
using VRPC.NativeMessasing;
using VRPC.Configuration;
using VRPC.ListeningDataManager;
using VRPC.Globals;
using VRPC.Packaging;

class Program
{
    private static Log log = new Log();

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

        if (messageDictionary[0].Contains("Program")) { NativeMessaging.ConnectivityStatus(messageDictionary); return; }

        currentService = messageDictionary[0];
        log.Write("[Main] Service selected: " + currentService);

        // Fix The given key '1' was not present in the dictionary.
        if (messageDictionary.Count < 2) { return; }

        // We make 2 tries to start Discord RPC in case the user started a new tab/refreshed the page.
        if (messageDictionary[1].Contains("Opened"))
        {
            bool OpenDiscordRPCSuccess = OpenDiscordRPC();
            if (!OpenDiscordRPCSuccess) { Thread.Sleep(2000); OpenDiscordRPC(); return; }
        }

        if (messageDictionary[1].Contains("Closed"))
        {
            if (currentService != DiscordRPCData.currentService) { return; }
            try { discordCancellationTokenSource.Cancel(); } catch (Exception e) { log.Warn($"[Main] Couldn't cancel Cancellation Token for Discord RPC, probably already cancelled? Exception {e.Data}"); return; }

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

            // log.Write($"[Main] Received {receivedMessage}");

            if (messageType == "RPC:") { NativeMessaging.UpdateRPCDataLegacy(messageContent); }
            if (messageType == "STATUS:") { StatusUpdate(messageContent); }
        }
    }

    static void UseListeningData(bool ShutdownRequested = false)
    {
        ListeningData.Heartbeat(listeningDataCancellationToken, ShutdownRequested);
    }

    static void UseGUI(string[] args)
    {
        bool isUninstall = false;
        bool isUninstallTemp = false;

        string roamingAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appNamePath = Path.Combine(roamingAppDataPath, VRPCGlobalData.appName);

        string sourceFilePath = AppContext.BaseDirectory;

        if (Directory.Exists(appNamePath) && !appNamePath.Contains(sourceFilePath))
        {
            if (File.Exists(Path.Combine(appNamePath, "VRPC.exe")))
            {
                isUninstallTemp = true;
                PackagingGlobals.uninstallString = $"\n\nPLEASE READ\nYou were launched to the uninstaller because the application is already installed. If this is a mistake, delete the folder {appNamePath} and relaunch the installer.\n\nUPDATES:\nIf you're updating to a new version, first select Uninstall below then relaunch the installer.";
            }
        }

        foreach (string arg in args)
        {
            if (arg == "--uninstall")
            {
                isUninstall = true;
            }
            if (arg == "--uninstall-temp")
            {
                isUninstallTemp = true;
            }
        }

        if (isUninstall)
        {
            CreateUninstaller.CreateUninstall();
        }
        else if (isUninstallTemp)
        {
            UninstallWindow uw = new UninstallWindow();
            uw.Uninstall();
        }
        else
        {
            InstallWindow iw = new InstallWindow();
            iw.Install();
        }
    }

    static void Main(string[] args)
    {
        foreach (string arg in args)
        {
            log.Info(arg);
        }
        if (args.Count() == 0) { log.Info("Assuming running natively."); }
        else
        {
            if (args[0].Contains("chrome-extension")) { log.Info("Assuming running from browser."); runningOnBrowser = true; }
            else if (args[0].Contains(".json")) { log.Info("Assuming running from browser."); runningOnBrowser = true; }
            else { log.Info("Assuming running natively."); }
        }

        if (!runningOnBrowser)
        {
            Console.Clear();
            Console.WriteLine($"Running {VRPCGlobalData.appName} version {VRPCGlobalData.appVersion}");
            Console.WriteLine($"Please don't close the console (this black box) while the UI is present or while the application is installing/uninstalling.\n");
            UseGUI(args);
            return;
        }

        VRPCSettings.CheckIfApplicationDataFolderExists();
        VRPCSettings.CheckSettings();
        log.Write("[Main] Reading Settings from file.");

        if (VRPCSettings.settingsData.DisableClearingLogs == false) { log.Clear(); }

        Thread nativeMessagingThread = new Thread(UseNativeMessaging);
        nativeMessagingThread.Start();
        log.Write("[Main] Starting Native Messaging.");

        Thread listeningDataThread = new Thread(new ParameterizedThreadStart((obj) => UseListeningData((bool)(obj ?? false))));
        listeningDataThread.Start();
        log.Write("[Main] Starting Listening Data.");
    }
}