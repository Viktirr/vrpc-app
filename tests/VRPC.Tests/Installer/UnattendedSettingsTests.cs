using VRPC.Configuration;
using VRPC.Install;

namespace VRPC.Tests.Installer;

// The opt-in unattended setting must be visible to the popup and persist in
// Config.json; the updater reads the same static object at runtime.
public class UnattendedSettingsTests
{
    [Fact]
    public void Unattended_setting_is_visible_and_round_trips()
    {
        using InstallerTestEnvironment environment = new();

        VRPCSettings.CheckSettings();
        Assert.False(VRPCSettings.settingsData.UnattendedUpdates);
        Assert.True(VRPCSettings.SettingsInfo.SettingsDictionary.ContainsKey("CategoryUpdates"));
        Assert.True(VRPCSettings.SettingsInfo.SettingsDictionary.ContainsKey("UnattendedUpdates"));

        VRPCSettings.SetSetting("UnattendedUpdates", "true");
        Assert.True(VRPCSettings.settingsData.UnattendedUpdates);

        VRPCSettings.CheckSettings();
        Assert.True(VRPCSettings.settingsData.UnattendedUpdates);
    }
}
