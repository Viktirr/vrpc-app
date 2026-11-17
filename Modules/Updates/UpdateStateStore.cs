using System.Text;
using System.Text.Json;
using VRPC.Application;

namespace VRPC.Updates
{
    // Atomic store for state/updater-state.json. Deliberately separate from the
    // installer's transaction marker (state/update.json).
    public sealed class UpdateStateStore
    {
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
            try
            {
                if (!File.Exists(_path)) { return new UpdaterState(); }
                UpdaterState? state = JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(_path, Encoding.UTF8), UpdateJson.Options);
                return state ?? new UpdaterState();
            }
            catch
            {
                TryPreserveCorrupt();
                return new UpdaterState();
            }
        }

        public void Save(UpdaterState state)
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

        public void Delete()
        {
            try { if (File.Exists(_path)) { File.Delete(_path); } } catch { }
            try { if (File.Exists(_path + ".bak")) { File.Delete(_path + ".bak"); } } catch { }
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
