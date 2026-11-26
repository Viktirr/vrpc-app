using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRPC.Application;

namespace VRPC.Deployment.Install
{
    // Runtime record of what was installed and registered.
    public sealed class RegisteredManifest
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public sealed class RegisteredRegistryKey
    {
        public string Hive { get; set; } = "HKCU";
        public string Path { get; set; } = "";
        public string ValueName { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public sealed class InstallState
    {
        public const int CurrentSchema = 2;

        public int Schema { get; set; } = CurrentSchema;
        public string Version { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public string AppDir { get; set; } = "";
        public string DataDir { get; set; } = "";
        public string InstalledAtUtc { get; set; } = "";
        public string PreviousVersion { get; set; } = "";
        public string MigratedFrom { get; set; } = "";
        public string ArpKey { get; set; } = ArpRegistration.KeyPath;

        // Schema 1 field, only read; migrated into the two family lists below.
        public List<string>? ExtensionIds { get; set; }
        public List<string> ChromiumExtensionIds { get; set; } = new();
        public List<string> FirefoxExtensionIds { get; set; } = new();
        public List<RegisteredManifest> Manifests { get; set; } = new();
        public List<RegisteredRegistryKey> RegistryKeys { get; set; } = new();

        // Set by migration when a schema 1 file cannot be split into families; not persisted.
        [JsonIgnore]
        public bool RegistrationMigrationFailed { get; set; }
    }

    // Converts a schema 1 install record into the family-split schema 2 record.
    public static class InstallStateMigration
    {
        public static void Migrate(InstallState state)
        {
            state.ChromiumExtensionIds ??= new();
            state.FirefoxExtensionIds ??= new();
            state.Manifests ??= new();
            state.RegistryKeys ??= new();
            if (state.Schema >= InstallState.CurrentSchema)
            {
                state.ExtensionIds = null;
                return;
            }

            List<string> chromium = new();
            List<string> firefox = new();
            bool failed = false;

            foreach (RegisteredManifest manifest in state.Manifests)
            {
                if (!TryReadManifestIds(manifest, chromium, firefox)) { failed = true; }
            }

            int legacyCount = state.ExtensionIds?.Count(id => !string.IsNullOrWhiteSpace(id)) ?? 0;
            if (state.Manifests.Count == 0 && legacyCount > 0) { failed = true; }

            state.Schema = InstallState.CurrentSchema;
            state.ChromiumExtensionIds = Distinct(chromium);
            state.FirefoxExtensionIds = Distinct(firefox);
            state.ExtensionIds = null;
            state.RegistrationMigrationFailed = failed;
        }

        private static bool TryReadManifestIds(RegisteredManifest manifest, List<string> chromium, List<string> firefox)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(manifest.Path) || !File.Exists(manifest.Path)) { return false; }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest.Path, Encoding.UTF8));
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { return false; }

                bool found = false;
                if (root.TryGetProperty("allowed_extensions", out JsonElement extensions) && extensions.ValueKind == JsonValueKind.Array)
                {
                    found = true;
                    foreach (JsonElement item in extensions.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) { continue; }
                        string? id = item.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) { firefox.Add(id.Trim()); }
                    }
                }

                if (root.TryGetProperty("allowed_origins", out JsonElement origins) && origins.ValueKind == JsonValueKind.Array)
                {
                    found = true;
                    foreach (JsonElement item in origins.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) { continue; }
                        string? origin = item.GetString()?.Trim();
                        if (string.IsNullOrWhiteSpace(origin)) { continue; }

                        string id = origin;
                        const string prefix = "chrome-extension://";
                        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { id = id.Substring(prefix.Length); }
                        id = id.TrimEnd('/');
                        if (id.Length > 0) { chromium.Add(id); }
                    }
                }

                return found;
            }
            catch
            {
                return false;
            }
        }

        private static List<string> Distinct(List<string> ids)
        {
            return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // Marker for a directory swap that may have been interrupted.
    public sealed class UpdateTransaction
    {
        public const int CurrentSchema = 3;

        // Old markers always carried schema 1 explicitly; defaulting to 1 treats a
        // missing field conservatively.
        public int Schema { get; set; } = 1;
        public string Phase { get; set; } = UpdatePhase.Staging;
        public string TargetVersion { get; set; } = "";
        public string PreviousVersion { get; set; } = "";
        public string StagingDir { get; set; } = "";
        public string BackupDir { get; set; } = "";
        public bool HadAppBefore { get; set; }
        public bool RegistrationRemoved { get; set; }
        public string StartedAtUtc { get; set; } = "";

        // Schema 3: the complete install-state record from before the swap, so
        // recovery can restore state even when the on-disk record was already
        // replaced with the target version.
        public InstallState? PreviousInstallState { get; set; }
        public List<string> TargetChromiumExtensionIds { get; set; } = new();
        public List<string> TargetFirefoxExtensionIds { get; set; } = new();
        public List<string> PreviousChromiumExtensionIds { get; set; } = new();
        public List<string> PreviousFirefoxExtensionIds { get; set; } = new();

        [JsonIgnore]
        public bool IsLegacySchema => Schema < CurrentSchema;
    }

    public static class UpdatePhase
    {
        public const string Staging = "staging";
        public const string Staged = "staged";
        public const string StoppingHost = "stopping-host";
        public const string RegistrationRemoved = "registration-removed";
        public const string Committing = "committing";
        public const string Committed = "committed";
        public const string Verifying = "verifying";
        public const string Registering = "registering";
        public const string Done = "done";

        private static readonly string[] Order =
        {
            Staging, Staged, StoppingHost, RegistrationRemoved,
            Committing, Committed, Verifying, Registering, Done
        };

        public static int Rank(string? phase)
        {
            if (phase is null) { return -1; }
            for (int i = 0; i < Order.Length; i++)
            {
                if (string.Equals(Order[i], phase, StringComparison.Ordinal)) { return i; }
            }
            return -1;
        }

        public static bool IsAtLeast(string? phase, string expected)
        {
            int rank = Rank(phase);
            int threshold = Rank(expected);
            return rank >= 0 && threshold >= 0 && rank >= threshold;
        }
    }

    public sealed class TransactionLoadResult
    {
        public UpdateTransaction? Transaction { get; init; }
        public bool Corrupt { get; init; }
        public string CorruptPath { get; init; } = "";
    }

    // Atomic state writes; corrupt state is moved aside.
    public static class InstallStateStore
    {
        public static InstallState? Load()
        {
            string path = AppPaths.InstallStatePath;
            if (!File.Exists(path)) { return null; }

            try
            {
                InstallState? state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path, Encoding.UTF8), InstallJson.Options);
                if (state is null) { return null; }
                InstallStateMigration.Migrate(state);
                return state;
            }
            catch
            {
                TryPreserveCorrupt(path);
                return null;
            }
        }

        public static void Save(InstallState state)
        {
            Directory.CreateDirectory(AppPaths.State);
            string path = AppPaths.InstallStatePath;
            string temporary = path + ".tmp";
            string json = JsonSerializer.Serialize(state, InstallJson.Options);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
            }

            File.Move(temporary, path, overwrite: true);
        }

        public static bool TrySave(InstallState state, out string error)
        {
            try
            {
                Save(state);
                error = "";
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public static bool TryDelete(out string error)
        {
            try
            {
                TryDeleteFile(AppPaths.InstallStatePath);
                TryDeleteFile(AppPaths.InstallStatePath + ".bak");
                error = "";
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        // Deep copy used by the transaction journal so a later mutation of the
        // live state object cannot alter the recorded previous state.
        public static InstallState Clone(InstallState state)
        {
            InstallState? clone = JsonSerializer.Deserialize<InstallState>(
                JsonSerializer.Serialize(state, InstallJson.Options), InstallJson.Options);
            if (clone is null) { throw new InvalidOperationException("The install state could not be copied."); }
            InstallStateMigration.Migrate(clone);
            clone.RegistrationMigrationFailed = state.RegistrationMigrationFailed;
            return clone;
        }

        public static void Delete()
        {
            TryDelete(AppPaths.InstallStatePath);
            TryDelete(AppPaths.InstallStatePath + ".bak");
        }

        public static UpdateTransaction? LoadTransaction()
        {
            return LoadTransactionResult().Transaction;
        }

        // Reads the marker and, when it cannot be parsed, moves it aside so a
        // corrupt file cannot be silently ignored on the next run.
        public static TransactionLoadResult LoadTransactionResult()
        {
            string path = AppPaths.UpdateMarkerPath;
            if (!File.Exists(path)) { return new TransactionLoadResult(); }

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return new TransactionLoadResult { Corrupt = true, CorruptPath = path };
            }

            try
            {
                UpdateTransaction? transaction = JsonSerializer.Deserialize<UpdateTransaction>(text, InstallJson.Options);
                if (transaction is null) { throw new JsonException("the update marker is empty"); }
                return new TransactionLoadResult { Transaction = transaction };
            }
            catch
            {
                string preserved = TryPreserveMarker(path);
                return new TransactionLoadResult { Corrupt = true, CorruptPath = preserved.Length > 0 ? preserved : path };
            }
        }

        public static void SaveTransaction(UpdateTransaction transaction)
        {
            Directory.CreateDirectory(AppPaths.State);
            string path = AppPaths.UpdateMarkerPath;
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(transaction, InstallJson.Options), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }

        public static void DeleteTransaction()
        {
            TryDelete(AppPaths.UpdateMarkerPath);
        }

        private static string TryPreserveMarker(string path)
        {
            try
            {
                string preserved = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(path, preserved, overwrite: true);
                return preserved;
            }
            catch
            {
                return "";
            }
        }

        private static void TryPreserveCorrupt(string path)
        {
            try
            {
                string backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(path, backup, overwrite: true);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
        }

        private static void TryDeleteFile(string path)
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }
}
