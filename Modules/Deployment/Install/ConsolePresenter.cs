namespace VRPC.Deployment.Install
{
    // Console implementation of the presenter seam. It stays the accessible path
    // and the fallback: a Vulkan window has no accessibility tree, and Linux has
    // no overlay at all.
    public sealed class ConsolePresenter : IPresenter
    {
        private readonly bool _quiet;
        private readonly bool _yes;
        private readonly bool _interactive;

        public ConsolePresenter(bool quiet, bool yes, bool? interactive = null)
        {
            _quiet = quiet;
            _yes = yes;
            _interactive = interactive ?? !Console.IsInputRedirected;
        }

        public Task<InstallDecision> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
        {
            if (_quiet || _yes) { return Task.FromResult(InstallDecision.Proceed); }
            RequireInteractive();

            Console.WriteLine();
            Console.WriteLine(prompt.Title);
            if (!string.IsNullOrEmpty(prompt.Subtitle)) { Console.WriteLine(prompt.Subtitle); }
            Console.WriteLine();
            Console.WriteLine(prompt.Text);
            Console.WriteLine();

            bool offersUninstall = string.Equals(prompt.SecondaryLabel, "Uninstall", StringComparison.OrdinalIgnoreCase);
            string choices = prompt.AffirmativeByDefault ? "[Y/n" : "[N/y";
            if (offersUninstall) { choices += "/u"; }
            choices += "]";
            Console.Write($"{prompt.PrimaryLabel}? {choices}: ");

            string? line = Console.ReadLine();
            if (line is null) { throw new PresenterUnavailableException(); }

            string value = line.Trim().ToLowerInvariant();
            if (offersUninstall && value is "u" or "uninstall" or "remove") { return Task.FromResult(InstallDecision.Uninstall); }
            if (value.Length == 0) { return Task.FromResult(prompt.AffirmativeByDefault ? InstallDecision.Proceed : InstallDecision.Cancel); }
            if (value is "y" or "yes" or "install" or "update" or "repair") { return Task.FromResult(InstallDecision.Proceed); }
            if (value is "n" or "no" or "cancel" or "close") { return Task.FromResult(InstallDecision.Cancel); }
            return Task.FromResult(prompt.AffirmativeByDefault ? InstallDecision.Proceed : InstallDecision.Cancel);
        }

        public Task<bool> ConfirmForceCloseAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
        {
            if (_quiet || _yes) { return Task.FromResult(true); }
            RequireInteractive();

            Console.WriteLine();
            Console.WriteLine(prompt.Title);
            Console.WriteLine(prompt.Text);
            Console.WriteLine();
            Console.Write($"{prompt.PrimaryLabel}? [N/y]: ");
            string? line = Console.ReadLine();
            if (line is null) { throw new PresenterUnavailableException(); }

            string value = line.Trim().ToLowerInvariant();
            return Task.FromResult(value is "y" or "yes" or "close" or "kill" or "force");
        }

        private void RequireInteractive()
        {
            if (!_interactive) { throw new PresenterUnavailableException(); }
        }

        public void Report(string step, string? title = null)
        {
            if (!_quiet) { Console.WriteLine(step); }
        }

        public Task CompleteAsync(InstallResult result)
        {
            Print(result);
            ReadKey();
            return Task.CompletedTask;
        }

        public Task FailAsync(InstallResult result)
        {
            Print(result);
            ReadKey();
            return Task.CompletedTask;
        }

        public Task CanceledAsync(InstallResult result)
        {
            Console.WriteLine();
            Console.WriteLine(result.Message);
            ReadKey();
            return Task.CompletedTask;
        }

        private void Print(InstallResult result)
        {
            Console.WriteLine();
            if (!result.Success) { Console.WriteLine("ERROR: " + result.Message); }
            else { Console.WriteLine(result.Message); }

            if (!string.IsNullOrEmpty(result.Version)) { Console.WriteLine($"Version: {result.Version}"); }
            if (!string.IsNullOrEmpty(result.LogPath)) { Console.WriteLine($"Log: {result.LogPath}"); }
        }

        private void ReadKey()
        {
            if (_quiet) { return; }
            Console.WriteLine("Press any key to exit.");
            try
            {
                if (!Console.IsInputRedirected) { Console.ReadKey(true); }
            }
            catch { }
        }
    }

    // Thrown when a prompt cannot be shown or answered. Consent must fail loudly
    // instead of treating EOF as yes; the engine reports it before any host stop
    // or file mutation can rely on the answer.
    public sealed class PresenterUnavailableException : Exception
    {
        public const string DefaultMessage =
            "VRPC setup needs a prompt that a person can see and answer, but no setup panel is available and this process has no interactive console. " +
            "Run VRPC.exe from a normal console, or pass --yes for an unattended installation.";

        public PresenterUnavailableException() : base(DefaultMessage)
        {
        }
    }
}
