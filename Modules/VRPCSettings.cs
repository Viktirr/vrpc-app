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

            public static Dictionary<string, string> CategoryLogging { get; } = new Dictionary<string, string> {
                { "InternalName", "CategoryLogging" },
                { "DisplayName", "Logging" },
                { "Description", "" },
                { "Visibility", "Hidden" },
                { "Type", "category" }
            };

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

            public static Dictionary<string, string> CategoryRichPresence { get; } = new Dictionary<string, string> {
                { "InternalName", "CategoryRichPresence" },
                { "DisplayName", "Discord Rich Presence" },
                { "Description", "" },
                { "Visibility", "Shown" },
                { "Type", "category" }
            };

            public static Dictionary<string, string> EnableDiscordRPC { get; } = new Dictionary<string, string> {
                { "InternalName", "EnableDiscordRPC" },
                { "DisplayName", "Enable Discord RPC" },
                { "Description", "Show 'Listening to' status on your Discord profile" },
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
                { "DisplayName", "Show Time Listened on RPC" },
                { "Description", "Show 'Listened for x hours' on Discord when listening to a song." },
                { "Visibility", "Shown" },
                { "Type", "bool" }  
            };
            
            public static Dictionary<string, string> ShowPlayingStatus { get; } = new Dictionary<string, string> { // no use  yet
                { "InternalName", "ShowPlayingStatus" },
                { "DisplayName", "Show Playing Status" },
                { "Description", "Show 'Paused' when pausing a song instead of removing Rich Presence from being shown." },
                { "Visibility", "Shown" },
                { "Type", "bool" }  
            };

            public static Dictionary<string, string> ShowAppWatermark { get; } = new Dictionary<string, string> {
                { "InternalName", "ShowAppWatermark" },
                { "DisplayName", "Show Watermark" },
                { "Description", "Show 'vrpc' when hovering over playing status on your Discord profile (requires Show Playing Status enabled)." },
                { "Visibility", "Shown" },
                { "Type", "bool" }  
            };

            public static Dictionary<string, string> CategoryListeningData { get; } = new Dictionary<string, string> {
                { "InternalName", "CategoryListeningData" },
                { "DisplayName", "Listening Data" },
                { "Description", "" },
                { "Visibility", "Shown" },
                { "Type", "category" }
            };

            public static Dictionary<string, string> EnableListeningData { get; } = new Dictionary<string, string> {
                { "InternalName", "EnableListeningData" },
                { "DisplayName", "Enable Listening Data" },
                { "Description", "Saves the time you listened to music locally, see About section for more information" },
                { "Visibility", "Shown" },
                { "Type", "bool" }
            };

            public static Dictionary<string, string> CategoryYouTubeMusic { get; } = new Dictionary<string, string> {
                { "InternalName", "CategoryYouTubeMusic" },
                { "DisplayName", "YouTube Music" },
                { "Description", "" },
                { "Visibility", "Shown" },
                { "Type", "category" }
            };

            public static Dictionary<string, string> EnableYouTubeMusic { get; } = new Dictionary<string, string> { // no use yet
                { "InternalName", "EnableYouTubeMusic" },
                { "DisplayName", "Enable YouTube Music Detection" },
                { "Description", "Should the app detect YouTube Music?" },
                { "Visibility", "Shown" },
                { "Type", "bool" }  
            };

            public static Dictionary<string, string> CategorySoundcloud { get; } = new Dictionary<string, string> {
                { "InternalName", "CategorySoundcloud" },
                { "DisplayName", "Soundcloud" },
                { "Description", "" },
                { "Visibility", "Shown" },
                { "Type", "category" }
            };

            public static Dictionary<string, string> EnableSoundcloud { get; } = new Dictionary<string, string> { // no use  yet
                { "InternalName", "EnableSoundcloud" },
                { "DisplayName", "Enable Soundcloud Detection" },
                { "Description", "Should the app detect Soundcloud?" },
                { "Visibility", "Shown" },
                { "Type", "bool" }  
            };

            public static Dictionary<string, object> SettingsDictionary = new Dictionary<string, object> {
                { "CategoryLogging", CategoryLogging },
                { "LoggingWriteEnabled", LoggingWriteEnabled },
                { "DisableClearingLogs", DisableClearingLogs },
                { "CategoryRichPresence", CategoryRichPresence },
                { "EnableDiscordRPC", EnableDiscordRPC },
                { "EnableListeningToButton", EnableListeningToButton },
                { "ShowcaseDataToRPC", ShowcaseDataToRPC },
                { "ShowPlayingStatus", ShowPlayingStatus },
                { "ShowAppWatermark", ShowAppWatermark },
                { "CategoryListeningData", CategoryListeningData },
                { "EnableListeningData", EnableListeningData },
                { "CategoryYouTubeMusic", CategoryYouTubeMusic },
                { "EnableYouTubeMusic", EnableYouTubeMusic },
                { "CategorySoundcloud", CategorySoundcloud },
                { "EnableSoundcloud", EnableSoundcloud }
            };
        }

        public class SettingsData
        {
            public bool CategoryLogging { get; set; } = true;
            public bool LoggingWriteEnabled { get; set; } = false;
            public bool DisableClearingLogs { get; set; } = false;
            public bool CategoryRichPresence { get; set; } = true;
            public bool EnableDiscordRPC { get; set; } = true;
            public bool EnableListeningToButton { get; set; } = true;
            public bool ShowcaseDataToRPC { get; set; } = false;
            public bool ShowPlayingStatus { get; set; } = false;
            public bool ShowAppWatermark { get; set; } = false;
            public bool CategoryListeningData { get; set; } = true;
            public bool EnableListeningData { get; set; } = true;
            public bool CategoryYouTubeMusic { get; set; } = true;
            public bool EnableYouTubeMusic { get; set; } = true;
            public bool CategorySoundcloud { get; set; } = true;
            public bool EnableSoundcloud { get; set; } = true;
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