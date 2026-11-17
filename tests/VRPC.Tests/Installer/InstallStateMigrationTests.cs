using System.Text;
using System.Text.Json;
using VRPC.Application;
using VRPC.Install;

namespace VRPC.Tests.Installer;

// §8.C: schema 1 install records are migrated into Chromium/Firefox ID families;
// an unreadable record is flagged instead of being treated as unregistered.
public class InstallStateMigrationTests
{
    private static string LegacyInstallJson(IEnumerable<string> extensionIds, IEnumerable<(string Name, string Path, string Sha)> manifests)
    {
        string manifestJson = string.Join(", ", manifests.Select(manifest =>
            JsonSerializer.Serialize(new { name = manifest.Name, path = manifest.Path, sha256 = manifest.Sha })));

        string idsJson = string.Join(", ", extensionIds.Select(id => JsonSerializer.Serialize(id)));
        return $$"""
        {
          "schema": 1,
          "version": "1.1.0",
          "channel": "stable",
          "appDir": "{{AppPaths.App.Replace("\\", "\\\\")}}",
          "extensionIds": [{{idsJson}}],
          "manifests": [{{manifestJson}}],
          "registryKeys": []
        }
        """;
    }

    [Fact]
    public void Load_LegacyFlatStateWithManifests_SplitsTheFamilies()
    {
        using InstallerTestEnvironment environment = new();
        string chromiumId = "custom-chromium-id";
        string firefoxId = "custom@firefox-id";
        environment.WriteManifests(chromiumId, firefoxId);

        List<RegisteredManifest> manifests = new()
        {
            new() { Name = BrowserCatalog.ChromiumManifestName, Path = Path.Combine(environment.ManifestsDir, BrowserCatalog.ChromiumManifestName), Sha256 = InstallerTestEnvironment.HashFile(Path.Combine(environment.ManifestsDir, BrowserCatalog.ChromiumManifestName)) },
            new() { Name = BrowserCatalog.FirefoxManifestName, Path = Path.Combine(environment.ManifestsDir, BrowserCatalog.FirefoxManifestName), Sha256 = InstallerTestEnvironment.HashFile(Path.Combine(environment.ManifestsDir, BrowserCatalog.FirefoxManifestName)) }
        };
        string json = LegacyInstallJson(
            new[] { chromiumId, firefoxId },
            manifests.Select(manifest => (manifest.Name, manifest.Path, manifest.Sha256)));
        environment.WriteJson(environment.InstallStatePath, json);

        InstallState? state = InstallStateStore.Load();

        Assert.NotNull(state);
        Assert.Equal(InstallState.CurrentSchema, state!.Schema);
        Assert.False(state.RegistrationMigrationFailed);
        Assert.Equal(new[] { chromiumId }, state.ChromiumExtensionIds);
        Assert.Equal(new[] { firefoxId }, state.FirefoxExtensionIds);
        Assert.Null(state.ExtensionIds);
    }

    [Fact]
    public void Load_LegacyFlatStateWithoutManifests_FlagsTheMigrationAsFailed()
    {
        using InstallerTestEnvironment environment = new();
        string json = LegacyInstallJson(new[] { "some-chromium-id", "some@firefox-id" }, Array.Empty<(string, string, string)>());
        environment.WriteJson(environment.InstallStatePath, json);

        InstallState? state = InstallStateStore.Load();

        Assert.NotNull(state);
        Assert.True(state!.RegistrationMigrationFailed);
        Assert.Empty(state.ChromiumExtensionIds);
        Assert.Empty(state.FirefoxExtensionIds);
    }

    [Fact]
    public void Load_LegacyStateWithAMissingManifestFile_FlagsTheMigrationAsFailed()
    {
        using InstallerTestEnvironment environment = new();
        string missing = Path.Combine(environment.ManifestsDir, "gone-chromium.json");
        string json = LegacyInstallJson(
            new[] { "some-chromium-id" },
            new[] { (BrowserCatalog.ChromiumManifestName, missing, new string('0', 64)) });
        environment.WriteJson(environment.InstallStatePath, json);

        InstallState? state = InstallStateStore.Load();

        Assert.NotNull(state);
        Assert.True(state!.RegistrationMigrationFailed);
    }

    [Fact]
    public void Save_NewState_WritesSchema2FamiliesAndNoLegacyFlatList()
    {
        using InstallerTestEnvironment environment = new();
        InstallState state = new()
        {
            Schema = InstallState.CurrentSchema,
            Version = "1.2.0",
            ChromiumExtensionIds = new List<string> { "chromium" },
            FirefoxExtensionIds = new List<string> { "firefox" }
        };
        environment.WriteManifests("chromium", "firefox");

        InstallStateStore.Save(state);
        string json = File.ReadAllText(environment.InstallStatePath, Encoding.UTF8);

        Assert.Contains("\"schema\": 2", json);
        Assert.Contains("chromiumExtensionIds", json);
        Assert.Contains("firefoxExtensionIds", json);
        Assert.DoesNotContain("extensionIds", json.Replace("chromiumExtensionIds", "").Replace("firefoxExtensionIds", ""));
    }

    [Fact]
    public void Transaction_RoundTripsEverySchema2Field()
    {
        using InstallerTestEnvironment environment = new();
        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
        marker.RegistrationRemoved = true;

        environment.WriteMarker(marker);
        TransactionLoadResult loaded = InstallStateStore.LoadTransactionResult();

        Assert.False(loaded.Corrupt);
        Assert.NotNull(loaded.Transaction);
        UpdateTransaction roundTripped = loaded.Transaction!;
        Assert.Equal(UpdateTransaction.CurrentSchema, roundTripped.Schema);
        Assert.Equal(UpdatePhase.Committing, roundTripped.Phase);
        Assert.Equal(marker.TargetVersion, roundTripped.TargetVersion);
        Assert.Equal(marker.PreviousVersion, roundTripped.PreviousVersion);
        Assert.Equal(marker.StagingDir, roundTripped.StagingDir);
        Assert.Equal(marker.BackupDir, roundTripped.BackupDir);
        Assert.True(roundTripped.HadAppBefore);
        Assert.True(roundTripped.RegistrationRemoved);
        Assert.Equal(new[] { "new-chromium" }, roundTripped.TargetChromiumExtensionIds);
        Assert.Equal(new[] { "new@firefox" }, roundTripped.TargetFirefoxExtensionIds);
        Assert.Equal(new[] { "old-chromium" }, roundTripped.PreviousChromiumExtensionIds);
        Assert.Equal(new[] { "old@firefox" }, roundTripped.PreviousFirefoxExtensionIds);
    }

    [Fact]
    public void CorruptTransaction_IsPreservedAndReported()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteJson(environment.MarkerPath, "{ this is not json");

        TransactionLoadResult loaded = InstallStateStore.LoadTransactionResult();

        Assert.True(loaded.Corrupt);
        Assert.NotNull(loaded.CorruptPath);
        Assert.True(File.Exists(loaded.CorruptPath));
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.StartsWith(environment.MarkerPath + ".corrupt-", loaded.CorruptPath);
    }

    [Fact]
    public void LegacySchemaTransaction_IsMarkedAsLegacy()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteJson(environment.MarkerPath, """
        {
          "schema": 1,
          "phase": "committing",
          "targetVersion": "2.0.0",
          "previousVersion": "1.0.0",
          "stagingDir": "",
          "backupDir": ""
        }
        """);

        TransactionLoadResult loaded = InstallStateStore.LoadTransactionResult();

        Assert.False(loaded.Corrupt);
        Assert.True(loaded.Transaction!.IsLegacySchema);
    }
}
