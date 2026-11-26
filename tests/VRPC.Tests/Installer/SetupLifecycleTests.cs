using System.Text;
using System.Text.Json;
using VRPC.Application;
using VRPC.Deployment.Install;

namespace VRPC.Tests.Installer;

// Durable host/setup ownership: a live lease blocks host startup, a dead one does
// not, and only unsafe transaction phases keep the host down after a crash.
public class SetupLifecycleTests
{
    [Fact]
    public void A_live_lease_blocks_host_startup_until_it_is_disposed()
    {
        using InstallerTestEnvironment environment = new();
        SetupLifecycleLease lease = SetupLifecycle.Begin("unit test");

        Assert.True(SetupLifecycle.IsSetupActive());
        Assert.True(SetupLifecycle.BlocksHostStartup(out string reason));
        Assert.Contains("setup is running", reason, StringComparison.OrdinalIgnoreCase);

        lease.Dispose();

        Assert.False(SetupLifecycle.IsSetupActive());
        Assert.False(SetupLifecycle.BlocksHostStartup(out _));
        Assert.False(File.Exists(AppPaths.SetupLeasePath));
    }

    [Fact]
    public void A_stale_lease_from_a_dead_process_does_not_block_startup()
    {
        using InstallerTestEnvironment environment = new();
        WriteLease(token: "stale", processId: int.MaxValue);

        Assert.False(SetupLifecycle.IsSetupActive());
        Assert.False(SetupLifecycle.BlocksHostStartup(out _));
    }

    [Fact]
    public void A_lease_is_only_deleted_by_the_process_that_owns_it()
    {
        using InstallerTestEnvironment environment = new();
        SetupLifecycleLease first = SetupLifecycle.Begin("first", "token-first");
        SetupLifecycleLease second = SetupLifecycle.Begin("second", "token-second");

        first.Dispose();
        Assert.True(File.Exists(AppPaths.SetupLeasePath));
        Assert.True(SetupLifecycle.IsSetupActive());

        second.Dispose();
        Assert.False(File.Exists(AppPaths.SetupLeasePath));
    }

    [Fact]
    public void Unsafe_transaction_phases_block_startup_but_pre_swap_phases_do_not()
    {
        using InstallerTestEnvironment environment = new();

        environment.WriteMarker(environment.NewMarker(UpdatePhase.Staged));
        Assert.False(SetupLifecycle.BlocksHostStartup(out _));

        environment.WriteMarker(environment.NewMarker(UpdatePhase.StoppingHost));
        Assert.True(SetupLifecycle.BlocksHostStartup(out string reason));
        Assert.Contains("interrupted setup", reason, StringComparison.OrdinalIgnoreCase);

        environment.WriteMarker(environment.NewMarker(UpdatePhase.Registering));
        Assert.True(SetupLifecycle.BlocksHostStartup(out _));

        InstallStateStore.DeleteTransaction();
        Assert.False(SetupLifecycle.BlocksHostStartup(out _));
    }

    [Fact]
    public void A_dead_handoff_lease_covers_the_child_start_window_but_expires()
    {
        using InstallerTestEnvironment environment = new();
        WriteLease(token: "handoff", processId: int.MaxValue, reason: "update handoff", startedAtUtc: DateTime.UtcNow);
        Assert.True(SetupLifecycle.IsSetupActive());
        Assert.True(SetupLifecycle.BlocksHostStartup(out _));

        WriteLease(token: "handoff", processId: int.MaxValue, reason: "update handoff", startedAtUtc: DateTime.UtcNow.AddMinutes(-5));
        Assert.False(SetupLifecycle.IsSetupActive());
        Assert.False(SetupLifecycle.BlocksHostStartup(out _));
    }

    private static void WriteLease(string token, int processId, string reason = "stale test", DateTime? startedAtUtc = null)
    {
        Directory.CreateDirectory(AppPaths.State);
        var lease = new
        {
            schema = 1,
            token,
            processId,
            processStartedUtc = DateTime.UtcNow.ToString("O"),
            reason,
            startedAtUtc = (startedAtUtc ?? DateTime.UtcNow).ToString("O"),
            expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O")
        };
        File.WriteAllText(AppPaths.SetupLeasePath, JsonSerializer.Serialize(lease), new UTF8Encoding(false));
    }
}
