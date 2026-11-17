using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VRPC.Application;
using VRPC.Install;

namespace VRPC.Updates
{
    // Starts the extracted setup panel as a detached external process, without
    // leaking the Native Messaging handles, and preserves custom extension ids.
    public sealed class UpdateProcessLauncher : IUpdateProcessLauncher
    {
        public bool TryLaunch(string packageDirectory, string version, out string errorMessage)
        {
            ProcessStartInfo? startInfo = BuildStartInfo(packageDirectory, version, out string logPath, out errorMessage);
            if (startInfo is null) { return false; }

            OutputPump pump = new OutputPump(logPath);
            try
            {
                Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    pump.Dispose();
                    errorMessage = "The setup process could not be started.";
                    return false;
                }

                try { process.StandardInput.Close(); } catch { }
                pump.Start(process.StandardOutput, process.StandardError);
                return true;
            }
            catch (Exception exception)
            {
                pump.Dispose();
                errorMessage = "The setup process could not be started: " + exception.Message;
                return false;
            }
        }

        // Validates the extracted package and builds the external setup invocation.
        // Split out so the argument/environment contract is unit-testable without
        // starting a real installer process.
        internal static ProcessStartInfo? BuildStartInfo(string packageDirectory, string version, out string logPath, out string errorMessage)
        {
            logPath = "";
            errorMessage = "";
            try
            {
                if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
                {
                    errorMessage = "The extracted update package is missing.";
                    return null;
                }
                if (!AppPaths.IsUnderUpdatesRoot(packageDirectory))
                {
                    errorMessage = "The update package is outside the updater's folder.";
                    return null;
                }

                // The coordinator authenticated the signed envelope immediately
                // before this call; this method re-verifies the payload files.
                PayloadManifest manifest = PayloadManifest.Load(packageDirectory);
                if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
                {
                    errorMessage = $"The extracted package is version {manifest.Version}, expected {version}.";
                    return null;
                }

                IReadOnlyList<string> errors = manifest.Verify();
                if (errors.Count > 0)
                {
                    errorMessage = "The extracted package did not verify: " + string.Join("; ", errors.Take(3));
                    return null;
                }

                string executable = manifest.EntryPointPath;
                if (!File.Exists(executable))
                {
                    errorMessage = $"The setup executable was not found at {executable}.";
                    return null;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = packageDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("--update");
                startInfo.ArgumentList.Add("--payload");
                startInfo.ArgumentList.Add(packageDirectory);
                startInfo.ArgumentList.Add("--root");
                startInfo.ArgumentList.Add(AppPaths.Root);
                startInfo.ArgumentList.Add("--panel");
                AddRegistrationArguments(startInfo);

                Directory.CreateDirectory(AppPaths.Logs);
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                logPath = Path.Combine(AppPaths.Logs, $"update-launch-{timestamp}.log");

                // The child setup runs after this host exits, so its console
                // handles are about to become broken pipes. Point its console at
                // a file instead of letting a fallback path crash on IOException.
                startInfo.Environment["VRPC_SETUP_CONSOLE_LOG"] = Path.Combine(AppPaths.Logs, $"update-setup-console-{timestamp}.log");
                foreach (string name in new[] { "VRPC_UPDATE_LOCAL_FEED", "VRPC_UPDATER_INITIAL_DELAY_SECONDS", "VRPC_DISABLE_UPDATER", "VRPC_NO_REGISTRY" })
                {
                    startInfo.Environment.Remove(name);
                }

                return startInfo;
            }
            catch (Exception exception)
            {
                errorMessage = "The setup process could not be prepared: " + exception.Message;
                return null;
            }
        }

        private static void AddRegistrationArguments(ProcessStartInfo startInfo)
        {
            try
            {
                string path = AppPaths.InstallStatePath;
                if (!File.Exists(path)) { return; }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                JsonElement root = document.RootElement;
                AddIds(startInfo, root, "chromiumExtensionIds", "--extension-id");
                AddIds(startInfo, root, "firefoxExtensionIds", "--firefox-extension-id");
            }
            catch
            {
                // The installer derives the ids from the existing registration when
                // they are not passed explicitly; a read failure must not block the
                // update launch by itself.
            }
        }

        private static void AddIds(ProcessStartInfo startInfo, JsonElement root, string propertyName, string flag)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement element) || element.ValueKind != JsonValueKind.Array) { return; }
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? id = item.GetString();
                if (string.IsNullOrWhiteSpace(id)) { continue; }
                startInfo.ArgumentList.Add(flag);
                startInfo.ArgumentList.Add(id);
            }
        }

        // Drains the child's stdout/stderr into a launch log. The host exits
        // shortly after starting the panel, so a truncated tail here is expected;
        // the real setup log lives next to it under state/logs/setup-*.log.
        private sealed class OutputPump : IDisposable
        {
            private readonly object _gate = new object();
            private readonly StreamWriter _writer;

            public OutputPump(string path)
            {
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
            }

            public void Start(StreamReader stdout, StreamReader stderr)
            {
                _ = Task.Run(() => PumpAsync(stdout));
                _ = Task.Run(() => PumpAsync(stderr));
            }

            private async Task PumpAsync(StreamReader reader)
            {
                try
                {
                    while (true)
                    {
                        string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) { return; }
                        lock (_gate) { _writer.WriteLine(line); }
                    }
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                try { _writer.Dispose(); } catch { }
            }
        }
    }
}
