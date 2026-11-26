using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VRPC.Application;
using VRPC.Deployment.Install;
using VRPC.Deployment.Updates;

namespace VRPC.Tests.Updates
{
    public sealed class UpdateProcessLauncherTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vrpc-launcher-test-" + Guid.NewGuid().ToString("N"));

        public UpdateProcessLauncherTests()
        {
            Directory.CreateDirectory(_root);
            AppPaths.OverrideRoot(_root);
        }

        public void Dispose()
        {
            AppPaths.ResetRoot();
            try { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } } catch { }
        }

        [Fact]
        public void Builds_the_expected_arguments_and_scrubs_test_environment()
        {
            string packageDirectory = CreatePackage("1.2.0");
            WriteInstallState();

            Environment.SetEnvironmentVariable("VRPC_UPDATE_LOCAL_FEED", "127.0.0.1:1");
            Environment.SetEnvironmentVariable("VRPC_NO_REGISTRY", "1");
            try
            {
                ProcessStartInfo? startInfo = UpdateProcessLauncher.BuildStartInfo(packageDirectory, "1.2.0", out string logPath, out string error);

                Assert.NotNull(startInfo);
                Assert.Equal("", error);
                Assert.True(File.Exists(Path.Combine(packageDirectory, "VRPC.exe")));
                Assert.False(startInfo!.UseShellExecute);
                Assert.True(startInfo.CreateNoWindow);

                List<string> arguments = startInfo.ArgumentList.ToList();
                Assert.Equal("--update", arguments[0]);
                Assert.Equal("--payload", arguments[1]);
                Assert.Equal(packageDirectory, arguments[2]);
                Assert.Equal("--root", arguments[3]);
                Assert.Equal(AppPaths.Root, arguments[4]);
                Assert.Contains("--panel", arguments);
                Assert.Contains("--extension-id", arguments);
                Assert.Contains("chromium-dev-id", arguments);
                Assert.Contains("--firefox-extension-id", arguments);
                Assert.Contains("vrpc@viktir.com", arguments);

                Assert.False(startInfo.Environment.ContainsKey("VRPC_UPDATE_LOCAL_FEED"));
                Assert.False(startInfo.Environment.ContainsKey("VRPC_NO_REGISTRY"));
                Assert.True(startInfo.Environment.ContainsKey("VRPC_SETUP_CONSOLE_LOG"));
                Assert.EndsWith(".log", logPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VRPC_UPDATE_LOCAL_FEED", null);
                Environment.SetEnvironmentVariable("VRPC_NO_REGISTRY", null);
            }
        }

        [Fact]
        public void Unattended_build_uses_quiet_yes_and_no_panel()
        {
            string packageDirectory = CreatePackage("1.2.0");

            ProcessStartInfo? startInfo = UpdateProcessLauncher.BuildStartInfo(
                packageDirectory, "1.2.0", out _, out string error, unattended: true);

            Assert.NotNull(startInfo);
            Assert.Equal("", error);
            List<string> arguments = startInfo!.ArgumentList.ToList();
            Assert.Contains("--quiet", arguments);
            Assert.Contains("--yes", arguments);
            Assert.DoesNotContain("--panel", arguments);
        }

        [Fact]
        public void Wrong_version_or_foreign_directory_is_rejected()
        {
            string packageDirectory = CreatePackage("1.2.0");
            Assert.Null(UpdateProcessLauncher.BuildStartInfo(packageDirectory, "1.3.0", out _, out string versionError));
            Assert.Contains("expected 1.3.0", versionError);

            string outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            Assert.Null(UpdateProcessLauncher.BuildStartInfo(outside, "1.2.0", out _, out string outsideError));
            Assert.Contains("outside the updater", outsideError);
        }

        [Fact]
        public void Every_automation_variable_is_scrubbed_from_the_setup_and_panel_environment()
        {
            string packageDirectory = CreatePackage("1.2.0");
            WriteInstallState();

            foreach (string name in AutomationEnvironment.Variables)
            {
                Environment.SetEnvironmentVariable(name, "1");
            }

            try
            {
                ProcessStartInfo? setup = UpdateProcessLauncher.BuildStartInfo(packageDirectory, "1.2.0", out _, out string error);
                Assert.NotNull(setup);
                Assert.Equal("", error);
                foreach (string name in AutomationEnvironment.Variables)
                {
                    Assert.False(setup!.Environment.ContainsKey(name), $"{name} leaked into the setup environment");
                }
                Assert.True(setup!.Environment.ContainsKey("VRPC_SETUP_CONSOLE_LOG"));

                ProcessStartInfo panel = OverlayPresenter.BuildStartInfo(Path.Combine(_root, "vrpc-installer-ui.exe"));
                foreach (string name in AutomationEnvironment.Variables)
                {
                    Assert.False(panel.Environment.ContainsKey(name), $"{name} leaked into the panel environment");
                }
            }
            finally
            {
                foreach (string name in AutomationEnvironment.Variables)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
            }
        }

        [Fact]
        public void Launch_fails_before_host_shutdown_when_the_package_has_no_panel()
        {
            string packageDirectory = CreatePackage("1.2.0", includePanel: false);

            UpdateProcessLauncher launcher = new();
            Assert.False(launcher.TryLaunch(packageDirectory, "1.2.0", unattended: false, out string error));

            Assert.Contains("cannot ask for your approval", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(AppPaths.SetupLeasePath));
        }

        private void WriteInstallState()
        {
            Directory.CreateDirectory(AppPaths.State);
            string json = JsonSerializer.Serialize(new
            {
                schema = 2,
                version = "1.1.0",
                chromiumExtensionIds = new[] { "chromium-dev-id" },
                firefoxExtensionIds = new[] { "vrpc@viktir.com" }
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(AppPaths.InstallStatePath, json, new UTF8Encoding(false));
        }

        private string CreatePackage(string version, bool includePanel = true)
        {
            string operationDirectory = Path.Combine(AppPaths.UpdatesDir, Guid.NewGuid().ToString("N"));
            string packageDirectory = Path.Combine(operationDirectory, "package");
            Directory.CreateDirectory(packageDirectory);

            List<string> names = new() { "VRPC.exe" };
            if (includePanel) { names.Add("vrpc-installer-ui.exe"); }
            foreach (string name in names)
            {
                File.WriteAllText(Path.Combine(packageDirectory, name), name == "VRPC.exe" ? "stub executable" : "stub panel");
            }

            List<object> files = new();
            foreach (string name in names)
            {
                string path = Path.Combine(packageDirectory, name);
                files.Add(new
                {
                    path = name,
                    size = new FileInfo(path).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                });
            }

            string payloadJson = JsonSerializer.Serialize(new
            {
                schema = 1,
                product = "VRPC",
                version,
                channel = "stable",
                entryPoint = "VRPC.exe",
                files
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            File.WriteAllText(Path.Combine(packageDirectory, "payload.json"), payloadJson, new UTF8Encoding(false));
            return packageDirectory;
        }
    }
}
