using VRPC.Application;
using VRPC.Install;

namespace VRPC.Tests.Installer;

// §8.A: one cross-process lock serializes setup mutations and reports exit code 7.
public class SetupOperationLockTests
{
    [Fact]
    public void Acquire_CreatesTheLockUnderTheInstallRoot()
    {
        using InstallerTestEnvironment environment = new();

        using SetupOperationLock operationLock = SetupOperationLock.Acquire(1000);

        Assert.True(File.Exists(environment.LockPath));
        Assert.Equal(Path.Combine(environment.Root, "state", "setup-operation.lock"), AppPaths.SetupOperationLockPath);
    }

    [Fact]
    public void Acquire_WhileTheLockIsHeld_ThrowsExitCode7()
    {
        using InstallerTestEnvironment environment = new();
        using SetupOperationLock first = SetupOperationLock.Acquire(1000);

        InstallException exception = Assert.Throws<InstallException>(() => SetupOperationLock.Acquire(150));

        Assert.Equal(SetupExitCodes.AnotherOperationInProgress, exception.ExitCode);
        Assert.Contains("Another setup operation is in progress", exception.Message);
    }

    [Fact]
    public void TryAcquire_WhileTheLockIsHeld_ReturnsFalseWithAMessage()
    {
        using InstallerTestEnvironment environment = new();
        using SetupOperationLock first = SetupOperationLock.Acquire(1000);

        bool acquired = SetupOperationLock.TryAcquire(150, out SetupOperationLock? operationLock, out string error);

        Assert.False(acquired);
        Assert.Null(operationLock);
        Assert.Contains("Another setup operation is in progress", error);
    }

    [Fact]
    public void Acquire_AfterTheFirstLockIsReleased_Succeeds()
    {
        using InstallerTestEnvironment environment = new();
        SetupOperationLock first = SetupOperationLock.Acquire(1000);
        first.Dispose();

        using SetupOperationLock second = SetupOperationLock.Acquire(1000);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Engine_WhileAnotherSetupHoldsTheLock_ReturnsExitCode7WithoutMutating()
    {
        using InstallerTestEnvironment environment = new();
        using FileStream held = new(environment.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        InstallEngine engine = new(
            new InstallRequest { SourceDirectory = environment.Root, RequestedAction = InstallAction.Update },
            environment.Presenter,
            new SetupLog(),
            environment.Registration);

        InstallResult result = await engine.RunAsync();

        Assert.Equal(SetupExitCodes.AnotherOperationInProgress, result.ExitCode);
        Assert.False(result.Success);
        Assert.NotNull(environment.Presenter.Result);
        Assert.False(File.Exists(environment.MarkerPath));
    }
}
