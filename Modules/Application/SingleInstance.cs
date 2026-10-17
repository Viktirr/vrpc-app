using System.Text;

namespace VRPC.Application
{
    // Single-instance lock via an exclusively opened pid file.
    public sealed class SingleInstance : IDisposable
    {
        private FileStream? _lockStream;

        public bool IsOwner { get; private set; }

        // Opening the pid file with FileShare.None is the lock.
        public static SingleInstance Acquire()
        {
            SingleInstance instance = new();
            try
            {
                Directory.CreateDirectory(AppPaths.State);
                instance._lockStream = new FileStream(AppPaths.PidPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                instance._lockStream.SetLength(0);
                byte[] pid = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
                instance._lockStream.Write(pid, 0, pid.Length);
                instance._lockStream.Flush(flushToDisk: true);
                instance.IsOwner = true;
            }
            catch (IOException)
            {
                instance._lockStream?.Dispose();
                instance._lockStream = null;
                instance.IsOwner = false;
            }
            catch (UnauthorizedAccessException)
            {
                instance.IsOwner = false;
            }

            return instance;
        }

        public void Dispose()
        {
            _lockStream?.Dispose();
            _lockStream = null;

            if (IsOwner)
            {
                try
                {
                    if (File.Exists(AppPaths.PidPath)) { File.Delete(AppPaths.PidPath); }
                }
                catch { }
            }

            IsOwner = false;
        }
    }
}
