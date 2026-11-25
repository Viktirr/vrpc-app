using System.Diagnostics;

namespace VRPC.Install
{
    // Test and verification switches must never ride into a real setup or panel
    // process through the inherited environment. The updater-created setup and
    // the setup-created panel both call Scrub; the release panel also compiles
    // its environment hooks out (see installer-ui Cargo features).
    public static class AutomationEnvironment
    {
        public static readonly string[] Variables =
        {
            "VRPC_UI_AUTO",
            "VRPC_UI_AUTO_ANSWER",
            "VRPC_UI_AUTO_DELAY_MS",
            "VRPC_UI_AUTO_DISMISS_MS",
            "VRPC_UI_DUMP",
            "VRPC_UI_SIMULATE",
            "OVERLAY_VALIDATION",
            "VRPC_UPDATE_LOCAL_FEED",
            "VRPC_UPDATER_INITIAL_DELAY_SECONDS",
            "VRPC_DISABLE_UPDATER",
            "VRPC_NO_REGISTRY",
            "VRPC_SETUP_LOCK_TIMEOUT_MS"
        };

        public static void Scrub(ProcessStartInfo startInfo)
        {
            foreach (string name in Variables)
            {
                try { startInfo.Environment.Remove(name); } catch { }
                try { startInfo.EnvironmentVariables.Remove(name); } catch { }
            }
        }
    }
}
