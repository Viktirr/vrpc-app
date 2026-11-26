using VRPC.Application;

namespace VRPC.Deployment.Install
{
    // app.old is only ever deleted when this transaction created it. An
    // unexpected backup is preserved instead: renamed aside when the current
    // application is still present, or treated as a manual-recovery stop when
    // nothing is installed, because the backup may be the only usable copy of
    // the previous installation.
    internal static class InstallBackupGuard
    {
        // Returns the path an unexpected backup was preserved at, or null when
        // there was nothing to reconcile.
        internal static string? Reconcile(string backupPath, bool hadApp, SetupLog log)
        {
            if (string.IsNullOrWhiteSpace(backupPath) || !Directory.Exists(backupPath)) { return null; }

            if (!hadApp)
            {
                throw new InstallException(
                    $"A previous-version backup exists at {backupPath}, but no application is installed. " +
                    "VRPC kept it instead of deleting it; move it out of the installation folder and run setup again. " +
                    "The backup was kept for manual recovery.",
                    SetupExitCodes.Failed);
            }

            string preserved = backupPath + ".unexpected-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try
            {
                Directory.Move(backupPath, preserved);
            }
            catch (Exception e)
            {
                throw new InstallException(
                    $"An unexpected previous-version backup at {backupPath} could not be moved aside: {e.Message}. " +
                    "It was kept for manual recovery; move it out of the installation folder and run setup again.",
                    SetupExitCodes.Failed);
            }

            log.Write($"Preserved an unexpected backup at {preserved} instead of deleting it.");
            return preserved;
        }
    }
}
