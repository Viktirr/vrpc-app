using VRPC.Application;
using VRPC.Install;

namespace VRPC.Setup
{
    // Console setup entry: command parsing, installed-app guard, engine handoff.
    public static class SetupEntry
    {
        // The engine is async because the panel is an interactive process; this
        // wrapper keeps the process entry point synchronous.
        public static int Run(string[] args)
        {
            RedirectConsoleIfRequested();
            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (InstallException e)
            {
                Console.Error.WriteLine(e.Message);
                return e.ExitCode;
            }
        }

        // An updater-started setup outlives the host that launched it, so its
        // inherited stdout/stderr handles become broken pipes. Console output is
        // redirected to a log file before any prompt or help text can touch them.
        private static void RedirectConsoleIfRequested()
        {
            try
            {
                string? path = Environment.GetEnvironmentVariable("VRPC_SETUP_CONSOLE_LOG");
                if (string.IsNullOrWhiteSpace(path)) { return; }

                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

                StreamWriter writer = new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                    new System.Text.UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                Console.SetOut(writer);
                Console.SetError(writer);
            }
            catch
            {
            }
        }

        // Parses commands before touching the installation.
        public static async Task<int> RunAsync(string[] args)
        {
            SetupOptions options;
            try
            {
                options = SetupOptions.Parse(args);
            }
            catch (SetupUsageException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                PrintUsage();
                return SetupExitCodes.Usage;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Invalid arguments: " + e.Message);
                return SetupExitCodes.Usage;
            }

            if (options.ShowHelp)
            {
                PrintUsage();
                return SetupExitCodes.Success;
            }

            if (options.ShowVersion)
            {
                Console.WriteLine($"VRPC {ProductInfo.Version}");
                return SetupExitCodes.Success;
            }

            if (!string.IsNullOrWhiteSpace(options.Root)) { AppPaths.OverrideRoot(options.Root); }

            if (options.SelfTest) { return SelfTest.Run(); }
            bool insideApp = AppPaths.IsUnderApp(AppContext.BaseDirectory);
            if (options.Uninstall && !options.UninstallTemp && insideApp)
            {
                return UninstallLauncher.Launch(options);
            }

            if (args.Length == 0)
            {
                if (insideApp)
                {
                    Console.WriteLine($"VRPC {ProductInfo.Version} is installed at {AppPaths.Root}.");
                    Console.WriteLine("VRPC is started by your browser. To update it, download the latest release and run that setup.");
                    Console.WriteLine("To uninstall, use Settings > Apps > Installed apps > VRPC > Uninstall.");
                    return SetupExitCodes.Success;
                }

                string localPayload = Path.Combine(AppContext.BaseDirectory, AppPaths.PayloadManifestName);
                if (!File.Exists(localPayload))
                {
                    Console.Error.WriteLine($"No {AppPaths.PayloadManifestName} was found next to this executable.");
                    Console.Error.WriteLine("This build is incomplete. Extract the full release package and run VRPC.exe again.");
                    return SetupExitCodes.InvalidPayload;
                }
            }

            if (insideApp && options.Action != InstallAction.Uninstall && !options.UninstallTemp)
            {
                Console.Error.WriteLine("VRPC is already installed. Download the latest release and run that setup to update it.");
                return SetupExitCodes.Usage;
            }

            // The operation lock is taken before the lease and the consent prompt:
            // a second setup can never overwrite a live lease and then exit,
            // leaving the first setup unprotected. Acquire throws exit code 7,
            // which Run reports before any panel is shown.
            using SetupOperationLock operationLock = SetupOperationLock.Acquire();
            using SetupLifecycleLease lease = SetupLifecycle.Begin(
                options.Action?.ToString() ?? "setup",
                options.SetupLeaseToken);
            SetupLog log = new();
            IPresenter presenter = await CreatePresenterAsync(options);
            log.Write($"UI: {DescribePresenter(presenter)}");
            InstallRequest request = new()
            {
                SourceDirectory = options.PayloadDirectory,
                RequestedAction = options.Action,
                Force = options.Force,
                Quiet = options.Quiet,
                Yes = options.Yes,
                AllowDowngrade = options.AllowDowngrade,
                PurgeData = options.PurgeData,
                FromTempInstaller = options.UninstallTemp,
                Registration = options.Registration
            };

            try
            {
                InstallEngine engine = new(request, presenter, log, lease: lease, operationLock: operationLock);
                InstallResult result = await engine.RunAsync();
                return result.ExitCode;
            }
            finally
            {
                (presenter as IDisposable)?.Dispose();
            }
        }

        // The one place the UI is chosen. The panel needs a human at a keyboard
        // and a GPU that can composite it; everything else keeps the console.
        private static async Task<IPresenter> CreatePresenterAsync(SetupOptions options)
        {
            ConsolePresenter console = new(options.Quiet, options.Yes);

            // An unattended run must never open a window, so --quiet and --yes win
            // even when --panel was passed as well.
            if (options.UsePanel && (options.UseConsole || options.Quiet || options.Yes))
            {
                Console.Error.WriteLine("--panel is ignored: --console, --quiet and --yes keep the console installer.");
            }

            if (options.UseConsole || options.Quiet || options.Yes) { return console; }
            if (!OperatingSystem.IsWindows()) { return console; }

            // Redirected handles mean an automated run: nobody can click, and the
            // panel would sit there until a timeout. --panel is the one way to say
            // that a person is watching a console they cannot type into.
            if (!options.UsePanel && (Console.IsInputRedirected || Console.IsOutputRedirected)) { return console; }

            OverlayPresenter? overlay = await OverlayPresenter.TryCreateAsync(fallback: console);
            return overlay is null ? console : overlay;
        }

        private static string DescribePresenter(IPresenter presenter)
        {
            return presenter switch
            {
                OverlayPresenter overlay => "setup panel at " + OverlayPresenter.ResolveExePath(),
                _ => OperatingSystem.IsWindows() ? "console" : "console (no panel on this platform)"
            };
        }

        // Keep in sync with SetupOptions.Parse; --uninstall-temp stays hidden.
        private static void PrintUsage()
        {
            Console.WriteLine("VRPC setup");
            Console.WriteLine();
            Console.WriteLine("  VRPC.exe                     Install or update from the package next to this executable");
            Console.WriteLine("  VRPC.exe --install           Install or update");
            Console.WriteLine("  VRPC.exe --update            Update an existing installation");
            Console.WriteLine("  VRPC.exe --repair            Reinstall the same version");
            Console.WriteLine("  VRPC.exe --uninstall         Uninstall (keeps data unless --purge)");
            Console.WriteLine("  VRPC.exe --selftest          Check the installed application");
            Console.WriteLine("  VRPC.exe --version           Print the version");
            Console.WriteLine();
            Console.WriteLine("  --payload <dir>              Directory containing payload.json (default: next to VRPC.exe)");
            Console.WriteLine("  --root <dir>                 Override the install root");
            Console.WriteLine("  --extension-id <id>          Chromium extension ID (repeatable)");
            Console.WriteLine("  --firefox-extension-id <id>  Firefox extension ID (repeatable)");
            Console.WriteLine("  --force                      Force-close a running VRPC");
            Console.WriteLine("  --allow-downgrade            Allow installing an older version");
            Console.WriteLine("  --purge                      Delete settings/listening data on uninstall");
            Console.WriteLine("  --quiet                      No progress output; auto-confirm");
            Console.WriteLine("  --yes                        Auto-confirm prompts");
            Console.WriteLine("  --console                    Use the console installer instead of the setup panel");
            Console.WriteLine("  --panel                      Use the setup panel even when the console is redirected");
            Console.WriteLine("  --help                       Show this help");
        }
    }

    // Argument parser kept separate from the engine.
    internal sealed class SetupOptions
    {
        public InstallAction? Action { get; private set; }
        public string PayloadDirectory { get; private set; } = AppContext.BaseDirectory;
        public string? Root { get; private set; }
        public bool Uninstall => Action == InstallAction.Uninstall;
        public bool UninstallTemp { get; private set; }
        public bool SelfTest { get; private set; }
        public bool ShowHelp { get; private set; }
        public bool ShowVersion { get; private set; }
        public bool Force { get; private set; }
        public bool Quiet { get; private set; }
        public bool Yes { get; private set; }
        public bool UseConsole { get; private set; }
        public bool UsePanel { get; private set; }
        public bool AllowDowngrade { get; private set; }
        public bool PurgeData { get; private set; }
        public string SetupLeaseToken { get; private set; } = "";
        public RegistrationOptions Registration { get; private set; } = RegistrationOptions.Default;

        public static SetupOptions Parse(string[] args)
        {
            SetupOptions options = new();
            bool chromiumCleared = false;
            bool firefoxCleared = false;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                switch (argument.ToLowerInvariant())
                {
                    case "--install": options.Action = InstallAction.Install; break;
                    case "--update": options.Action = InstallAction.Update; break;
                    case "--repair": options.Action = InstallAction.Repair; break;
                    case "--uninstall": options.Action = InstallAction.Uninstall; break;
                    case "--uninstall-temp":
                        options.Action = InstallAction.Uninstall;
                        options.UninstallTemp = true;
                        break;
                    case "--selftest": options.SelfTest = true; break;
                    case "--help":
                    case "-h": options.ShowHelp = true; break;
                    case "--version": options.ShowVersion = true; break;
                    case "--force": options.Force = true; break;
                    case "--quiet": options.Quiet = true; options.Yes = true; break;
                    case "--yes": options.Yes = true; break;
                    case "--console": options.UseConsole = true; break;
                    case "--panel": options.UsePanel = true; break;
                    case "--allow-downgrade": options.AllowDowngrade = true; break;
                    case "--purge": options.PurgeData = true; break;
                    case "--payload":
                        options.PayloadDirectory = Path.GetFullPath(Next(args, ref i, argument));
                        break;
                    case "--root":
                        options.Root = Path.GetFullPath(Next(args, ref i, argument));
                        break;
                    case "--setup-lease-token":
                        options.SetupLeaseToken = Next(args, ref i, argument);
                        break;
                    case "--extension-id":
                        if (!chromiumCleared)
                        {
                            options.Registration.ChromiumExtensionIds.Clear();
                            options.Registration.ChromiumExplicit = true;
                            chromiumCleared = true;
                        }
                        options.Registration.ChromiumExtensionIds.Add(Next(args, ref i, argument));
                        break;
                    case "--firefox-extension-id":
                        if (!firefoxCleared)
                        {
                            options.Registration.FirefoxExtensionIds.Clear();
                            options.Registration.FirefoxExplicit = true;
                            firefoxCleared = true;
                        }
                        options.Registration.FirefoxExtensionIds.Add(Next(args, ref i, argument));
                        break;
                    default:
                        throw new SetupUsageException($"Unknown argument: {argument}");
                }
            }

            return options;
        }

        private static string Next(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length) { throw new SetupUsageException($"{name} needs a value."); }
            return args[++index];
        }
    }

    internal sealed class SetupUsageException : Exception
    {
        public SetupUsageException(string message) : base(message) { }
    }
}
