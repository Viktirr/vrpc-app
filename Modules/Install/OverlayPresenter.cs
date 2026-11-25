using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VRPC.Install
{
    // Spawns and supervises the installer panel (installer-ui, a separate Rust
    // process driven over stdin/stdout).
    //
    // The panel is untrusted: a decision line is the only evidence of a user's
    // choice, the exit code alone is never trusted, and every failure path lands
    // on a working console presenter instead of throwing into the install flow.
    public sealed class OverlayPresenter : IPresenter, IDisposable
    {
        public const int ProtocolVersion = 2;
        public const string ExeName = "vrpc-installer-ui.exe";

        public const int DefaultReadyTimeoutMs = 5000;
        // The panel heartbeats every 2 s. Six seconds is three missed ticks: long
        // enough that a busy machine cannot trip it, short enough that a dead
        // renderer costs the user seconds instead of the whole install.
        public const int DefaultStallTimeoutMs = 6000;
        public const int DefaultDismissCeilingMs = 10 * 60 * 1000;

        private const int ProbeTimeoutMs = 5000;
        private const uint SpiGetScreenReader = 0x0046;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

        public static string ResolveExePath()
        {
            return Path.Combine(AppContext.BaseDirectory, ExeName);
        }

        // Null when this machine cannot show the panel at all. The caller keeps
        // the console presenter and nothing is ever spawned.
        public static async Task<OverlayPresenter?> TryCreateAsync(
            string? exePath = null,
            IPresenter? fallback = null,
            int readyTimeoutMs = DefaultReadyTimeoutMs,
            int stallTimeoutMs = DefaultStallTimeoutMs,
            int dismissCeilingMs = DefaultDismissCeilingMs,
            CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows()) { return null; }

            string path = string.IsNullOrWhiteSpace(exePath) ? ResolveExePath() : exePath;
            if (!File.Exists(path)) { return null; }

            if (ScreenReaderRunning()) { return null; }

            string? probeError = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            if (probeError is not null)
            {
                Console.WriteLine($"[ui] the panel cannot render on this machine, using the console installer: {probeError}");
                return null;
            }

            return new OverlayPresenter(path, fallback ?? new ConsolePresenter(false, false), readyTimeoutMs, stallTimeoutMs, dismissCeilingMs);
        }

        private readonly string _exePath;
        private readonly IPresenter _fallback;
        private readonly int _readyTimeoutMs;
        private readonly int _stallTimeoutMs;
        private readonly int _dismissCeilingMs;

        private readonly object _stdinGate = new();
        private readonly object _logGate = new();
        private readonly Queue<string> _stderrTail = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _lastLineMs;
        private int _ticksSeen;
        private int _stepsReported;
        private int _stepTotal;
        private string _progressTitle = "Working";

        private Process? _process;
        private StreamWriter? _stdin;
        private Channel<OverlayMessage>? _channel;
        private ChannelReader<OverlayMessage>? _reader;
        private Task? _stdoutPump;
        private Task? _stderrPump;

        private volatile bool _sessionStarted;
        private volatile bool _usable;
        private volatile bool _failed;
        private volatile bool _decided;
        private volatile string _reason = "the panel was not attempted";
        private int? _childExitCode;

        private OverlayPresenter(string exePath, IPresenter fallback, int readyTimeoutMs, int stallTimeoutMs, int dismissCeilingMs)
        {
            _exePath = exePath;
            _fallback = fallback;
            _readyTimeoutMs = readyTimeoutMs;
            _stallTimeoutMs = Math.Max(0, stallTimeoutMs);
            _dismissCeilingMs = Math.Max(0, dismissCeilingMs);
        }

        public Action<string>? Trace { get; set; }

        public string Reason => _reason;

        public int TicksSeen => Volatile.Read(ref _ticksSeen);

        public int? ChildExitCode => _childExitCode;

        public bool OverlayServedConsent => _usable && _decided;

        public string Describe()
        {
            if (_failed) { return $"console fallback ({_reason})"; }
            if (_decided) { return "the panel, end to end (hello -> ready -> decision, steps over stdin)"; }
            return "the panel was never used";
        }

        public async Task<InstallDecision> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
        {
            if (_decided) { throw new InvalidOperationException("Consent is asked once per session."); }

            _stepTotal = prompt.TotalSteps;
            _progressTitle = prompt.ProgressTitle;

            try
            {
                await StartAsync(cancellationToken).ConfigureAwait(false);
                SendConsent(prompt);
                InstallDecision decision = await WaitForDecisionAsync(cancellationToken).ConfigureAwait(false);
                _decided = true;
                Trace?.Invoke($"[overlay] decision: {decision}");
                return decision;
            }
            catch (Exception e)
            {
                return await FallBackConsentAsync(e, prompt, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<bool> ConfirmForceCloseAsync(ConfirmPrompt prompt, CancellationToken cancellationToken = default)
        {
            if (!CanPresent()) { return await _fallback.ConfirmForceCloseAsync(prompt, cancellationToken).ConfigureAwait(false); }

            try
            {
                Send("question", BuildPrompt(prompt));
                return await WaitForAnswerAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                string reason = Explain(e);
                MarkUnusable(reason);
                Kill();
                Console.WriteLine();
                Console.WriteLine($"[ui] the panel stopped responding, asking on the console instead: {reason}");
                return await _fallback.ConfirmForceCloseAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Report(string step, string? title = null)
        {
            if (title is not null) { _progressTitle = title; }

            if (!CanPresent())
            {
                _fallback.Report(step, title);
                return;
            }

            _stepsReported++;
            JsonObject message = new()
            {
                ["event"] = "progress",
                ["title"] = _progressTitle,
                ["text"] = step,
                ["done"] = _stepsReported,
                ["total"] = _stepTotal
            };
            Send(message);
        }

        public async Task CompleteAsync(InstallResult result)
        {
            if (!CanPresent())
            {
                await _fallback.CompleteAsync(result).ConfigureAwait(false);
                return;
            }

            string title = string.IsNullOrEmpty(result.Title) ? "VRPC setup is complete" : result.Title;
            await SendTerminalAsync("finish", title, Compose(result)).ConfigureAwait(false);
        }

        public async Task FailAsync(InstallResult result)
        {
            if (!CanPresent())
            {
                await _fallback.FailAsync(result).ConfigureAwait(false);
                return;
            }

            string title = string.IsNullOrEmpty(result.Title) ? "VRPC setup failed" : result.Title;
            await SendTerminalAsync("fail", title, Compose(result)).ConfigureAwait(false);
        }

        // A canceled panel has already closed itself by the time the decision
        // reaches the engine, so there is nothing to draw. When the console
        // served the prompt instead, the console owns this ending too.
        public async Task CanceledAsync(InstallResult result)
        {
            if (!_decided)
            {
                await _fallback.CanceledAsync(result).ConfigureAwait(false);
                return;
            }

            Kill();
        }

        private static string Compose(InstallResult result)
        {
            StringBuilder text = new(result.Message);
            if (!string.IsNullOrEmpty(result.Version)) { text.Append("\n\nVersion: ").Append(result.Version); }
            if (!string.IsNullOrEmpty(result.LogPath)) { text.Append("\nLog: ").Append(result.LogPath); }
            return text.ToString();
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_sessionStarted) { return; }
            _sessionStarted = true;

            Trace?.Invoke($"[overlay] exe: {_exePath}");
            Trace?.Invoke($"[overlay] ready timeout: {_readyTimeoutMs} ms, stall timeout: {(_stallTimeoutMs == 0 ? "off" : _stallTimeoutMs + " ms")}");

            Process process = new() { StartInfo = BuildStartInfo(_exePath), EnableRaisingEvents = true };
            if (!process.Start()) { throw new InvalidOperationException("the panel process did not start"); }

            _process = process;
            Trace?.Invoke($"[overlay] child pid {process.Id}");

            _channel = Channel.CreateUnbounded<OverlayMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            _reader = _channel.Reader;
            _stdin = process.StandardInput;
            _stdoutPump = Task.Run(() => PumpStdoutAsync(process, _channel.Writer));
            _stderrPump = Task.Run(() => PumpStderrAsync(process));

            await HandshakeAsync(_reader, cancellationToken).ConfigureAwait(false);

            _usable = true;
            _reason = "n/a";
            _ = Task.Run(() => WatchdogAsync(process));
            Trace?.Invoke("[overlay] 'ready' received, the panel owns the prompt");
        }

        // Cheap preflight used by the updater before it hands off. The update
        // setup process is spawned with redirected (closed) stdin, so a console
        // fallback can never ask the user anything. If no panel can be shown, the
        // handoff must fail before the old host is asked to exit.
        internal static async Task<(bool Available, string Reason)> CheckAvailabilityAsync(
            string exePath,
            CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows()) { return (false, "the setup panel is only available on Windows"); }
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return (false, $"the update package does not contain {ExeName}");
            }
            if (ScreenReaderRunning())
            {
                return (false, "the setup panel is disabled while a screen reader is running, and the updater cannot show an accessible console prompt");
            }

            string? probeError = await ProbeAsync(exePath, cancellationToken).ConfigureAwait(false);
            if (probeError is not null)
            {
                return (false, "the setup panel cannot render on this machine: " + probeError);
            }

            return (true, "");
        }

        internal static ProcessStartInfo BuildStartInfo(string exePath)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // A UTF-8 BOM on the first line silently kills the handshake.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false)
            };

            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) { startInfo.WorkingDirectory = directory; }
            }
            catch
            {
                // a bogus path is surfaced by Process.Start throwing
            }

            AutomationEnvironment.Scrub(startInfo);
            return startInfo;
        }

        private void SendConsent(ConfirmPrompt prompt)
        {
            Send("consent", BuildPrompt(prompt));
        }

        private static JsonObject BuildPrompt(ConfirmPrompt prompt)
        {
            JsonObject main = new()
            {
                ["title"] = prompt.Title,
                ["subtitle"] = prompt.Subtitle,
                ["text"] = prompt.Text,
                ["close"] = prompt.CloseLabel,
                ["primary"] = new JsonObject
                {
                    ["label"] = prompt.PrimaryLabel,
                    ["style"] = prompt.PrimaryStyle
                }
            };

            if (!string.IsNullOrWhiteSpace(prompt.SecondaryLabel))
            {
                main["secondary"] = new JsonObject
                {
                    ["label"] = prompt.SecondaryLabel,
                    ["style"] = prompt.SecondaryStyle
                };
            }

            return main;
        }

        private void Send(string overlayEvent, JsonObject payload)
        {
            payload["event"] = overlayEvent;
            Send(payload);
        }

        private void Send(JsonObject payload)
        {
            if (!CanPresent())
            {
                _fallback.Report(payload["text"]?.ToString() ?? "");
                return;
            }

            string line = payload.ToJsonString();

            try
            {
                lock (_stdinGate)
                {
                    _stdin!.Write(line);
                    _stdin.Write('\n');
                    _stdin.Flush();
                }

                Trace?.Invoke($"-> {line}");
            }
            catch (Exception e)
            {
                MarkUnusable("writing to the panel failed: " + Explain(e));
                Kill();
            }
        }

        private async Task SendTerminalAsync(string overlayEvent, string title, string text)
        {
            JsonObject payload = new()
            {
                ["title"] = title,
                ["text"] = text
            };
            Send(overlayEvent, payload);
            await WaitForDismissAsync().ConfigureAwait(false);
        }

        // After finish/fail the panel shows a Dismiss button, and the user is
        // reading it. A fixed deadline here killed the window under them, so the
        // wait is bounded by the heartbeat (a wedged panel stops ticking) and by
        // a generous ceiling, never by how fast a human reads.
        private async Task WaitForDismissAsync()
        {
            try
            {
                lock (_stdinGate)
                {
                    _stdin?.Flush();
                    _stdin?.Close();
                }
            }
            catch
            {
                // the child is already gone
            }

            Process? process = _process;
            if (process is null) { return; }

            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    if (process.HasExited)
                    {
                        RecordExitCode();
                        Trace?.Invoke($"[overlay] the panel was dismissed (exit code {_childExitCode})");
                        break;
                    }
                }
                catch
                {
                    break;
                }

                if (IsStalled())
                {
                    Trace?.Invoke("[overlay] the panel stopped heartbeating on a terminal screen, killing it");
                    Kill();
                    break;
                }

                if (elapsed.ElapsedMilliseconds >= _dismissCeilingMs)
                {
                    Trace?.Invoke($"[overlay] the panel was left open for {_dismissCeilingMs} ms, killing it");
                    Kill();
                    break;
                }

                await Task.Delay(200).ConfigureAwait(false);
            }

            await DrainPumpsAsync(500).ConfigureAwait(false);
        }

        private async Task HandshakeAsync(ChannelReader<OverlayMessage> reader, CancellationToken cancellationToken)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            bool helloSeen = false;

            while (true)
            {
                int remaining = _readyTimeoutMs - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) { throw new TimeoutException($"no 'ready' within {_readyTimeoutMs} ms"); }

                OverlayMessage? received = await ReadAsync(reader, TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
                if (received is null) { throw new TimeoutException($"no 'ready' within {_readyTimeoutMs} ms"); }

                OverlayMessage message = received.Value;
                if (message.Kind == OverlayMessageKind.Eof)
                {
                    throw new IOException($"the panel exited during start-up, before 'ready'{ExitCodeSuffix()}");
                }

                switch (message.Event)
                {
                    case "hello":
                        if (helloSeen) { break; }
                        if (message.Proto != ProtocolVersion)
                        {
                            throw new IOException($"protocol mismatch: the panel speaks {message.Proto}, VRPC speaks {ProtocolVersion}");
                        }
                        helloSeen = true;
                        break;

                    case "ready":
                        if (!helloSeen) { throw new IOException("'ready' arrived before 'hello'"); }
                        return;

                    case "tick":
                        Trace?.Invoke("<- 'tick' before 'ready' ignored");
                        break;

                    case "error":
                        throw new IOException($"the panel reported an error: {message.Message ?? "(no message)"}");

                    case "decision":
                        throw new IOException($"a decision arrived before 'ready' ('{message.Value}'); no UI was ever usable");

                    default:
                        Trace?.Invoke($"<- unknown event '{message.Event}' before 'ready', ignored");
                        break;
                }
            }
        }

        private async Task<InstallDecision> WaitForDecisionAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                OverlayMessage? received = await ReadAsync(_reader!, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);

                if (received is null)
                {
                    if (IsStalled())
                    {
                        throw new TimeoutException($"the panel stalled: no output for {_stallTimeoutMs} ms after 'ready', so it is alive but not responding");
                    }

                    if (IsChildExited()) { throw new IOException($"the panel exited without a decision{ExitCodeSuffix()}"); }
                    continue;
                }

                OverlayMessage message = received.Value;
                if (message.Kind == OverlayMessageKind.Eof)
                {
                    throw new IOException($"the panel closed its output without a decision{ExitCodeSuffix()}");
                }

                switch (message.Event)
                {
                    case "decision":
                    {
                        string value = (message.Value ?? "").Trim().ToLowerInvariant();
                        InstallDecision? decision = value switch
                        {
                            "proceed" or "install" => InstallDecision.Proceed,
                            "uninstall" => InstallDecision.Uninstall,
                            "cancel" => InstallDecision.Cancel,
                            _ => null
                        };

                        if (decision is null) { throw new IOException($"unknown decision '{message.Value}'"); }
                        return decision.Value;
                    }

                    case "tick":
                        break;

                    case "error":
                        throw new IOException($"the panel reported an error: {message.Message ?? "(no message)"}");

                    case "hello":
                    case "ready":
                        Trace?.Invoke($"<- duplicate '{message.Event}' ignored");
                        break;

                    default:
                        Trace?.Invoke($"<- unknown event '{message.Event}' ignored");
                        break;
                }
            }
        }

        private async Task<bool> WaitForAnswerAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                OverlayMessage? received = await ReadAsync(_reader!, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);

                if (received is null)
                {
                    if (IsStalled())
                    {
                        throw new TimeoutException($"the panel stalled: no output for {_stallTimeoutMs} ms while a question was on screen");
                    }

                    if (IsChildExited()) { throw new IOException($"the panel exited without answering{ExitCodeSuffix()}"); }
                    continue;
                }

                OverlayMessage message = received.Value;
                if (message.Kind == OverlayMessageKind.Eof)
                {
                    throw new IOException($"the panel closed its output without answering{ExitCodeSuffix()}");
                }

                switch (message.Event)
                {
                    case "answer":
                    {
                        string value = (message.Value ?? "").Trim().ToLowerInvariant();
                        if (value is "yes" or "no") { return value == "yes"; }
                        throw new IOException($"unknown answer '{message.Value}'");
                    }

                    case "tick":
                        break;

                    case "error":
                        throw new IOException($"the panel reported an error: {message.Message ?? "(no message)"}");

                    default:
                        Trace?.Invoke($"<- '{message.Event}' while waiting for an answer, ignored");
                        break;
                }
            }
        }

        private void NoteActivity()
        {
            Volatile.Write(ref _lastLineMs, _clock.ElapsedMilliseconds);
        }

        private bool IsStalled()
        {
            if (_stallTimeoutMs == 0) { return false; }
            return _clock.ElapsedMilliseconds - Volatile.Read(ref _lastLineMs) >= _stallTimeoutMs;
        }

        private async Task WatchdogAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // the process handle is already gone
            }

            RecordExitCode();
            bool wasUsable = _usable;
            _usable = false;

            // A failure that already decided why the panel is gone keeps its
            // reason: it is more specific than "the process exited".
            if (!_decided && wasUsable)
            {
                MarkUnusable($"the panel exited with code {_childExitCode} before delivering a decision");
            }
            else if (_decided)
            {
                Trace?.Invoke($"[overlay] the panel exited with code {_childExitCode} after the decision");
            }
        }

        private async Task<InstallDecision> FallBackConsentAsync(Exception error, ConfirmPrompt prompt, CancellationToken cancellationToken)
        {
            string reason = Explain(error);
            MarkUnusable(reason);
            Kill();
            await DrainPumpsAsync(500).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"[ui] falling back to the console installer: {reason}");

            string stderr = StderrSnapshot();
            if (stderr.Length > 0)
            {
                Console.WriteLine("[ui] panel stderr:");
                Console.WriteLine(stderr);
            }

            Console.WriteLine("[ui] the console presenter takes over from here.");
            Console.WriteLine();

            return await _fallback.ConfirmAsync(prompt, cancellationToken).ConfigureAwait(false);
        }

        private void MarkUnusable(string reason)
        {
            _reason = reason;
            _failed = true;
            _usable = false;
        }

        private bool CanPresent()
        {
            if (!_usable || _failed) { return false; }
            Process? process = _process;
            if (process is null || _stdin is null || _reader is null) { return false; }

            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private async Task DrainPumpsAsync(int milliseconds)
        {
            List<Task> pumps = new(2);
            if (_stdoutPump is not null) { pumps.Add(_stdoutPump); }
            if (_stderrPump is not null) { pumps.Add(_stderrPump); }
            if (pumps.Count == 0) { return; }

            await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(milliseconds)).ConfigureAwait(false);
        }

        private static async Task<OverlayMessage?> ReadAsync(ChannelReader<OverlayMessage> reader, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                return await reader.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                return OverlayMessage.Eof();
            }
        }

        private async Task PumpStdoutAsync(Process process, ChannelWriter<OverlayMessage> writer)
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) { break; }

                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) { continue; }

                    NoteActivity();

                    OverlayMessage? parsed = TryParse(trimmed);
                    if (parsed is null)
                    {
                        Trace?.Invoke($"<- unparseable line ignored: {trimmed}");
                        continue;
                    }

                    if (parsed.Value.Event == "tick") { Interlocked.Increment(ref _ticksSeen); }

                    Trace?.Invoke($"<- {trimmed}");
                    await writer.WriteAsync(parsed.Value).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Trace?.Invoke($"<- stdout reader stopped: {Explain(e)}");
            }
            finally
            {
                writer.TryComplete();
            }
        }

        private async Task PumpStderrAsync(Process process)
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) { break; }

                    lock (_logGate)
                    {
                        _stderrTail.Enqueue(line);
                        while (_stderrTail.Count > 50) { _stderrTail.Dequeue(); }
                    }

                    Trace?.Invoke($"[stderr] {line}");
                }
            }
            catch
            {
                // stderr is diagnostics only, never fatal
            }
        }

        private static OverlayMessage? TryParse(string line)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { return null; }
                if (!root.TryGetProperty("event", out JsonElement eventProperty) || eventProperty.ValueKind != JsonValueKind.String) { return null; }

                string name = eventProperty.GetString() ?? "";
                int proto = root.TryGetProperty("proto", out JsonElement protoProperty)
                    && protoProperty.ValueKind == JsonValueKind.Number
                    && protoProperty.TryGetInt32(out int parsedProto)
                        ? parsedProto
                        : 0;

                return new OverlayMessage(
                    OverlayMessageKind.Event,
                    name,
                    ReadString(root, "title"),
                    ReadString(root, "text"),
                    ReadString(root, "value"),
                    ReadString(root, "message"),
                    proto);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        public string StderrSnapshot()
        {
            lock (_logGate)
            {
                return string.Join(Environment.NewLine, _stderrTail);
            }
        }

        private static string Explain(Exception error)
        {
            string message = error.Message;
            if (string.IsNullOrWhiteSpace(message)) { message = error.GetType().Name; }
            return $"{error.GetType().Name}: {message}";
        }

        private bool IsChildExited()
        {
            try
            {
                return _process?.HasExited ?? true;
            }
            catch
            {
                return true;
            }
        }

        private void RecordExitCode()
        {
            if (_childExitCode is not null) { return; }

            try
            {
                _childExitCode = _process?.ExitCode ?? -1;
            }
            catch
            {
                _childExitCode = -1;
            }
        }

        private string ExitCodeSuffix()
        {
            return _childExitCode is null ? "" : $" (exit code {_childExitCode})";
        }

        private void Kill()
        {
            Process? process = _process;
            if (process is null) { return; }

            try
            {
                if (!process.HasExited)
                {
                    Trace?.Invoke($"[overlay] killing the panel (pid {process.Id})");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception e)
            {
                Trace?.Invoke($"[overlay] kill failed: {Explain(e)}");
            }
            finally
            {
                RecordExitCode();
            }
        }

        public void Dispose()
        {
            Kill();

            try
            {
                _process?.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        private static async Task<string?> ProbeAsync(string exePath, CancellationToken cancellationToken)
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = exePath,
                    Arguments = "--probe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };

                using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("the probe did not start");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

                using CancellationTokenSource timeout = new(ProbeTimeoutMs);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return $"the probe did not finish within {ProbeTimeoutMs} ms";
                }

                if (process.ExitCode == 0) { return null; }

                string detail = (await stdout.ConfigureAwait(false)).Trim();
                if (detail.Length == 0) { detail = (await stderr.ConfigureAwait(false)).Trim(); }
                return $"the probe failed (exit {process.ExitCode}){(detail.Length == 0 ? "" : ": " + FirstLine(detail))}";
            }
            catch (Exception e)
            {
                return "the probe could not run: " + Explain(e);
            }
        }

        private static string FirstLine(string text)
        {
            int index = text.IndexOfAny(new[] { '\r', '\n' });
            return (index < 0 ? text : text[..index]).Trim();
        }

        private static bool ScreenReaderRunning()
        {
            if (!OperatingSystem.IsWindows()) { return false; }

            try
            {
                return SystemParametersInfo(SpiGetScreenReader, 0, out bool running, 0) && running;
            }
            catch
            {
                return false;
            }
        }

        private enum OverlayMessageKind
        {
            Event,
            Eof
        }

        private readonly record struct OverlayMessage(
            OverlayMessageKind Kind,
            string Event,
            string? Title,
            string? Text,
            string? Value,
            string? Message,
            int Proto)
        {
            public static OverlayMessage Eof() => new(OverlayMessageKind.Eof, "", null, null, null, null, 0);
        }
    }
}
