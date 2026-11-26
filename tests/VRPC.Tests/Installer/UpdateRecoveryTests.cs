using VRPC.Deployment.Install;

namespace VRPC.Tests.Installer;

// §8.B: every branch of the interrupted-update recovery state machine, built
// from hand-made marker/app/app.old/install-state fixtures. Inconclusive states
// must keep the marker and the evidence instead of deleting anything.
public class UpdateRecoveryTests
{
    private static RecoveryResult Recover(InstallerTestEnvironment environment)
    {
        return UpdateRecovery.Run(new SetupLog(), environment.Registration);
    }

    [Fact]
    public void NoMarker_DoesNothing()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        Assert.False(result.Recovered);
        Assert.True(File.Exists(Path.Combine(environment.AppDir, "payload.json")));
    }

    [Fact]
    public void StaleMarkerBeforeTheSwap_RemovesStagingAndMarkerAndKeepsTheApp()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        Directory.CreateDirectory(environment.StagingPath);
        File.WriteAllText(Path.Combine(environment.StagingPath, "partial.bin"), "x");
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Staged));

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        Assert.True(result.Recovered);
        Assert.False(Directory.Exists(environment.StagingPath));
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
    }

    [Fact]
    public void MarkerWithOnlyStaging_IsCleared()
    {
        using InstallerTestEnvironment environment = new();
        Directory.CreateDirectory(environment.StagingPath);
        File.WriteAllText(Path.Combine(environment.StagingPath, "partial.bin"), "x");
        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Staging);
        marker.HadAppBefore = false;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        Assert.False(Directory.Exists(environment.StagingPath));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void MissingAppWithBackup_RestoresThePreviousVersionAndStops()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Committing));

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.Contains("run setup again", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void MissingAppWithUnverifiableBackup_KeepsTheEvidenceAndStops()
    {
        using InstallerTestEnvironment environment = new();
        Directory.CreateDirectory(environment.BackupDir);
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Committing));

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.False(Directory.Exists(environment.AppDir));
        Assert.True(Directory.Exists(environment.BackupDir));
        Assert.True(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void AppAndBackupPresent_Uncommitted_RestoresThePreviousVersionAndStops()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "2.0.0");
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Committing));
        environment.Registration.Enabled = false;

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.Empty(Directory.GetDirectories(environment.Root, "app.failed-*"));
    }

    [Fact]
    public void AppAndBackupPresent_VerifiedCommit_FinishesTheCleanup()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "2.0.0");
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("2.0.0", "new-chromium", "new@firefox");
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Verifying));
        environment.Registration.Enabled = false;

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        Assert.True(result.Recovered);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("2.0.0", version);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void AppAndBackupPresent_UnverifiableBackup_KeepsTheMarker()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "2.0.0");
        Directory.CreateDirectory(environment.BackupDir);
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Committing));

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.True(Directory.Exists(environment.AppDir));
        Assert.True(Directory.Exists(environment.BackupDir));
        Assert.True(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void AppAtTargetWithoutBackup_FinishesTheCommit()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "2.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Registering));
        environment.Registration.Enabled = false;

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        Assert.True(result.Recovered);
        InstallState? state = InstallStateStore.Load();
        Assert.NotNull(state);
        Assert.Equal("2.0.0", state!.Version);
        Assert.Equal(new[] { "new-chromium" }, state.ChromiumExtensionIds);
        Assert.Equal(new[] { "new@firefox" }, state.FirefoxExtensionIds);
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void AppAtTargetWithRegistrationRemoved_ReappliesTheTargetIds()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "2.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Registering);
        marker.RegistrationRemoved = true;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        RegistrationCall call = Assert.Single(environment.Registration.Applied);
        Assert.Equal("2.0.0", call.Version);
        Assert.Equal(new[] { "new-chromium" }, call.Options.ChromiumExtensionIds);
        InstallState? state = InstallStateStore.Load();
        Assert.Equal("2.0.0", state!.Version);
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void CorruptMarkerWithBackupAndNoApp_RestoresTheBackupAndContinues()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.WriteJson(environment.MarkerPath, "{ this is not json");

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        Assert.True(result.Recovered);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.Single(Directory.GetFiles(environment.StateDir, "update.json.corrupt-*"));
    }

    [Fact]
    public void CorruptMarkerWithAppPresent_StopsWithoutTouchingTheApp()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.WriteJson(environment.MarkerPath, "{ this is not json");

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.Single(Directory.GetFiles(environment.StateDir, "update.json.corrupt-*"));
    }

    [Fact]
    public void NothingInstalledWithHadAppBefore_KeepsTheMarkerAndStops()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Committing));

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.True(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void RegistrationRemovedOnAStaleMarker_RestoresThePreviousRegistration()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        UpdateTransaction marker = environment.NewMarker(UpdatePhase.RegistrationRemoved);
        marker.RegistrationRemoved = true;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        RegistrationCall call = Assert.Single(environment.Registration.Applied);
        Assert.Equal("1.0.0", call.Version);
        Assert.Equal(new[] { "old-chromium" }, call.Options.ChromiumExtensionIds);
        Assert.Equal(new[] { "old@firefox" }, call.Options.FirefoxExtensionIds);
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void MarkerStagingOutsideTheRoot_IsNotDeletedAndStops()
    {
        using InstallerTestEnvironment environment = new();
        string outside = Path.Combine(Path.GetTempPath(), "vrpc-outside-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
            UpdateTransaction marker = environment.NewMarker(UpdatePhase.Staged);
            marker.StagingDir = outside;
            environment.WriteMarker(marker);

            RecoveryResult result = Recover(environment);

            Assert.False(result.ShouldContinue);
            Assert.True(Directory.Exists(outside));
            Assert.True(File.Exists(environment.MarkerPath));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MarkerBackupOutsideTheRoot_StopsWithoutTouchingIt()
    {
        using InstallerTestEnvironment environment = new();
        string outside = Path.Combine(Path.GetTempPath(), "vrpc-outside-backup-" + Guid.NewGuid().ToString("N"));
        string outsideBackup = Path.Combine(outside, "app.old");
        Directory.CreateDirectory(outsideBackup);
        try
        {
            UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
            marker.BackupDir = outsideBackup;
            environment.WriteMarker(marker);

            RecoveryResult result = Recover(environment);

            Assert.False(result.ShouldContinue);
            Assert.True(Directory.Exists(outsideBackup));
            Assert.True(File.Exists(environment.MarkerPath));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LegacySchemaMarker_RestoresTheBackupLikeTheCurrentSchema()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        environment.WriteJson(environment.MarkerPath, $$"""
        {
          "schema": 1,
          "phase": "committing",
          "targetVersion": "2.0.0",
          "previousVersion": "1.0.0",
          "stagingDir": "{{environment.StagingPath.Replace("\\", "\\\\")}}",
          "backupDir": "{{environment.BackupDir.Replace("\\", "\\\\")}}",
          "startedAtUtc": "{{DateTime.UtcNow:o}}"
        }
        """);
        environment.Registration.Enabled = false;

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    // M5: without a marker an orphaned app.old is the only previous install left.
    // It is restored (and logged) only when it verifies and agrees with the state.
    [Fact]
    public void NoMarkerWithMissingAppAndVerifiedBackup_RestoresThePreviousVersionAndContinues()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "custom-chromium-id", "custom@firefox-id");
        SetupLog log = new();

        RecoveryResult result = UpdateRecovery.Run(log, environment.Registration);

        Assert.True(result.ShouldContinue);
        Assert.True(result.Recovered);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(Directory.Exists(environment.BackupDir));
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.Contains("restored the previous installation", File.ReadAllText(log.Path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoMarkerWithMissingAppAndUnverifiableBackup_StopsAndKeepsTheBackup()
    {
        using InstallerTestEnvironment environment = new();
        Directory.CreateDirectory(environment.BackupDir);
        environment.WriteJson(Path.Combine(environment.BackupDir, "notes.txt"), "keep me");
        SetupLog log = new();

        RecoveryResult result = UpdateRecovery.Run(log, environment.Registration);

        Assert.False(result.ShouldContinue);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.Contains("manual recovery", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(environment.AppDir));
        Assert.True(Directory.Exists(environment.BackupDir));
        Assert.True(File.Exists(Path.Combine(environment.BackupDir, "notes.txt")));
        Assert.False(File.Exists(environment.MarkerPath));
    }

    [Fact]
    public void NoMarkerWithMissingAppAndMismatchedInstallState_StopsAndKeepsTheBackup()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("2.0.0", "new-chromium", "new@firefox");

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.Equal(SetupExitCodes.Failed, result.ExitCode);
        Assert.Contains("manual recovery", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(environment.AppDir));
        Assert.True(Directory.Exists(environment.BackupDir));
    }

    [Fact]
    public void NoMarkerWithAppPresentAndOrphanedBackup_LeavesBothUntouched()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "1.0.0");
        environment.WriteInstalledPayload(environment.BackupDir, "0.9.0");

        RecoveryResult result = Recover(environment);

        Assert.True(result.ShouldContinue);
        Assert.False(result.Recovered);
        Assert.True(Directory.Exists(environment.AppDir));
        Assert.True(Directory.Exists(environment.BackupDir));
    }

    // AU-02: the interrupted setup already removed the registration, so restoring
    // the files and then verifying the missing registration could never succeed.
    [Fact]
    public void RestoreFromBackup_RestoresRegistrationBeforeVerifyingIt()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        environment.Registration.Remove(null);
        Assert.False(environment.Registration.Registered);

        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
        marker.RegistrationRemoved = true;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.True(environment.Registration.Registered);
        Assert.Single(environment.Registration.Applied);
        int apply = environment.Registration.Calls.IndexOf("apply");
        int verify = environment.Registration.Calls.LastIndexOf("verify");
        Assert.True(apply >= 0);
        Assert.True(verify > apply, "registration must be restored before it is verified");
        Assert.Equal("1.0.0", InstallStateStore.Load()!.Version);
        Assert.False(File.Exists(environment.MarkerPath));
    }

    // AU-03: the target state was already written before the crash; the schema-3
    // snapshot is the only record of what the previous installation looked like.
    [Fact]
    public void RestoreFromBackup_UsesTheSnapshotWhenTheDiskHasTheTargetState()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        InstallState previous = environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        InstallStateStore.Save(new InstallState
        {
            Schema = InstallState.CurrentSchema,
            Version = "2.0.0",
            AppDir = environment.AppDir,
            DataDir = Path.Combine(environment.Root, "data"),
            ChromiumExtensionIds = new List<string> { "new-chromium" },
            FirefoxExtensionIds = new List<string> { "new@firefox" }
        });

        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
        marker.PreviousInstallState = previous;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.Equal("1.0.0", InstallStateStore.Load()!.Version);
        Assert.Equal(new[] { "old-chromium" }, InstallStateStore.Load()!.ChromiumExtensionIds);
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.True(environment.Registration.Registered);
    }

    [Fact]
    public void LegacyMarkerWithMismatchedState_KeepsTheMarkerInsteadOfClearingIt()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("2.0.0", "new-chromium", "new@firefox");
        environment.WriteMarker(environment.NewMarker(UpdatePhase.Committing));

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.True(File.Exists(environment.MarkerPath));
        Assert.Equal("2.0.0", InstallStateStore.Load()!.Version);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
    }

    [Fact]
    public void AppAndBackupWithTargetStateAndSnapshot_RestoresThePreviousVersion()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.AppDir, "2.0.0");
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        InstallState previous = environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        InstallStateStore.Save(new InstallState
        {
            Schema = InstallState.CurrentSchema,
            Version = "2.0.0",
            AppDir = environment.AppDir,
            DataDir = Path.Combine(environment.Root, "data"),
            ChromiumExtensionIds = new List<string> { "new-chromium" },
            FirefoxExtensionIds = new List<string> { "new@firefox" }
        });

        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
        marker.PreviousInstallState = previous;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.Equal("1.0.0", InstallStateStore.Load()!.Version);
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.False(File.Exists(environment.MarkerPath));
        Assert.Empty(Directory.GetDirectories(environment.Root, "app.failed-*"));
    }

    [Fact]
    public void StateWriteFailureDuringRecovery_KeepsTheMarkerAndRestoredApp()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        InstallState previous = environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        InstallStateStore.Save(new InstallState
        {
            Schema = InstallState.CurrentSchema,
            Version = "2.0.0",
            AppDir = environment.AppDir,
            DataDir = Path.Combine(environment.Root, "data")
        });

        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
        marker.PreviousInstallState = previous;
        environment.WriteMarker(marker);

        // Sabotage the atomic write's temporary path so the state restore fails.
        Directory.CreateDirectory(Path.Combine(environment.StateDir, "install.json.tmp"));

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.True(File.Exists(environment.MarkerPath));
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.Equal("2.0.0", InstallStateStore.Load()!.Version);
    }

    [Fact]
    public void RegistrationRestoreFailureDuringRecovery_KeepsTheMarker()
    {
        using InstallerTestEnvironment environment = new();
        environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        environment.Registration.Remove(null);
        environment.Registration.FailApply = true;

        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
        marker.RegistrationRemoved = true;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.False(result.ShouldContinue);
        Assert.True(File.Exists(environment.MarkerPath));
        Assert.True(InstallVerification.TryReadAppVersion(environment.AppDir, out string version));
        Assert.Equal("1.0.0", version);
        Assert.Equal("1.0.0", InstallStateStore.Load()!.Version);
    }

    // AU-03 disk-shape matrix: whatever branch runs, a cleared marker always
    // leaves app, install state and registration at one verified version, and a
    // kept marker always keeps usable evidence.
    [Theory]
    [InlineData("app-only")]
    [InlineData("backup-only")]
    [InlineData("both")]
    public void EveryRecoveryOutcomeIsInternallyConsistent(string shape)
    {
        using InstallerTestEnvironment environment = new();
        InstallState previous = environment.CreateInstallState("1.0.0", "old-chromium", "old@firefox");
        if (shape is "backup-only" or "both")
        {
            environment.WriteInstalledPayload(environment.BackupDir, "1.0.0");
        }
        if (shape is "app-only" or "both")
        {
            environment.WriteInstalledPayload(environment.AppDir, "2.0.0");
        }

        UpdateTransaction marker = environment.NewMarker(UpdatePhase.Committing);
        marker.PreviousInstallState = previous;
        environment.WriteMarker(marker);

        RecoveryResult result = Recover(environment);

        Assert.True(environment.Registration.Registered, "recovery must leave a usable browser registration");
        if (!File.Exists(environment.MarkerPath))
        {
            InstallState state = InstallStateStore.Load()!;
            Assert.Empty(InstallVerification.VerifyApp(environment.AppDir, state.Version));
            Assert.Empty(InstallVerification.VerifyInstallState(state.Version));
        }
        else
        {
            Assert.False(result.ShouldContinue);
            Assert.True(Directory.Exists(environment.AppDir) || Directory.Exists(environment.BackupDir));
        }
    }
}
