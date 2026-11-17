using VRPC.Application;
using VRPC.Install;

namespace VRPC.Tests.Installer;

// End-to-end engine tests on a temp root with a fake registration seam: update +
// rollback must preserve custom extension IDs and never leave a marker behind.
public class InstallEngineTests
{
    private static InstallEngine CreateEngine(InstallerTestEnvironment environment, string package, RegistrationOptions? requested = null)
    {
        return new InstallEngine(
            new InstallRequest
            {
                SourceDirectory = package,
                RequestedAction = InstallAction.Update,
                Registration = requested ?? RegistrationOptions.Default
            },
            environment.Presenter,
            new SetupLog(),
            environment.Registration);
    }

    [Fact]
    public async Task Update_PreservesTheCustomExtensionIdsAndWritesSchema2State()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(SetupExitCodes.Success, result.ExitCode);

        InstallState? saved = InstallStateStore.Load();
        Assert.NotNull(saved);
        Assert.Equal(InstallState.CurrentSchema, saved!.Schema);
        Assert.Equal("1.1.0", saved.Version);
        Assert.Equal(new[] { "custom-chromium-id" }, saved.ChromiumExtensionIds);
        Assert.Equal(new[] { "custom@firefox-id" }, saved.FirefoxExtensionIds);
        Assert.Null(saved.ExtensionIds);

        RegistrationCall applied = Assert.Single(environment.Registration.Applied);
        Assert.Equal("1.1.0", applied.Version);
        Assert.Equal(new[] { "custom-chromium-id" }, applied.Options.ChromiumExtensionIds);
        Assert.Equal(new[] { "custom@firefox-id" }, applied.Options.FirefoxExtensionIds);

        Assert.False(File.Exists(environment.MarkerPath));
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string installed));
        Assert.Equal("1.1.0", installed);
    }

    [Fact]
    public async Task Update_WhenRegistrationRemovalFails_LeavesAMarkerRecoveryCanUse()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        environment.Registration.FailRemove = true;
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        TransactionLoadResult pending = InstallStateStore.LoadTransactionResult();
        Assert.NotNull(pending.Transaction);
        Assert.True(pending.Transaction!.RegistrationRemoved);
        Assert.Single(Directory.GetDirectories(environment.Root, ".staging-*"));

        environment.Registration.FailRemove = false;
        RecoveryResult recovery = UpdateRecovery.Run(new SetupLog(), environment.Registration);

        Assert.True(recovery.ShouldContinue);
        RegistrationCall restored = Assert.Single(environment.Registration.Applied);
        Assert.Equal("1.0.0", restored.Version);
        Assert.Equal(new[] { "custom-chromium-id" }, restored.Options.ChromiumExtensionIds);
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.Empty(Directory.GetDirectories(environment.Root, ".staging-*"));
    }

    [Fact]
    public async Task Update_FromLegacySchema1State_PreservesTheMigratedExtensionIds()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.WriteLegacyInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.True(result.Success, result.Message);
        InstallState? saved = InstallStateStore.Load();
        Assert.NotNull(saved);
        Assert.Equal(InstallState.CurrentSchema, saved!.Schema);
        Assert.Equal(new[] { "custom-chromium-id" }, saved.ChromiumExtensionIds);
        Assert.Equal(new[] { "custom@firefox-id" }, saved.FirefoxExtensionIds);
        RegistrationCall applied = Assert.Single(environment.Registration.Applied);
        Assert.Equal(new[] { "custom-chromium-id" }, applied.Options.ChromiumExtensionIds);
    }

    [Fact]
    public async Task Update_WithExplicitIds_UsesThemAsTheTargetRegistration()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "recorded-chromium", "recorded@firefox");
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);
        RegistrationOptions requested = RegistrationOptions.FromLists(new[] { "explicit-chromium" }, new[] { "explicit@firefox" });

        InstallResult result = await CreateEngine(environment, package, requested).RunAsync();

        Assert.True(result.Success, result.Message);
        InstallState? saved = InstallStateStore.Load();
        Assert.Equal(new[] { "explicit-chromium" }, saved!.ChromiumExtensionIds);
        Assert.Equal(new[] { "explicit@firefox" }, saved.FirefoxExtensionIds);
        RegistrationCall applied = Assert.Single(environment.Registration.Applied);
        Assert.Equal(new[] { "explicit-chromium" }, applied.Options.ChromiumExtensionIds);
    }

    [Fact]
    public async Task Update_WithoutRecordedIds_FailsBeforeAskingTheUser()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        InstallState state = new()
        {
            Schema = InstallState.CurrentSchema,
            Version = "1.0.0",
            AppDir = environment.AppDir,
            DataDir = Path.Combine(environment.Root, "data")
        };
        InstallStateStore.Save(state);
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "9.9.9", realBinaries: false);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.Empty(environment.Presenter.Prompts);
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.Equal("1.0.0", InstallStateStore.Load()!.Version);
    }

    [Fact]
    public async Task Update_WhenTheSelfTestFails_RestoresThePreviousAppAndRegistration()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        File.WriteAllText(Path.Combine(environment.AppDir, "old-only.txt"), "previous version");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        string package = Path.Combine(environment.Root, "package");
        environment.WriteBrokenPackage(package, "9.9.9");

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(environment.AppDir, "old-only.txt")));
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);

        InstallState? saved = InstallStateStore.Load();
        Assert.Equal("1.0.0", saved!.Version);
        Assert.Equal(new[] { "custom-chromium-id" }, saved.ChromiumExtensionIds);

        RegistrationCall restored = Assert.Single(environment.Registration.Applied);
        Assert.Equal("1.0.0", restored.Version);
        Assert.Equal(new[] { "custom-chromium-id" }, restored.Options.ChromiumExtensionIds);
        Assert.Equal(new[] { "custom@firefox-id" }, restored.Options.FirefoxExtensionIds);

        Assert.False(File.Exists(environment.MarkerPath));
        Assert.False(Directory.Exists(environment.BackupDir));
    }

    [Fact]
    public async Task Update_WhenPostCommitVerificationFails_RollsBackTheStateAndRegistration()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        environment.Registration.FailVerify = true;
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.Equal(2, environment.Registration.Applied.Count);
        Assert.Equal("1.1.0", environment.Registration.Applied[0].Version);
        Assert.Equal("1.0.0", environment.Registration.Applied[1].Version);
        Assert.Equal(new[] { "custom-chromium-id" }, environment.Registration.Applied[1].Options.ChromiumExtensionIds);

        InstallState? saved = InstallStateStore.Load();
        Assert.Equal("1.0.0", saved!.Version);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public async Task Update_AfterAnInterruptedBackupRestore_StopsWithThePreviousVersion()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Committing));
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "9.9.9", realBinaries: true);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.Empty(environment.Registration.Applied);
    }

    [Fact]
    public async Task FreshInstall_SucceedsAndRegistersTheDefaults()
    {
        using InstallerTestEnvironment environment = new();
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallEngine engine = new(
            new InstallRequest { SourceDirectory = package },
            environment.Presenter,
            new SetupLog(),
            environment.Registration);

        InstallResult result = await engine.RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(SetupExitCodes.Success, result.ExitCode);
        InstallState? saved = InstallStateStore.Load();
        Assert.NotNull(saved);
        Assert.Equal("1.1.0", saved!.Version);
        Assert.Equal(new[] { BrowserCatalog.ChromeStoreId }, saved.ChromiumExtensionIds);
        Assert.Equal(new[] { BrowserCatalog.FirefoxExtensionId }, saved.FirefoxExtensionIds);
        RegistrationCall applied = Assert.Single(environment.Registration.Applied);
        Assert.Equal("1.1.0", applied.Version);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string installed));
        Assert.Equal("1.1.0", installed);
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public async Task Repair_SameVersion_Succeeds()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.1.0");
        environment.CreateInstallState("1.1.0", "custom-chromium-id", "custom@firefox-id");
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallEngine engine = new(
            new InstallRequest { SourceDirectory = package, RequestedAction = InstallAction.Repair },
            environment.Presenter,
            new SetupLog(),
            environment.Registration);

        InstallResult result = await engine.RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.Contains("repaired", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "custom-chromium-id" }, InstallStateStore.Load()!.ChromiumExtensionIds);
    }

    // M5: a fresh install over an orphaned backup must restore it, never delete it.
    [Fact]
    public async Task FreshInstall_WithVerifiedOrphanedBackup_RestoresItInsteadOfDeleting()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");

        InstallEngine engine = new(
            new InstallRequest { SourceDirectory = Path.Combine(environment.Root, "missing-package") },
            environment.Presenter,
            new SetupLog(),
            environment.Registration);

        InstallResult result = await engine.RunAsync();

        Assert.False(result.Success);
        Assert.Equal(SetupExitCodes.InvalidPayload, result.ExitCode);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string installed));
        Assert.Equal("1.0.0", installed);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public async Task FreshInstall_WithUnverifiableOrphanedBackup_StopsAndKeepsIt()
    {
        using InstallerTestEnvironment environment = new();
        Directory.CreateDirectory(environment.BackupDir);
        environment.WriteJson(Path.Combine(environment.BackupDir, "notes.txt"), "keep me");
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallEngine engine = new(
            new InstallRequest { SourceDirectory = package },
            environment.Presenter,
            new SetupLog(),
            environment.Registration);

        InstallResult result = await engine.RunAsync();

        Assert.False(result.Success);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.Contains("manual recovery", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(environment.Presenter.Prompts);
        Assert.False(Directory.Exists(environment.AppDir));
        Assert.True(File.Exists(Path.Combine(environment.BackupDir, "notes.txt")));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public async Task Update_AfterAnOrphanedBackupIsRestored_ProceedsWithTheUpdate()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string installed));
        Assert.Equal("1.1.0", installed);
        Assert.Equal("1.1.0", InstallStateStore.Load()!.Version);
        RegistrationCall applied = Assert.Single(environment.Registration.Applied);
        Assert.Equal(new[] { "custom-chromium-id" }, applied.Options.ChromiumExtensionIds);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public async Task Update_WithAStaleBackup_PreservesItInsteadOfDeletingIt()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        environment.WriteInstalledPayload(environment.BackupDir, "0.9.0");
        string package = Path.Combine(environment.Root, "package");
        environment.WritePackage(package, "1.1.0", realBinaries: true);

        InstallResult result = await CreateEngine(environment, package).RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string installed));
        Assert.Equal("1.1.0", installed);
        Assert.False(Directory.Exists(environment.BackupDir));
        string preserved = Assert.Single(Directory.GetDirectories(environment.Root, "app.old.unexpected-*"));
        Assert.True(InstallVerification.TryReadAppVersion(preserved, out string preservedVersion));
        Assert.Equal("0.9.0", preservedVersion);
    }
}
