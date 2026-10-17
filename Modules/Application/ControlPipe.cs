using System.IO.Pipes;
using System.Text;
using VRPC.Logging;

namespace VRPC.Application
{
    // IPC communication between installer and host. Used for shutting down the app between updates.
    public sealed class ControlServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly Action _onShutdown;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Log _log = new();

        private ControlServer(string pipeName, Action onShutdown)
        {
            _pipeName = pipeName;
            _onShutdown = onShutdown;
        }

        public static ControlServer? Start(Action onShutdown)
        {
            if (!OperatingSystem.IsWindows()) { return null; }
            ControlServer server = new(AppPaths.ControlPipeNameForCurrentUser(), onShutdown);
            _ = Task.Run(server.RunAsync);
            return server;
        }

        private async Task RunAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
#pragma warning disable CA1416
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#pragma warning restore CA1416
                }
                catch (Exception e)
                {
                    _log.Error("[Control] Could not create the control pipe: " + e.Message);
                    return;
                }

                await using (pipe)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);

                        using StreamReader reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                        using StreamWriter writer = new(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

                        string? command = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                        if (string.Equals(command, "PING", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("PONG").ConfigureAwait(false);
                        }
                        else if (string.Equals(command, "VERSION", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync(ProductInfo.Version).ConfigureAwait(false);
                        }
                        else if (string.Equals(command, "SHUTDOWN", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("OK").ConfigureAwait(false);
                            await writer.FlushAsync(_cancellation.Token).ConfigureAwait(false);
                            _log.Write("[Control] Shutdown requested through the control pipe.");
                            _onShutdown();
                            return;
                        }
                        else
                        {
                            await writer.WriteLineAsync("ERR").ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                    }
                    catch (Exception e)
                    {
                        _log.Warn($"[Control] Pipe request failed: {e.Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            try { _cancellation.Cancel(); } catch { }
        }
    }

    // Setup-side client for the control pipe.
    public static class ControlClient
    {
        public static bool TryShutdown(TimeSpan? timeout = null)
        {
            return TryQuery("SHUTDOWN", out _, out _, timeout);
        }

        public static bool TryPing(TimeSpan? timeout = null)
        {
            return TryQuery("PING", out string response, out _, timeout) &&
                   string.Equals(response, "PONG", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGetVersion(out string version, TimeSpan? timeout = null)
        {
            return TryQuery("VERSION", out version, out _, timeout);
        }

        public static bool TryQuery(string command, out string response, out string error, TimeSpan? timeout = null)
        {
            response = "";
            error = "";
            if (!OperatingSystem.IsWindows())
            {
                error = "The control pipe is only implemented on Windows.";
                return false;
            }

            TimeSpan wait = timeout ?? TimeSpan.FromSeconds(2);
            try
            {
                using NamedPipeClientStream pipe = new(".", AppPaths.ControlPipeNameForCurrentUser(), PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect((int)wait.TotalMilliseconds);

                using StreamWriter writer = new(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                using StreamReader reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                writer.WriteLine(command);
                writer.Flush();

                using CancellationTokenSource timeoutSource = new(wait);
                string? line = reader.ReadLineAsync(timeoutSource.Token).AsTask().GetAwaiter().GetResult();
                if (line is null)
                {
                    error = "The application closed the control pipe without answering.";
                    return false;
                }

                response = line;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
