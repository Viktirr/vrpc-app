using VRPC.Configuration;

namespace VRPC.ListeningDataManager
{
    class ListeningDataBackup : ListeningData
    {
        public static void CreateBackup()
        {
            string sourcePath = VRPCSettings.ListeningDataPath;
            string backupPath = VRPCSettings.ListeningDataBackupPath;
            File.Copy(sourcePath, backupPath, overwrite: true);
        }
    }
}