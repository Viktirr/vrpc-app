namespace VRPC.Install
{
    // Request/plan/result/prompt/presenter seam shared by the engine and its UI.
    public enum InstallAction
    {
        Install,
        Update,
        Repair,
        Uninstall
    }

    // The three ways a prompt can be answered. Proceed is the plan's own action:
    // install, update or repair, whichever the engine planned.
    public enum InstallDecision
    {
        Proceed,
        Uninstall,
        Cancel
    }

    public static class PromptStyle
    {
        public const string Primary = "primary";
        public const string Danger = "danger";
        public const string Ghost = "ghost";
    }

    // One prompt, whether it is the license or a mid-flow question. Both the
    // console and the panel render the same shape, and every word comes from the
    // engine, so a new action never needs a new UI.
    public sealed class ConfirmPrompt
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Text { get; init; } = "";
        public string PrimaryLabel { get; init; } = "Continue";
        public string PrimaryStyle { get; init; } = PromptStyle.Primary;
        public string? SecondaryLabel { get; init; }
        public string SecondaryStyle { get; init; } = PromptStyle.Ghost;
        public string CloseLabel { get; init; } = "Close";

        // Console default for an empty answer. Prompts that change something are
        // opt-in; a question about a running copy is not.
        public bool AffirmativeByDefault { get; init; }

        // Progress copy and scale. The panel needs both to draw a bar; the
        // console ignores them.
        public string ProgressTitle { get; init; } = "Working";
        public int TotalSteps { get; init; }
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
        public bool PurgeData { get; init; }
        public List<string> Warnings { get; init; } = new();
    }

    public sealed class InstallResult
    {
        public bool Success { get; init; }
        public int ExitCode { get; init; }
        public string Title { get; init; } = "";
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

    // Async on purpose: the panel is an interactive child process, and the
    // engine has nothing to do while a human reads the license.
    public interface IPresenter
    {
        Task<InstallDecision> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default);
        Task<bool> ConfirmForceCloseAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default);

        // `title` renames the progress screen: the same steps are an update or an
        // uninstall, depending on what the user chose a moment earlier.
        void Report(string step, string? title = null);
        Task CompleteAsync(InstallResult result);
        Task FailAsync(InstallResult result);

        // The user said no. Separate from FailAsync because a panel that was
        // canceled has already closed itself: there is nowhere left to write.
        Task CanceledAsync(InstallResult result);
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
