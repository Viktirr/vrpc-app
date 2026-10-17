namespace VRPC.Install
{
    // Request/plan/result/presenter seam shared by the engine and its UI.
    public enum InstallAction
    {
        Install,
        Update,
        Repair,
        Uninstall
    }

    public sealed class InstallPlan
    {
        public InstallAction Action { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string TargetVersion { get; init; } = "";
        public string Root { get; init; } = "";
        public string AppDir { get; init; } = "";
        public string DataDir { get; init; } = "";
        public bool LegacyInstall { get; init; }
        public int FileCount { get; init; }
        public bool ForceCloseRequired { get; init; }
        public List<string> Warnings { get; init; } = new();
    }

    public sealed class InstallResult
    {
        public bool Success { get; init; }
        public int ExitCode { get; init; }
        public string Message { get; init; } = "";
        public string Version { get; init; } = "";
        public string LogPath { get; init; } = "";
        public List<string> Warnings { get; init; } = new();
    }

    public sealed class InstallRequest
    {
        public string SourceDirectory { get; init; } = "";
        public InstallAction? RequestedAction { get; init; }
        public bool Force { get; init; }
        public bool Quiet { get; init; }
        public bool Yes { get; init; }
        public bool AllowDowngrade { get; init; }
        public bool PurgeData { get; init; }
        public bool FromTempInstaller { get; init; }
        public RegistrationOptions Registration { get; init; } = RegistrationOptions.Default;
    }

    public interface IPresenter
    {
        bool Confirm(InstallPlan plan);
        bool ConfirmForceClose(string message);
        void Report(string step);
        void Complete(InstallResult result);
        void Fail(InstallResult result);
    }

    public static class SetupExitCodes
    {
        public const int Success = 0;
        public const int Canceled = 2;
        public const int Running = 3;
        public const int InvalidPayload = 4;
        public const int Failed = 5;
        public const int Usage = 6;
    }
}
