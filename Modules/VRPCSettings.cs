using System.Text;
using System.Text.Json;
using VRPC.Logging;

namespace VRPC.Configuration
{
    class VRPCSettings
    {
        private static string pathAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string UserAppDataPath = Path.Combine(pathAppData, "VRPCApp");
        public static string RPCInfoPath = Path.Combine(UserAppDataPath, "RPCInfo.txt");
        public static string ListeningDataPath = Path.Combine(UserAppDataPath, "ListeningData.json");
        public static string ConfigPath = Path.Combine(UserAppDataPath, "Config.json");
        public static SettingsData settingsData = new SettingsData();

        public static string LogPath = Path.Combine(UserAppDataPath, "Log.txt");
        public static Log log = new Log();

        public static void CheckIfApplicationDataFolderExists()
        {
            try
            {
                Directory.CreateDirectory(UserAppDataPath);
                log.Write("Application data directory created.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"FATAL: Application data directory was not able to be created. Quitting! Exception {e.Data}");
                log.Write($"FATAL: Application data directory was not able to be created. Quitting! Exception {e.Data}");
                Environment.Exit(1);
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
                { "Description", "Enable or disable Discord RPC" },
                { "Visibility", "Shown" },
                { "Type", "bool" }
            };

            public static Dictionary<string, string> EnableListeningData { get; } = new Dictionary<string, string> {
                { "InternalName", "EnableListeningData" },
                { "DisplayName", "Enable Listening Data" },
                { "Description", "Enable or disable listening data" },
                { "Visibility", "Shown" },
                { "Type", "bool" }
            };

            public static Dictionary<string, object> SettingsDictionary = new Dictionary<string, object> {
                { "LoggingWriteEnabled", LoggingWriteEnabled },
                { "DisableClearingLogs", DisableClearingLogs },
                { "EnableDiscordRPC", EnableDiscordRPC },
                { "EnableListeningData", EnableListeningData }
            };
        }

        public class SettingsData
        {
            public bool LoggingWriteEnabled { get; set; } = false;
            public bool DisableClearingLogs { get; set; } = true;
            public bool EnableDiscordRPC { get; set; } = true;
            public bool EnableListeningData { get; set; } = true;
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