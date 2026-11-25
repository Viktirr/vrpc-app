using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VRPC.Application;
using VRPC.Install;

namespace VRPC.Tests.Installer;

// Shared fixture for installer tests. Every test gets a fresh temp root; the
// process-wide AppPaths override is restored on dispose and the whole assembly
// runs sequentially (see TestAssembly.cs), so mutating it is safe.
public sealed class InstallerTestEnvironment : IDisposable
{
    private const string RootVariable = "VRPC_ROOT";
    private const string NoRegistryVariable = "VRPC_NO_REGISTRY";
    private const string LockTimeoutVariable = "VRPC_SETUP_LOCK_TIMEOUT_MS";

    private readonly string? _oldRoot;
    private readonly string? _oldNoRegistry;
    private readonly string? _oldLockTimeout;
    private bool _disposed;

    public string Root { get; }
    public FakeRegistration Registration { get; } = new();
    public FakePresenter Presenter { get; } = new();

    public InstallerTestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "vrpc-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "state"));
        Directory.CreateDirectory(Path.Combine(Root, "data"));
        Directory.CreateDirectory(Path.Combine(Root, "manifests"));

        _oldRoot = Environment.GetEnvironmentVariable(RootVariable);
        _oldNoRegistry = Environment.GetEnvironmentVariable(NoRegistryVariable);
        _oldLockTimeout = Environment.GetEnvironmentVariable(LockTimeoutVariable);

        Environment.SetEnvironmentVariable(RootVariable, Root);
        Environment.SetEnvironmentVariable(NoRegistryVariable, "1");
        Environment.SetEnvironmentVariable(LockTimeoutVariable, "1500");
        AppPaths.OverrideRoot(Root);
    }

    public string AppDir => Path.Combine(Root, "app");
    public string BackupDir => Path.Combine(Root, "app.old");
    public string StateDir => Path.Combine(Root, "state");
    public string ManifestsDir => Path.Combine(Root, "manifests");
    public string MarkerPath => Path.Combine(StateDir, "update.json");
    public string InstallStatePath => Path.Combine(StateDir, "install.json");
    public string LockPath => Path.Combine(StateDir, "setup-operation.lock");
    public string StagingPath => Path.Combine(Root, ".staging-tests");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // A package next to a payload manifest. realBinaries copies the application
    // built by the test project so the installer's --selftest child really runs.
    public void WritePackage(string directory, string version, bool realBinaries)
    {
        Directory.CreateDirectory(directory);
        List<string> files = new();

        if (realBinaries)
        {
            foreach (string file in new[] { "VRPC.exe", "VRPC.dll", "VRPC.runtimeconfig.json", "VRPC.deps.json", "DiscordRPC.dll" })
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(directory, file), overwrite: true);
                files.Add(file);
            }
        }
        else
        {
            File.WriteAllText(Path.Combine(directory, "VRPC.exe"), "not a real executable " + version, new UTF8Encoding(false));
            files.Add("VRPC.exe");
        }

        WritePayloadManifest(directory, version, files);
    }

    // A package whose entry point cannot run, so the self-test fails.
    public void WriteBrokenPackage(string directory, string version)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "VRPC.exe"), "this is not a valid executable", new UTF8Encoding(false));
        WritePayloadManifest(directory, version, new List<string> { "VRPC.exe" });
    }

    // An installed (or backup) application folder whose payload.json reports version.
    public void WriteInstalledPayload(string directory, string version)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "VRPC.exe"), "installed executable " + version, new UTF8Encoding(false));
        WritePayloadManifest(directory, version, new List<string> { "VRPC.exe" });
    }

    private void WritePayloadManifest(string directory, string version, List<string> files)
    {
        var manifest = new
        {
            schema = 1,
            product = "VRPC",
            version,
            channel = "stable",
            entryPoint = "VRPC.exe",
            files = files.Select(file => new
            {
                path = file,
                size = new FileInfo(Path.Combine(directory, file)).Length,
                sha256 = HashFile(Path.Combine(directory, file))
            }).ToList()
        };

        File.WriteAllText(Path.Combine(directory, "payload.json"), JsonSerializer.Serialize(manifest, PayloadJsonOptions), new UTF8Encoding(false));
    }

    public InstallState CreateInstallState(string version, string chromiumId, string firefoxId)
    {
        InstallState state = new()
        {
            Schema = InstallState.CurrentSchema,
            Version = version,
            Channel = "stable",
            AppDir = AppDir,
            DataDir = Path.Combine(Root, "data"),
            InstalledAtUtc = DateTime.UtcNow.ToString("o"),
            ChromiumExtensionIds = new List<string> { chromiumId },
            FirefoxExtensionIds = new List<string> { firefoxId },
            Manifests = WriteManifests(chromiumId, firefoxId)
        };
        InstallStateStore.Save(state);
        return state;
    }

    public List<RegisteredManifest> WriteManifests(string chromiumId, string firefoxId)
    {
        Directory.CreateDirectory(ManifestsDir);
        string chromiumPath = Path.Combine(ManifestsDir, BrowserCatalog.ChromiumManifestName);
        string firefoxPath = Path.Combine(ManifestsDir, BrowserCatalog.FirefoxManifestName);

        File.WriteAllText(chromiumPath, JsonSerializer.Serialize(new
        {
            name = "vrpc",
            path = AppPaths.AppExe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{chromiumId}/" }
        }, JsonOptions), new UTF8Encoding(false));

        File.WriteAllText(firefoxPath, JsonSerializer.Serialize(new
        {
            name = "vrpc",
            path = AppPaths.AppExe,
            type = "stdio",
            allowed_extensions = new[] { firefoxId }
        }, JsonOptions), new UTF8Encoding(false));

        return new List<RegisteredManifest>
        {
            new() { Name = BrowserCatalog.ChromiumManifestName, Path = chromiumPath, Sha256 = HashFile(chromiumPath) },
            new() { Name = BrowserCatalog.FirefoxManifestName, Path = firefoxPath, Sha256 = HashFile(firefoxPath) }
        };
    }

    public void WriteMarker(UpdateTransaction marker)
    {
        InstallStateStore.SaveTransaction(marker);
    }

    // A pre-schema-2 install record: one flat ID list plus the recorded manifest
    // files, which is what the real 1.1.0-dev installs on disk look like.
    public void WriteLegacyInstallState(string version, string chromiumId, string firefoxId)
    {
        List<RegisteredManifest> manifests = WriteManifests(chromiumId, firefoxId);
        var legacy = new
        {
            schema = 1,
            version,
            channel = "stable",
            appDir = AppDir.Replace("\\", "\\\\"),
            dataDir = Path.Combine(Root, "data").Replace("\\", "\\\\"),
            extensionIds = new[] { chromiumId, firefoxId },
            manifests = manifests.Select(manifest => new
            {
                name = manifest.Name,
                path = manifest.Path.Replace("\\", "\\\\"),
                sha256 = manifest.Sha256
            }).ToList(),
            registryKeys = Array.Empty<object>()
        };

        File.WriteAllText(InstallStatePath, JsonSerializer.Serialize(legacy, JsonOptions), new UTF8Encoding(false));
    }

    public UpdateTransaction NewMarker(string phase, string previous = "1.0.0", string target = "2.0.0")
    {
        return new UpdateTransaction
        {
            Schema = UpdateTransaction.CurrentSchema,
            Phase = phase,
            TargetVersion = target,
            PreviousVersion = previous,
            StagingDir = StagingPath,
            BackupDir = BackupDir,
            HadAppBefore = true,
            StartedAtUtc = DateTime.UtcNow.ToString("o"),
            TargetChromiumExtensionIds = new List<string> { "new-chromium" },
            TargetFirefoxExtensionIds = new List<string> { "new@firefox" },
            PreviousChromiumExtensionIds = new List<string> { "old-chromium" },
            PreviousFirefoxExtensionIds = new List<string> { "old@firefox" }
        };
    }

    public void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;

        Environment.SetEnvironmentVariable(RootVariable, _oldRoot);
        Environment.SetEnvironmentVariable(NoRegistryVariable, _oldNoRegistry);
        Environment.SetEnvironmentVariable(LockTimeoutVariable, _oldLockTimeout);
        AppPaths.ResetRoot();

        try
        {
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
        catch { }
    }
}

public sealed record RegistrationCall(string AppExe, RegistrationOptions Options, string Version);

public sealed class FakeRegistration : IInstallerRegistration
{
    public bool Enabled { get; set; } = true;
    public bool FailApply { get; set; }
    public bool FailVerify { get; set; }
    public int FailNextVerify { get; set; }
    private int _verifyFailuresRemaining;
    public bool FailRemove { get; set; }

    // Stateful model of the real registration: Verify fails after Remove until
    // Apply restores a manifest set. Recovery must restore before it verifies.
    public bool Registered { get; set; } = true;
    public int VerifyCount { get; private set; }
    public List<string> Calls { get; } = new();
    public List<RegistrationCall> Applied { get; } = new();
    public List<string> VerifiedAppExes { get; } = new();
    public List<InstallState?> RemovedStates { get; } = new();

    public int RemoveArpCount { get; private set; }
    public int RemoveLegacyCount { get; private set; }
    public int RemoveAllKeysCount { get; private set; }

    public RegistrationResult Apply(string appExe, RegistrationOptions options, string version)
    {
        if (FailApply) { throw new RegistrationException("The fake registration refused to apply."); }
        Calls.Add("apply");
        Applied.Add(new RegistrationCall(appExe, options.Clone(), version));
        Registered = true;
        return new RegistrationResult();
    }

    public void Remove(InstallState? state)
    {
        if (FailRemove) { throw new RegistrationException("The fake registration refused to be removed."); }
        Calls.Add("remove");
        RemovedStates.Add(state);
        Registered = false;
    }

    public void RemoveArp() => RemoveArpCount++;

    public void RemoveLegacy() => RemoveLegacyCount++;

    public void RemoveAllKeys() => RemoveAllKeysCount++;

    public void Verify(string appExe, IReadOnlyList<RegisteredManifest> manifests)
    {
        VerifyCount++;
        Calls.Add("verify");
        if (FailVerify) { throw new RegistrationException("The fake registration verification failed."); }
        if (FailNextVerify > 0 && _verifyFailuresRemaining < FailNextVerify)
        {
            _verifyFailuresRemaining++;
            throw new RegistrationException("The fake registration verification failed.");
        }
        if (!Registered) { throw new RegistrationException("The fake registration was removed and has not been restored."); }
        VerifiedAppExes.Add(appExe);
    }
}

public sealed class FakePresenter : IPresenter
{
    public InstallDecision ConfirmDecision { get; set; } = InstallDecision.Proceed;
    public bool ForceCloseDecision { get; set; } = true;
    public List<ConfirmPrompt> Prompts { get; } = new();
    public List<string> Steps { get; } = new();
    public InstallResult? Result { get; private set; }

    public Task<InstallDecision> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        return Task.FromResult(ConfirmDecision);
    }

    public Task<bool> ConfirmForceCloseAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        return Task.FromResult(ForceCloseDecision);
    }

    public void Report(string step, string? title = null) => Steps.Add(step);

    public Task CompleteAsync(InstallResult result)
    {
        Result = result;
        return Task.CompletedTask;
    }

    public Task FailAsync(InstallResult result)
    {
        Result = result;
        return Task.CompletedTask;
    }

    public Task CanceledAsync(InstallResult result)
    {
        Result = result;
        return Task.CompletedTask;
    }
}
