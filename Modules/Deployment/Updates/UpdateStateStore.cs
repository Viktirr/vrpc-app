using System.Text;
using System.Text.Json;
using VRPC.Application;

namespace VRPC.Deployment.Updates
{
    // Atomic store for state/updater-state.json. Deliberately separate from the
    // installer's transaction marker (state/update.json).
    public sealed class UpdateStateStore
    {
        // The coordinator writes while tests/status readers load. Serializing the
        // small file operations in-process avoids intermittent sharing violations
        // that could otherwise look like a corrupt state file.
        private static readonly object StoreGate = new();
        private readonly string _path;

        public UpdateStateStore() : this(AppPaths.UpdaterStatePath)
        {
        }

        public UpdateStateStore(string path)
        {
            _path = path;
        }

        public string Path => _path;

        public UpdaterState Load()
        {
            lock (StoreGate)
            {
                try
                {
                    if (!File.Exists(_path)) { return new UpdaterState(); }

                    // A concurrent save replaces the file with File.Move; allowing
                    // delete sharing here prevents a reader from making the replace
                    // fail (or from treating a transient sharing violation as
                    // corruption).
                    string json;
                    using (FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new(stream, Encoding.UTF8))
                    {
                        json = reader.ReadToEnd();
                    }

                    UpdaterState? state = JsonSerializer.Deserialize<UpdaterState>(json, UpdateJson.Options);
                    return state ?? new UpdaterState();
                }
                catch
                {
                    TryPreserveCorrupt();
                    return new UpdaterState();
                }
            }
        }

        public void Save(UpdaterState state)
        {
            lock (StoreGate)
            {
                string? directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(state, UpdateJson.Options), new UTF8Encoding(false));

                if (File.Exists(_path))
                {
                    try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { }
                }

                File.Move(temporary, _path, overwrite: true);
            }
        }

        public void Delete()
        {
            lock (StoreGate)
            {
                try { if (File.Exists(_path)) { File.Delete(_path); } } catch { }
                try { if (File.Exists(_path + ".bak")) { File.Delete(_path + ".bak"); } } catch { }
            }
        }

        private void TryPreserveCorrupt()
        {
            try
            {
                if (!File.Exists(_path)) { return; }
                string backup = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(_path, backup, overwrite: true);
            }
            catch { }
        }
    }
}
