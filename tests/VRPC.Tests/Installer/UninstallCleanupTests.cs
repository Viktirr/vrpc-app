using VRPC.Application;
using VRPC.Install;

namespace VRPC.Tests.Installer;

// L10: uninstall removes the updater's downloaded/extracted packages and its
// cached state, but keeps settings and listening data unless --purge.
public class UninstallCleanupTests
{
    private static InstallEngine CreateUninstallEngine(InstallerTestEnvironment environment, bool purge)
    {
        return new InstallEngine(
            new InstallRequest { RequestedAction = InstallAction.Uninstall, PurgeData = purge },
            environment.Presenter,
            new SetupLog(),
            environment.Registration);
    }

    private static void SeedUpdaterArtifacts(InstallerTestEnvironment environment)
    {
        string operation = Path.Combine(environment.StateDir, "updates", "operation-1");
        Directory.CreateDirectory(Path.Combine(operation, "package"));
        environment.WriteJson(Path.Combine(operation, "package.zip.part"), "partial download");
        environment.WriteJson(Path.Combine(operation, "package", "VRPC.exe"), "extracted payload");
        environment.WriteJson(Path.Combine(environment.StateDir, "updater-state.json"), "{}");
        environment.WriteJson(Path.Combine(environment.StateDir, "updater-state.json.bak"), "{}");
    }

    private static void SeedUserData(InstallerTestEnvironment environment)
    {
        Directory.CreateDirectory(Path.Combine(environment.Root, "data"));
        environment.WriteJson(Path.Combine(environment.Root, "data", "Config.json"), "{}");
        environment.WriteJson(Path.Combine(environment.Root, "data", "ListeningData.json"), "{}");
    }

    [Fact]
    public async Task Uninstall_RemovesUpdaterArtifacts_ButKeepsSettingsAndListeningData()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.1.0");
        environment.CreateInstallState("1.1.0", "custom-chromium-id", "custom@firefox-id");
        SeedUserData(environment);
        SeedUpdaterArtifacts(environment);

        InstallResult result = await CreateUninstallEngine(environment, purge: false).RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(SetupExitCodes.Success, result.ExitCode);
        Assert.Contains("were kept", result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(Directory.Exists(environment.AppDir));
        Assert.False(Directory.Exists(Path.Combine(environment.StateDir, "updates")));
        Assert.False(File.Exists(Path.Combine(environment.StateDir, "updater-state.json")));
        Assert.False(File.Exists(Path.Combine(environment.StateDir, "updater-state.json.bak")));
        Assert.False(File.Exists(environment.InstallStatePath));

        Assert.True(File.Exists(Path.Combine(environment.Root, "data", "Config.json")));
        Assert.True(File.Exists(Path.Combine(environment.Root, "data", "ListeningData.json")));
    }

    [Fact]
    public async Task UninstallWithPurge_RemovesUpdaterArtifactsAndUserData()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.1.0");
        environment.CreateInstallState("1.1.0", "custom-chromium-id", "custom@firefox-id");
        SeedUserData(environment);
        SeedUpdaterArtifacts(environment);

        InstallResult result = await CreateUninstallEngine(environment, purge: true).RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.Contains("data was removed", result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(Directory.Exists(Path.Combine(environment.StateDir, "updates")));
        Assert.False(File.Exists(Path.Combine(environment.StateDir, "updater-state.json")));
        Assert.False(Directory.Exists(Path.Combine(environment.Root, "data")));
    }

    [Fact]
    public async Task Uninstall_WithoutUpdaterArtifacts_StillSucceeds()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.1.0");
        environment.CreateInstallState("1.1.0", "custom-chromium-id", "custom@firefox-id");

        InstallResult result = await CreateUninstallEngine(environment, purge: false).RunAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(environment.AppDir));
        Assert.False(Directory.Exists(Path.Combine(environment.StateDir, "updates")));
        Assert.False(File.Exists(Path.Combine(environment.StateDir, "updater-state.json")));
    }
}
