using System.Globalization;

namespace VRPC.Install
{
    // Console implementation of the presenter seam.
    public sealed class ConsolePresenter : IPresenter
    {
        private readonly bool _quiet;
        private readonly bool _yes;

        public ConsolePresenter(bool quiet, bool yes)
        {
            _quiet = quiet;
            _yes = yes;
        }

        public bool Confirm(InstallPlan plan)
        {
            if (_quiet || _yes) { return true; }

            Console.WriteLine();
            switch (plan.Action)
            {
                case InstallAction.Install:
                    Console.WriteLine(ConsentText(plan));
                    Console.Write("Proceed with installation? [N/y]: ");
                    return IsYes(Console.ReadLine());
                case InstallAction.Update:
                    Console.WriteLine($"VRPC {plan.CurrentVersion} is installed at {plan.Root}.");
                    Console.WriteLine($"Update to {plan.TargetVersion}? Your settings and listening history will be kept. [Y/n]: ");
                    return IsNoOrEmptyAsYes(Console.ReadLine());
                case InstallAction.Repair:
                    Console.WriteLine($"VRPC {plan.CurrentVersion} is installed at {plan.Root}.");
                    Console.WriteLine($"Reinstall/repair version {plan.TargetVersion}? [Y/n]: ");
                    return IsNoOrEmptyAsYes(Console.ReadLine());
                case InstallAction.Uninstall:
                    Console.WriteLine($"This will uninstall VRPC from {plan.Root}.");
                    Console.WriteLine("Your settings and listening history will be kept unless --purge is used.");
                    Console.Write("Proceed with uninstallation? [N/y]: ");
                    return IsYes(Console.ReadLine());
                default:
                    return false;
            }
        }

        public bool ConfirmForceClose(string message)
        {
            if (_quiet || _yes) { return true; }
            Console.WriteLine();
            Console.WriteLine(message);
            Console.Write("Force-close it and continue? [N/y]: ");
            return IsYes(Console.ReadLine());
        }

        public void Report(string step)
        {
            if (!_quiet) { Console.WriteLine(step); }
        }

        public void Complete(InstallResult result)
        {
            Console.WriteLine();
            Console.WriteLine(result.Message);
            if (!string.IsNullOrEmpty(result.Version)) { Console.WriteLine($"Version: {result.Version}"); }
            if (!string.IsNullOrEmpty(result.LogPath)) { Console.WriteLine($"Log: {result.LogPath}"); }
            if (!_quiet) { Console.WriteLine("Press any key to exit."); ReadKey(); }
        }

        public void Fail(InstallResult result)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR: " + result.Message);
            if (!string.IsNullOrEmpty(result.LogPath)) { Console.WriteLine($"Log: {result.LogPath}"); }
            if (!_quiet) { Console.WriteLine("Press any key to exit."); ReadKey(); }
        }

        private static string ConsentText(InstallPlan plan)
        {
            return "VRPC is an application that allows communication between the extension and client to provide Rich Presence data to Discord alongside other features.\n\n" +
                   "By clicking Install below, you agree to the License, Terms of Use and Privacy Policy of this application listed over at https://viktir.com/vrpc\n\n" +
                   "If you do not agree with any of the previous notices, refrain from installing this software by clicking the Cancel button or exiting the program.\n\n" +
                   "By selecting install the following will occur:\n" +
                   $"The files for this application will extract at {plan.Root}\n" +
                   "A few registry keys will be created to allow for Native Messaging permissions to work with the extension and to create an entry for uninstalling the application in the Settings app.";
        }

        private static bool IsYes(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) { return false; }
            string value = input.Trim().ToLowerInvariant();
            return value is "y" or "yes" or "install" or "uninstall";
        }

        private static bool IsNoOrEmptyAsYes(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) { return true; }
            string value = input.Trim().ToLowerInvariant();
            return value is not ("n" or "no" or "cancel");
        }

        private static void ReadKey()
        {
            try
            {
                if (!Console.IsInputRedirected) { Console.ReadKey(true); }
            }
            catch { }
        }
    }
}
