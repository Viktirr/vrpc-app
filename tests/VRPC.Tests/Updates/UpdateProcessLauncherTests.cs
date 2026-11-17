using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VRPC.Application;
using VRPC.Updates;

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

        private string CreatePackage(string version)
        {
            string operationDirectory = Path.Combine(AppPaths.UpdatesDir, Guid.NewGuid().ToString("N"));
            string packageDirectory = Path.Combine(operationDirectory, "package");
            Directory.CreateDirectory(packageDirectory);

            File.WriteAllText(Path.Combine(packageDirectory, "VRPC.exe"), "stub executable");
            File.WriteAllText(Path.Combine(packageDirectory, "vrpc-installer-ui.exe"), "stub panel");

            List<object> files = new();
            foreach (string name in new[] { "VRPC.exe", "vrpc-installer-ui.exe" })
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
