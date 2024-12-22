using System;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DiscordRPC;
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

        public static void checkIfApplicationDataFolderExists()
        {
            try
            {
                Directory.CreateDirectory(UserAppDataPath);
                log.Write("Application data directory created.");
            } catch (Exception e)
            {
                Console.WriteLine($"FATAL: Application data directory was not able to be created. Quitting! Exception {e.Data}");
                log.Write($"FATAL: Application data directory was not able to be created. Quitting! Exception {e.Data}");
                Environment.Exit(1);
            }
        }

        public class SettingsData
        {
            public bool LoggingWriteEnabled { get; set; } = false;
            public bool DisableClearingLogs { get; set; } = true;
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

        public static void checkSettings()
        {
            settingsData = ReadConfigFile();
            SaveConfigFile(settingsData);
        }
    }
}