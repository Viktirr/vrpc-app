namespace VRPC.Install
{
    // Console implementation of the presenter seam. It stays the accessible path
    // and the fallback: a Vulkan window has no accessibility tree, and Linux has
    // no overlay at all.
    public sealed class ConsolePresenter : IPresenter
    {
        private readonly bool _quiet;
        private readonly bool _yes;

        public ConsolePresenter(bool quiet, bool yes)
        {
            _quiet = quiet;
            _yes = yes;
        }

        public Task<InstallDecision> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
        {
            if (_quiet || _yes) { return Task.FromResult(InstallDecision.Proceed); }

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

            string value = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (offersUninstall && value is "u" or "uninstall" or "remove") { return Task.FromResult(InstallDecision.Uninstall); }
            if (value.Length == 0) { return Task.FromResult(prompt.AffirmativeByDefault ? InstallDecision.Proceed : InstallDecision.Cancel); }
            if (value is "y" or "yes" or "install" or "update" or "repair") { return Task.FromResult(InstallDecision.Proceed); }
            if (value is "n" or "no" or "cancel" or "close") { return Task.FromResult(InstallDecision.Cancel); }
            return Task.FromResult(prompt.AffirmativeByDefault ? InstallDecision.Proceed : InstallDecision.Cancel);
        }

        public Task<bool> ConfirmForceCloseAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
        {
            if (_quiet || _yes) { return Task.FromResult(true); }

            Console.WriteLine();
            Console.WriteLine(prompt.Title);
            Console.WriteLine(prompt.Text);
            Console.WriteLine();
            Console.Write($"{prompt.PrimaryLabel}? [N/y]: ");
            string value = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            return Task.FromResult(value is "y" or "yes" or "close" or "kill" or "force");
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
}
