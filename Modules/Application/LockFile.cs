using System.Diagnostics;
using System.Text;

namespace VRPC.Lock
{
    public class Lock
    {
        private string _lockFilePath;
        private string _lockFileName = ".lock";
        private string _lockFilePathFinal;
        private FileStream? _lockFileStream;
        private StreamWriter? lockFile;

        public Lock(string lockFileName = ".lock")
        {
            string roamingAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appNamePath = Path.Combine(roamingAppDataPath, "VRPCApp");

            _lockFilePath = appNamePath;
            _lockFileName = lockFileName;

            _lockFilePathFinal = Path.Combine(appNamePath, lockFileName);
        }

        private int GetApplicationPID()
        {
            return Process.GetCurrentProcess().Id;
        }

        // Intentionally not closing the file so it's "locked" in a way.
        // The whole purpose is to have only one instance of the application open at a time to avoid conflicts.
        public void Create()
        {
            if (CheckLockFileExist())
            {
                Environment.Exit(-1);
            }
            else
            {
                _lockFileStream = new FileStream(_lockFilePathFinal, FileMode.Create);
                lockFile = new StreamWriter(_lockFileStream, Encoding.UTF8, -1, true);
                lockFile.Write(GetApplicationPID());
                lockFile.Flush();
            }

        }

        // Checks if a lock file exists and if the file is locked
        bool CheckLockFileExist()
        {
            if (File.Exists(_lockFilePathFinal))
            {
                try
                {
                    string pidString = File.ReadAllText(_lockFilePathFinal).Trim();

                    if (pidString.Length == 0)
                    {
                        return false;
                    }
                    else
                    {
                        int.TryParse(pidString, out int pid);
                        Process process;
                        try
                        {
                            process = Process.GetProcessById(pid);
                        }
                        catch
                        {
                            return false;
                        }

                        if (process.ProcessName == "Lock")
                        {
                            return true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
                catch
                {
                    return true;
                }
            }
            else
            {
                return false;
            }
        }

        public void RemoveLock()
        {
            if (_lockFileStream != null)
            {
                _lockFileStream.Close();
                _lockFileStream.Dispose();
                File.Delete(_lockFilePathFinal);
            }
        }
    }
}