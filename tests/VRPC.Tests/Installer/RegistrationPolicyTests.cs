using VRPC.Install;

namespace VRPC.Tests.Installer;

// §8.C: an update must keep the recorded extension IDs, explicit CLI values win,
// and an install state without usable IDs fails instead of resetting to defaults.
public class RegistrationPolicyTests
{
    private static InstallState Recorded(string chromium = "recorded-chromium", string firefox = "recorded@firefox")
    {
        return new InstallState
        {
            Schema = InstallState.CurrentSchema,
            Version = "1.1.0",
            ChromiumExtensionIds = new List<string> { chromium },
            FirefoxExtensionIds = new List<string> { firefox }
        };
    }

    [Fact]
    public void ResolveTarget_UsesTheRecordedFamiliesWhenNothingIsExplicit()
    {
        RegistrationOptions resolved = RegistrationPolicy.ResolveTarget(Recorded(), RegistrationOptions.Default, registrationEnabled: true);

        Assert.Equal(new[] { "recorded-chromium" }, resolved.ChromiumExtensionIds);
        Assert.Equal(new[] { "recorded@firefox" }, resolved.FirefoxExtensionIds);
    }

    [Fact]
    public void ResolveTarget_ExplicitIdsWinOverTheRecordedFamilies()
    {
        RegistrationOptions requested = RegistrationOptions.FromLists(new[] { "explicit-chromium" }, new[] { "explicit@firefox" });

        RegistrationOptions resolved = RegistrationPolicy.ResolveTarget(Recorded(), requested, registrationEnabled: true);

        Assert.Equal(new[] { "explicit-chromium" }, resolved.ChromiumExtensionIds);
        Assert.Equal(new[] { "explicit@firefox" }, resolved.FirefoxExtensionIds);
    }

    [Fact]
    public void ResolveTarget_ExplicitFamilyOnlyReplacesThatFamily()
    {
        RegistrationOptions requested = RegistrationOptions.Default;
        requested.ChromiumExtensionIds.Clear();
        requested.ChromiumExtensionIds.Add("explicit-chromium");
        requested.ChromiumExplicit = true;

        RegistrationOptions resolved = RegistrationPolicy.ResolveTarget(Recorded(), requested, registrationEnabled: true);

        Assert.Equal(new[] { "explicit-chromium" }, resolved.ChromiumExtensionIds);
        Assert.Equal(new[] { "recorded@firefox" }, resolved.FirefoxExtensionIds);
    }

    [Fact]
    public void ResolvePrevious_ReturnsTheInstalledFamilies()
    {
        RegistrationOptions previous = RegistrationPolicy.ResolvePrevious(Recorded(), RegistrationOptions.Default, registrationEnabled: true);

        Assert.Equal(new[] { "recorded-chromium" }, previous.ChromiumExtensionIds);
        Assert.Equal(new[] { "recorded@firefox" }, previous.FirefoxExtensionIds);
    }

    [Fact]
    public void ResolveTarget_WithoutRecordedIds_ThrowsExitCode5()
    {
        InstallState state = new() { Schema = InstallState.CurrentSchema, Version = "1.1.0" };

        InstallException exception = Assert.Throws<InstallException>(
            () => RegistrationPolicy.ResolveTarget(state, RegistrationOptions.Default, registrationEnabled: true));

        Assert.Equal(SetupExitCodes.Failed, exception.ExitCode);
        Assert.Contains("extension IDs", exception.Message);
    }

    [Fact]
    public void ResolveTarget_WhenMigrationFailed_ThrowsExitCode5()
    {
        InstallState state = Recorded();
        state.RegistrationMigrationFailed = true;

        InstallException exception = Assert.Throws<InstallException>(
            () => RegistrationPolicy.ResolveTarget(state, RegistrationOptions.Default, registrationEnabled: true));

        Assert.Equal(SetupExitCodes.Failed, exception.ExitCode);
    }

    [Fact]
    public void ResolveTarget_WhenRegistrationIsDisabled_UsesTheRequestedDefaults()
    {
        InstallState state = new() { Schema = InstallState.CurrentSchema, Version = "1.1.0" };

        RegistrationOptions resolved = RegistrationPolicy.ResolveTarget(state, RegistrationOptions.Default, registrationEnabled: false);

        Assert.Equal(new[] { BrowserCatalog.ChromeStoreId }, resolved.ChromiumExtensionIds);
        Assert.Equal(new[] { BrowserCatalog.FirefoxExtensionId }, resolved.FirefoxExtensionIds);
    }

    [Fact]
    public void ResolveTarget_OnAFreshInstall_UsesTheRequestedDefaults()
    {
        RegistrationOptions resolved = RegistrationPolicy.ResolveTarget(null, RegistrationOptions.Default, registrationEnabled: true);

        Assert.Equal(new[] { BrowserCatalog.ChromeStoreId }, resolved.ChromiumExtensionIds);
        Assert.Equal(new[] { BrowserCatalog.FirefoxExtensionId }, resolved.FirefoxExtensionIds);
    }

    [Fact]
    public void ResolvePrevious_OnAFreshInstall_ReflectsTheRequestedOptions()
    {
        RegistrationOptions resolved = RegistrationPolicy.ResolvePrevious(null, RegistrationOptions.Default, registrationEnabled: true);

        Assert.Equal(new[] { BrowserCatalog.ChromeStoreId }, resolved.ChromiumExtensionIds);
    }
}
