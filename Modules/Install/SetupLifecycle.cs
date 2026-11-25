using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VRPC.Application;

namespace VRPC.Install
{
    // Durable ownership record shared by the host and setup. A live lease keeps
    // the host from starting while a setup prompt, swap or recovery is running;
    // an abandoned lease is detected by its process id and never blocks forever.
    public sealed class SetupLifecycleLease : IDisposable
    {
        private readonly object _gate = new();
        private string? _token;

        internal SetupLifecycleLease(string token)
        {
            _token = token;
        }

        internal string Token => _token ?? "";

        public void Refresh()
        {
            string? token = _token;
            if (token is null) { return; }
            SetupLifecycle.TryWriteLease(token, Environment.ProcessId, "setup");
        }

        // The child setup took the lease over; the parent must not delete it.
        internal void Release()
        {
            lock (_gate) { _token = null; }
        }

        public void Dispose()
        {
            string? token;
            lock (_gate)
            {
                token = _token;
                _token = null;
            }
            if (token is not null) { SetupLifecycle.TryDeleteLease(token, Environment.ProcessId); }
        }
    }

    internal sealed class SetupLeaseRecord
    {
        public int Schema { get; set; } = 1;
        public string Token { get; set; } = "";
        public int ProcessId { get; set; }
        public string ProcessStartedUtc { get; set; } = "";
        public string Reason { get; set; } = "";
        public string StartedAtUtc { get; set; } = "";
        public string ExpiresAtUtc { get; set; } = "";
    }

    // Cross-process lifecycle guard. Setup holds a lease for its whole lifetime;
    // the host refuses to start while the lease is live or while a transaction
    // marker records an unsafe phase. Stale leases (dead process) and pre-swap
    // markers never keep VRPC from starting.
    public static class SetupLifecycle
    {
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromHours(12);
        private static readonly object FileGate = new();

        private static readonly string[] UnsafePhases =
        {
            UpdatePhase.StoppingHost,
            UpdatePhase.RegistrationRemoved,
            UpdatePhase.Committing,
            UpdatePhase.Committed,
            UpdatePhase.Verifying,
            UpdatePhase.Registering
        };

        // Starts a lease owned by this process. The updater uses it before it
        // launches setup and hands the token to the child, which takes it over.
        public static SetupLifecycleLease Begin(string reason, string? token = null)
        {
            token = string.IsNullOrWhiteSpace(token) ? Guid.NewGuid().ToString("N") : token.Trim();
            TryWriteLease(token, Environment.ProcessId, reason);
            return new SetupLifecycleLease(token);
        }

        // The updater's handoff claim. Unlike Begin (used by setup after it holds
        // the setup-operation lock), this must never overwrite a lease owned by a
        // live setup process.
        public static SetupLifecycleLease? TryBeginHandoff(string reason, out string error)
        {
            error = "";
            lock (FileGate)
            {
                if (TryReadLease(out SetupLeaseRecord existing) &&
                    existing.ProcessId != Environment.ProcessId &&
                    IsActive(existing))
                {
                    error = "Another setup operation is already in progress.";
                    return null;
                }

                string token = Guid.NewGuid().ToString("N");
                if (!TryWriteLease(token, Environment.ProcessId, reason))
                {
                    error = "The setup lease could not be written.";
                    return null;
                }
                return new SetupLifecycleLease(token);
            }
        }

        // True while a setup process owns the installation. The updater uses it to
        // suppress a second handoff; a dead setup never counts as active.
        public static bool IsSetupActive()
        {
            return TryReadLease(out SetupLeaseRecord lease) && IsActive(lease);
        }

        // Host-startup gate. Returns true when starting the normal host would race
        // an active setup or an unsafe half-applied transaction.
        public static bool BlocksHostStartup(out string reason)
        {
            reason = "";
            if (TryReadLease(out SetupLeaseRecord lease) && IsActive(lease))
            {
                reason = $"VRPC setup is running ({lease.Reason}). Wait for it to finish before starting the app.";
                return true;
            }

            if (!File.Exists(AppPaths.UpdateMarkerPath)) { return false; }
            if (!TryReadMarkerPhase(out string phase))
            {
                reason = "An interrupted setup left an unreadable update marker. Run VRPC setup again to recover the installation.";
                return true;
            }

            if (Array.IndexOf(UnsafePhases, phase) >= 0)
            {
                reason = $"An interrupted setup was found in the '{phase}' phase. Run VRPC setup again to recover the installation.";
                return true;
            }

            return false;
        }

        internal static bool TryWriteLease(string token, int processId, string reason, DateTime? startedUtc = null)
        {
            try
            {
                string? directory = Path.GetDirectoryName(AppPaths.SetupLeasePath);
                if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

                DateTime started = startedUtc ?? ProcessStartUtc(processId);
                DateTime now = DateTime.UtcNow;
                SetupLeaseRecord lease = new()
                {
                    Schema = 1,
                    Token = token,
                    ProcessId = processId,
                    ProcessStartedUtc = started.ToString("O"),
                    Reason = reason,
                    StartedAtUtc = now.ToString("O"),
                    ExpiresAtUtc = now.Add(LeaseDuration).ToString("O")
                };

                lock (FileGate)
                {
                    string path = AppPaths.SetupLeasePath;
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(lease, InstallJson.Options), new UTF8Encoding(false));
                    File.Move(temporary, path, overwrite: true);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void TryDeleteLease(string token, int processId)
        {
            try
            {
                lock (FileGate)
                {
                    if (!TryReadLease(out SetupLeaseRecord lease)) { return; }
                    if (!string.Equals(lease.Token, token, StringComparison.Ordinal)) { return; }
                    if (lease.ProcessId != processId) { return; }
                    File.Delete(AppPaths.SetupLeasePath);
                }
            }
            catch
            {
            }
        }

        internal static bool TryReadLease(out SetupLeaseRecord lease)
        {
            lease = new SetupLeaseRecord();
            try
            {
                string path = AppPaths.SetupLeasePath;
                if (!File.Exists(path)) { return false; }
                SetupLeaseRecord? parsed = JsonSerializer.Deserialize<SetupLeaseRecord>(File.ReadAllText(path, Encoding.UTF8), InstallJson.Options);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Token)) { return false; }
                lease = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsActive(SetupLeaseRecord lease)
        {
            if (IsLive(lease)) { return true; }

            // The updater releases the lease to the not-yet-started child with the
            // old host's pid. Keep that handoff window covered until the child
            // writes its own pid; a setup that never starts expires quickly.
            if (string.Equals(lease.Reason, "update handoff", StringComparison.Ordinal) &&
                DateTime.TryParse(lease.StartedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime started) &&
                DateTime.UtcNow - started.ToUniversalTime() < TimeSpan.FromMinutes(1))
            {
                return true;
            }

            return false;
        }

        private static bool IsLive(SetupLeaseRecord lease)
        {
            if (lease.ProcessId <= 0) { return false; }
            if (lease.ProcessId == Environment.ProcessId) { return true; }

            try
            {
                using Process process = Process.GetProcessById(lease.ProcessId);
                if (process.HasExited) { return false; }

                if (DateTime.TryParse(lease.ProcessStartedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime expected))
                {
                    DateTime actual = process.StartTime.ToUniversalTime();
                    return Math.Abs((actual - expected.ToUniversalTime()).TotalSeconds) < 5;
                }

                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch
            {
                // Access denied: trust the recorded expiry rather than blocking on
                // a process this account cannot inspect.
                return DateTime.TryParse(lease.ExpiresAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime expires)
                    && DateTime.UtcNow < expires.ToUniversalTime();
            }
        }

        private static DateTime ProcessStartUtc(int processId)
        {
            if (processId != Environment.ProcessId) { return DateTime.UtcNow; }
            try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch { return DateTime.UtcNow; }
        }

        private static bool TryReadMarkerPhase(out string phase)
        {
            phase = "";
            try
            {
                UpdateTransaction? transaction = JsonSerializer.Deserialize<UpdateTransaction>(
                    File.ReadAllText(AppPaths.UpdateMarkerPath, Encoding.UTF8), InstallJson.Options);
                if (transaction is null) { return false; }
                phase = transaction.Phase;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
