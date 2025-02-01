using System.Text;
using System.Text.Json;
using VRPC.Logging;

namespace VRPC.Configuration
{
    class VRPCSettings
    {
        private static string pathAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string UserAppDataPath = Path.Combine(pathAppData, "VRPCApp");
        public static string UserDataPath = Path.Combine(UserAppDataPath, "Data");
        public static string ListeningDataPath = Path.Combine(UserDataPath, "ListeningData.json");
        public static string ConfigPath = Path.Combine(UserDataPath, "Config.json");
        public static SettingsData settingsData = new SettingsData();

        public static string LogPath = Path.Combine(UserDataPath, "Log.txt");
        public static Log log = new Log();

        public static void CheckIfApplicationDataFolderExists()
        {
            try
            {
                Directory.CreateDirectory(UserDataPath);
                log.Write("[VRPCSettings] Application data directory created.");
            }
            catch (Exception e)
            {
                log.Write($"[VRPCSettings] FATAL: Application data directory was not able to be created. Quitting! Exception {e.Data}");
                throw new Exception($"[VRPCSettings] FATAL: Application data directory was not able to be created. Quitting! Exception {e.Data}");
            }
        }
        public static class SettingsInfo
        {
            // Example data:
            // LoggingWriteEnabled
            // {
            //     "InternalName": "LoggingWriteEnabled"
            //     "DisplayName": "Logging Write Enabled"
            //     "Description": "Enable or disable logging to a file"
            //     "Type": "bool"
            // }

            public static Dictionary<string, string> LoggingWriteEnabled { get; } = new Dictionary<string, string> {
                { "InternalName", "LoggingWriteEnabled" },
                { "DisplayName", "Logging Write Enabled" },
                { "Description", "(NOT RECOMMENDED, LEAVE AT DISABLED) Enable or disable ALL logging to a file" },
                { "Visibility", "Hidden" },
                { "Type", "bool" }
            };

            public static Dictionary<string, string> DisableClearingLogs { get; } = new Dictionary<string, string> {
                { "InternalName", "DisableClearingLogs" },
                { "DisplayName", "Disable Clearing Logs" },
                { "Description", "(NOT RECOMMENDED, LEAVE AT DISABLED) Enable or disable clearing logs on startup" },
                { "Visibility", "Hidden" },
                { "Type", "bool" }
            };

            public static Dictionary<string, string> EnableDiscordRPC { get; } = new Dictionary<string, string> {
                { "InternalName", "EnableDiscordRPC" },
                { "DisplayName", "Enable Discord RPC" },
                { "Description", "Show 'Listening to' status on your Discord profile" },
                { "Visibility", "Shown" },
                { "Type", "bool" }
            };

            public static Dictionary<string, string> EnableListeningData { get; } = new Dictionary<string, string> {
                { "InternalName", "EnableListeningData" },
                { "DisplayName", "Enable Listening Data" },
                { "Description", "Saves the time you listened to music locally, see About section for more information" },
                { "Visibility", "Shown" },
                { "Type", "bool" }
            };

            public static Dictionary<string, string> EnableListeningToButton { get; } = new Dictionary<string, string> {
                { "InternalName", "EnableListeningToButton" },
                { "DisplayName", "Enable Listening To Button" },
                { "Description", "Show 'Listen on ...' button on your Discord profile (Not visible to you)" },
                { "Visibility", "Shown" },
                { "Type", "bool" }
            };

            public static Dictionary<string, string> ShowcaseDataToRPC { get; } = new Dictionary<string, string> {
                { "InternalName", "ShowcaseDataToRPC" },
                { "DisplayName", "Showcase Data To RPC" },
                { "Description", "Periodically show the listening data stored (i.e. how much you've listened to a song) on Discord" },
                { "Visibility", "Shown" },
                { "Type", "bool" }  
            };

            public static Dictionary<string, string> ShowAppWatermark { get; } = new Dictionary<string, string> {
                { "InternalName", "ShowAppWatermark" },
                { "DisplayName", "Show Watermark" },
                { "Description", "Show 'vrpc' when hovering over playing status on your Discord profile" },
                { "Visibility", "Shown" },
                { "Type", "bool" }  
            };

            public static Dictionary<string, object> SettingsDictionary = new Dictionary<string, object> {
                { "LoggingWriteEnabled", LoggingWriteEnabled },
                { "DisableClearingLogs", DisableClearingLogs },
                { "EnableDiscordRPC", EnableDiscordRPC },
                { "EnableListeningToButton", EnableListeningToButton },
                { "EnableListeningData", EnableListeningData },
                { "ShowcaseDataToRPC", ShowcaseDataToRPC },
                { "ShowAppWatermark", ShowAppWatermark }
            };
        }

        public class SettingsData
        {
            public bool LoggingWriteEnabled { get; set; } = false;
            public bool DisableClearingLogs { get; set; } = false;
            public bool EnableDiscordRPC { get; set; } = true;
            public bool EnableListeningToButton { get; set; } = true;
            public bool EnableListeningData { get; set; } = true;
            public bool ShowcaseDataToRPC { get; set; } = false;
            public bool ShowAppWatermark { get; set; } = false;
        }

        private static SettingsData ReadConfigFile()
        {
            SettingsData settingsData = new SettingsData();
            try
            {
                string json = System.IO.File.ReadAllText(ConfigPath, Encoding.UTF8);
                settingsData = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
            }
            catch
            {
                log.Warn("[Configuration] Couldn't read from file. Won't update, maybe the file is corrupted or doesn't exist?");
                return settingsData;
            }
            return settingsData;
        }

        private static SettingsData SaveConfigFile(SettingsData settingsData)
        {
            try
            {
                string jsonString = JsonSerializer.Serialize(settingsData, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(ConfigPath, jsonString, Encoding.UTF8);
            }
            catch { log.Error("[Configuration] Couldn't write to file"); }
            return settingsData;
        }

        public static void CheckSettings()
        {
            settingsData = ReadConfigFile();
            SaveConfigFile(settingsData);
        }

        public static object GetSetting(string config)
        {
            try
            {
                if (SettingsInfo.SettingsDictionary.ContainsKey(config))
                {
                    return SettingsInfo.SettingsDictionary[config];
                }
                else
                {
                    log.Warn($"[VRPCSettings] There's no detailed description for {config}.");
                    return null;
                }
            }
            catch { return null; }
        }

        public static void SetSetting(string configName, string configValue)
        {
            try
            {
                if (SettingsInfo.SettingsDictionary.ContainsKey(configName))
                {
                    if (((Dictionary<string, string>)SettingsInfo.SettingsDictionary[configName])["Type"] == "bool")
                    {
                        bool value = Convert.ToBoolean(configValue);
                        var property = settingsData.GetType().GetProperty(configName);
                        if (property != null)
                        {
                            property.SetValue(settingsData, value);
                            log.Info($"[VRPCSettings] Updated setting {configName} to {value}.");
                            SaveConfigFile(settingsData);
                            log.Write($"[VRPCSettings] Saved settings to file.");
                        }
                        else
                        {
                            log.Warn($"[VRPCSettings] Property {configName} not found.");
                        }
                    }
                    else
                    {
                        log.Warn($"[VRPCSettings] Couldn't set {configName} to {configValue}. Type is not bool.");
                    }
                }
                else
                {
                    log.Warn($"[VRPCSettings] Couldn't set {configName} to {configValue}. It doesn't exist.");
                }
            }
            catch (Exception e)
            {
                log.Error($"[VRPCSettings] Couldn't set {configName} to {configValue}. Exception: {e.Message}");
            }
        }
    }
}