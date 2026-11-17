using VRPC.Install;

namespace VRPC.Tests.Installer;

// M5: an app.old the current transaction did not create is never deleted.
// With an installed app it is renamed aside; with no app it stops the setup.
public class InstallBackupGuardTests
{
    [Fact]
    public void UnaccountedBackupWithoutAnApplication_StopsWithAManualRecoveryError()
    {
        using InstallerTestEnvironment environment = new();
        Directory.CreateDirectory(environment.BackupDir);
        environment.WriteJson(Path.Combine(environment.BackupDir, "keep.txt"), "keep me");

        InstallException exception = Assert.Throws<InstallException>(
            () => InstallBackupGuard.Reconcile(environment.BackupDir, hadApp: false, new SetupLog()));

        Assert.Equal(SetupExitCodes.Failed, exception.ExitCode);
        Assert.Contains("manual recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(environment.BackupDir));
        Assert.True(File.Exists(Path.Combine(environment.BackupDir, "keep.txt")));
    }

    [Fact]
    public void UnexpectedBackupWithAnInstalledApplication_IsPreservedInsteadOfDeleted()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.1.0");
        Directory.CreateDirectory(environment.BackupDir);
        environment.WriteJson(Path.Combine(environment.BackupDir, "keep.txt"), "keep me");
        SetupLog log = new();

        string? preserved = InstallBackupGuard.Reconcile(environment.BackupDir, hadApp: true, log);

        Assert.NotNull(preserved);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.True(Directory.Exists(preserved));
        Assert.True(File.Exists(Path.Combine(preserved!, "keep.txt")));
        Assert.Contains("Preserved an unexpected backup", File.ReadAllText(log.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBackup_IsANoOp()
    {
        using InstallerTestEnvironment environment = new();

        Assert.Null(InstallBackupGuard.Reconcile(environment.BackupDir, hadApp: false, new SetupLog()));
        Assert.Null(InstallBackupGuard.Reconcile(environment.BackupDir, hadApp: true, new SetupLog()));
    }
}
