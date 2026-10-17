using System.Text;
using System.Text.Json;
using VRPC.Application;

namespace VRPC.Install
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
        public int Schema { get; set; } = 1;
        public string Version { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public string AppDir { get; set; } = "";
        public string DataDir { get; set; } = "";
        public string InstalledAtUtc { get; set; } = "";
        public string PreviousVersion { get; set; } = "";
        public string MigratedFrom { get; set; } = "";
        public string ArpKey { get; set; } = ArpRegistration.KeyPath;
        public List<string> ExtensionIds { get; set; } = new();
        public List<RegisteredManifest> Manifests { get; set; } = new();
        public List<RegisteredRegistryKey> RegistryKeys { get; set; } = new();
    }

    // Marker for a directory swap that may have been interrupted.
    public sealed class UpdateTransaction
    {
        public int Schema { get; set; } = 1;
        public string Phase { get; set; } = UpdatePhase.Staging;
        public string TargetVersion { get; set; } = "";
        public string PreviousVersion { get; set; } = "";
        public string StagingDir { get; set; } = "";
        public string BackupDir { get; set; } = "";
        public string StartedAtUtc { get; set; } = "";
    }

    public static class UpdatePhase
    {
        public const string Staging = "staging";
        public const string Committing = "committing";
        public const string Verifying = "verifying";
        public const string Done = "done";
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

        public static void Delete()
        {
            TryDelete(AppPaths.InstallStatePath);
            TryDelete(AppPaths.InstallStatePath + ".bak");
        }

        public static UpdateTransaction? LoadTransaction()
        {
            string path = AppPaths.UpdateMarkerPath;
            if (!File.Exists(path)) { return null; }

            try
            {
                return JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(path, Encoding.UTF8), InstallJson.Options);
            }
            catch
            {
                return null;
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
    }
}
