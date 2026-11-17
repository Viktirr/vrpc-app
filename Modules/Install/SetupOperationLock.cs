using System.Diagnostics;
using VRPC.Application;

namespace VRPC.Install
{
    // Cross-process exclusive lock that serializes every setup mutation.
    // The path is root-scoped, so custom --root installations get their own lock.
    public sealed class SetupOperationLock : IDisposable
    {
        public const int DefaultTimeoutMilliseconds = 10000;
        private const string TimeoutVariable = "VRPC_SETUP_LOCK_TIMEOUT_MS";

        private FileStream? _stream;

        private SetupOperationLock(FileStream stream)
        {
            _stream = stream;
        }

        // Acquires the lock or throws exit code 7 ("another setup operation is in progress").
        public static SetupOperationLock Acquire(int? timeoutMilliseconds = null)
        {
            if (TryAcquire(timeoutMilliseconds ?? ResolveTimeout(), out SetupOperationLock? operationLock, out string error))
            {
                return operationLock!;
            }

            throw new InstallException(error, SetupExitCodes.AnotherOperationInProgress);
        }

        public static bool TryAcquire(int timeoutMilliseconds, out SetupOperationLock? operationLock, out string error)
        {
            operationLock = null;
            error = "";
            string path = AppPaths.SetupOperationLockPath;

            try { Directory.CreateDirectory(AppPaths.State); }
            catch (Exception e)
            {
                error = $"The setup state folder {AppPaths.State} could not be created: {e.Message}";
                return false;
            }

            if (timeoutMilliseconds < 0) { timeoutMilliseconds = 0; }
            Stopwatch stopwatch = Stopwatch.StartNew();
            int delay = 25;

            while (true)
            {
                try
                {
                    FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                    operationLock = new SetupOperationLock(stream);
                    return true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException e)
                {
                    error = $"The setup lock {path} could not be opened: {e.Message}";
                    return false;
                }

                int remaining = timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    error = "Another setup operation is in progress. Wait for it to finish and retry.";
                    return false;
                }

                Thread.Sleep(Math.Min(delay, remaining));
                delay = Math.Min(delay * 2, 250);
            }
        }

        public void Dispose()
        {
            FileStream? stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null) { return; }
            try { stream.Dispose(); } catch { }
        }

        private static int ResolveTimeout()
        {
            string? value = Environment.GetEnvironmentVariable(TimeoutVariable);
            if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out int parsed))
            {
                return Math.Clamp(parsed, 0, 60000);
            }

            return DefaultTimeoutMilliseconds;
        }
    }
}
