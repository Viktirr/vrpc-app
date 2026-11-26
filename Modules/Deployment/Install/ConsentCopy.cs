namespace VRPC.Deployment.Install
{
    // Every word the user reads. The panel carries no product copy at all: it
    // draws the prompt the engine sends, which is also what keeps the license
    // text and the thing the user is agreeing to in one file.
    public static class ConsentCopy
    {
        public static ConfirmPrompt ForPlan(InstallPlan plan)
        {
            return plan.Action switch
            {
                InstallAction.Install => Install(plan),
                InstallAction.Update => Update(plan),
                InstallAction.Repair => Repair(plan),
                _ => Uninstall(plan)
            };
        }

        public static ConfirmPrompt ForceClose(InstallAction action, string version, string exePath)
        {
            string what = ActionWord(action);
            return new ConfirmPrompt
            {
                Title = "VRPC is still running",
                Subtitle = string.IsNullOrEmpty(version) ? "" : $"Version {version}",
                Text = $"VRPC is running from {exePath}.\n\n" +
                       $"Its files cannot be replaced while it is open. Continuing closes it now and then continues the {what}. " +
                       "Anything the app has already written to your settings and listening history is kept.",
                PrimaryLabel = "Close VRPC",
                PrimaryStyle = PromptStyle.Danger,
                CloseLabel = "Not now",
                AffirmativeByDefault = false
            };
        }

        private static ConfirmPrompt Install(InstallPlan plan)
        {
            return new ConfirmPrompt
            {
                Title = "Install VRPC",
                Subtitle = $"Version {plan.TargetVersion}",
                Text = License("Install") + "\n\n" +
                       "By selecting Install the following will occur:\n" +
                       $"The files for this application will extract to {plan.Root}\n" +
                       $"{plan.FileCount} files will be verified and copied\n" +
                       "A few registry keys will be created to allow Native Messaging permissions to work with the extension " +
                       "and to create an entry for uninstalling the application in the Settings app.",
                PrimaryLabel = "Install",
                PrimaryStyle = PromptStyle.Primary,
                CloseLabel = "Close",
                AffirmativeByDefault = false,
                ProgressTitle = "Installing VRPC",
                TotalSteps = 7
            };
        }

        private static ConfirmPrompt Update(InstallPlan plan)
        {
            return new ConfirmPrompt
            {
                Title = "Update VRPC",
                Subtitle = $"{plan.CurrentVersion} to {plan.TargetVersion}",
                Text = License("Update") + "\n\n" +
                       $"VRPC {plan.CurrentVersion} is installed at {plan.Root}. Your settings and listening history are kept.\n\n" +
                       "By selecting Update the following will occur:\n" +
                       "A running copy of VRPC is closed first\n" +
                       "The application files are replaced with the ones in this package\n" +
                       "The new version runs a self-test, and the previous files are restored if it fails",
                PrimaryLabel = "Update",
                PrimaryStyle = PromptStyle.Primary,
                SecondaryLabel = "Uninstall",
                SecondaryStyle = PromptStyle.Ghost,
                CloseLabel = "Close",
                AffirmativeByDefault = true,
                ProgressTitle = "Updating VRPC",
                TotalSteps = 7
            };
        }

        private static ConfirmPrompt Repair(InstallPlan plan)
        {
            return new ConfirmPrompt
            {
                Title = "VRPC is up to date",
                Subtitle = plan.TargetVersion,
                Text = License("Repair") + "\n\n" +
                       $"VRPC {plan.TargetVersion} is already installed at {plan.Root}. Your settings and listening history are kept.\n\n" +
                       "By selecting Repair the following will occur:\n" +
                       "The application files are replaced with the ones in this package, repairing a damaged installation\n" +
                       "The registry keys that let the browser extension reach the app are rewritten",
                PrimaryLabel = "Repair",
                PrimaryStyle = PromptStyle.Primary,
                SecondaryLabel = "Uninstall",
                SecondaryStyle = PromptStyle.Ghost,
                CloseLabel = "Close",
                AffirmativeByDefault = true,
                ProgressTitle = "Repairing VRPC",
                TotalSteps = 7
            };
        }

        private static ConfirmPrompt Uninstall(InstallPlan plan)
        {
            string data = plan.PurgeData
                ? "Your settings and listening history will be deleted."
                : "Your settings and listening history will be kept.";

            return new ConfirmPrompt
            {
                Title = "Uninstall VRPC",
                Subtitle = string.IsNullOrEmpty(plan.CurrentVersion) ? "" : $"Version {plan.CurrentVersion}",
                Text = License("Uninstall") + "\n\n" +
                       $"By selecting Uninstall the following will occur:\n" +
                       $"The application files in {plan.Root} will be removed\n" +
                       "The registry keys and the Settings entry for the application are removed\n" +
                       data,
                PrimaryLabel = "Uninstall",
                PrimaryStyle = PromptStyle.Danger,
                CloseLabel = "Close",
                AffirmativeByDefault = false,
                ProgressTitle = "Uninstalling VRPC",
                TotalSteps = 4
            };
        }

        private static string License(string agreement)
        {
            return "VRPC is an application that allows communication between the extension and client to provide " +
                   "Rich Presence data to Discord alongside other features.\n\n" +
                   $"By clicking {agreement} below, you agree to the License, Terms of Use and Privacy Policy of this " +
                   "application listed over at https://viktir.com/vrpc\n\n" +
                   "If you do not agree with any of the previous notices, refrain from installing this software by " +
                   "clicking the Close button or exiting the program.";
        }

        public static string ActionWord(InstallAction action)
        {
            return action switch
            {
                InstallAction.Install => "installation",
                InstallAction.Update => "update",
                InstallAction.Repair => "repair",
                _ => "uninstallation"
            };
        }

        public static string ResultTitle(InstallAction action)
        {
            return action switch
            {
                InstallAction.Install => "VRPC is installed",
                InstallAction.Update => "VRPC is updated",
                InstallAction.Repair => "VRPC was repaired",
                _ => "VRPC was removed"
            };
        }
    }
}
