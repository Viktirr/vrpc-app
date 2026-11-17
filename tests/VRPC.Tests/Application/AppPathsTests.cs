using VRPC.Application;

namespace VRPC.Tests.Application
{
    public sealed class AppPathsTests : IDisposable
    {
        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "vrpc-paths-test-" + Guid.NewGuid().ToString("N"));

        public AppPathsTests()
        {
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            AppPaths.ResetRoot();
            try { if (Directory.Exists(_tempRoot)) { Directory.Delete(_tempRoot, recursive: true); } } catch { }
        }

        [Fact]
        public void Custom_root_gets_a_distinct_stable_control_pipe_name()
        {
            AppPaths.ResetRoot();
            string defaultName = AppPaths.ControlPipeNameForCurrentUser();

            AppPaths.OverrideRoot(_tempRoot);
            string customName = AppPaths.ControlPipeNameForCurrentUser();
            string customNameAgain = AppPaths.ControlPipeNameForCurrentUser();

            Assert.NotEqual(defaultName, customName);
            Assert.Equal(customName, customNameAgain);
            Assert.StartsWith(defaultName, customName, StringComparison.Ordinal);

            AppPaths.ResetRoot();
            Assert.Equal(defaultName, AppPaths.ControlPipeNameForCurrentUser());
        }

        [Fact]
        public void Updates_root_containment_only_accepts_paths_below_the_root()
        {
            AppPaths.OverrideRoot(_tempRoot);
            Assert.True(AppPaths.IsUnderUpdatesRoot(Path.Combine(AppPaths.UpdatesDir, "abc")));
            Assert.False(AppPaths.IsUnderUpdatesRoot(Path.Combine(_tempRoot, "state", "elsewhere")));
            Assert.False(AppPaths.IsUnderUpdatesRoot(Path.Combine(Path.GetTempPath(), "vrpc-escape")));
        }
    }
}
