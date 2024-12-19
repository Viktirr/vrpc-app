using System;
using VRPC.Logging;

namespace VRPC.Configuration
{
    class VRPCSettings
    {
        private static string pathAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string UserAppDataPath = Path.Combine(pathAppData, "VRPCApp");
        public static string RPCInfoPath = Path.Combine(UserAppDataPath, "RPCInfo.txt");
        public static string ListeningDataPath = Path.Combine(UserAppDataPath, "ListeningData.json");

        public static string LogPath = Path.Combine(UserAppDataPath, "Log.txt");
        public static bool LoggingWriteEnabled = false;

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
    }
}