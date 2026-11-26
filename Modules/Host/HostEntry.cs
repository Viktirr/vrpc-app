using VRPC.Application;
using VRPC.Configuration;
using VRPC.Logging;
using VRPC.NativeMessasing;

namespace VRPC.Host
{
    // Host startup: settings, lock, control pipe, native messaging and listening data.
    public static class HostEntry
    {
        // Settings, lock, control pipe, then the long-running threads.
        public static int Run(string[] args)
        {
            Log log = new();
            try
            {
                Program.runningOnBrowser = true;
                VRPCSettings.CheckIfApplicationDataFolderExists();
                VRPCSettings.CheckSettings();
                if (!VRPCSettings.settingsData.DisableClearingLogs) { log.Clear(); }
                log.Info($"[Host] Starting VRPC {ProductInfo.Version} root={AppPaths.Root} args={string.Join(" ", args)}");

                if (VRPC.Deployment.Install.SetupLifecycle.BlocksHostStartup(out string setupReason))
                {
                    log.Warn("[Host] Startup suppressed: " + setupReason);
                    NativeMessagingCommands.SendLockExit();
                    return 0;
                }

                Program.Instance = SingleInstance.Acquire();
                if (!Program.Instance.IsOwner)
                {
                    log.Info("[Host] Another VRPC instance owns the lock.");
                    NativeMessagingCommands.SendLockExit();
                    return 0;
                }

                try
                {
                    Program.ControlServer = ControlServer.Start(() => Program.Shutdown(exit: true));
                }
                catch (Exception e)
                {
                    log.Warn("[Host] The control pipe is unavailable; setup will have to force-close this instance. " + e.Message);
                }

                Thread nativeMessagingThread = new(Program.UseNativeMessaging);
                nativeMessagingThread.Start();
                log.Write("[Host] Starting Native Messaging.");

                Thread listeningDataThread = new(() => Program.UseListeningData(false));
                listeningDataThread.Start();
                log.Write("[Host] Starting Listening Data.");

                // Explains itself through the log in every case; an updater
                // failure must never keep Rich Presence or history from working.
                VRPC.Deployment.Updates.UpdateRuntime.TryStart(log);

                return 0;
            }
            catch (Exception e)
            {
                log.Error($"[Host] Failed to start: {e}");
                return 1;
            }
        }
    }
}
