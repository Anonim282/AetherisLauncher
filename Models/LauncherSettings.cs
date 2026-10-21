using System;
using System.IO;

namespace AetherisLauncher.Models
{
    public enum ProxyMode
    {
        Direct,
        SystemProxy,
        CustomProxy
    }

    public class LauncherSettings
    {
        public string? GameRootDirectory { get; set; }
        public bool IsFirstLaunchCompleted { get; set; } = false;

        public ProxyMode ProxyMode { get; set; } = ProxyMode.SystemProxy;
        public string CustomProxyHost { get; set; } = "127.0.0.1";
        public int CustomProxyPort { get; set; } = 7890;
        public bool UseBmclapiMirror { get; set; } = true;
        public int DefaultMemoryMb { get; set; } = 4096;
        public string? SelectedJavaPath { get; set; }
        public string? ActiveAccountId { get; set; }

        public string GetEffectiveGameRootDirectory()
        {
            if (!string.IsNullOrWhiteSpace(GameRootDirectory))
            {
                try
                {
                    Directory.CreateDirectory(GameRootDirectory);
                    return GameRootDirectory;
                }
                catch { }
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var defaultDir = Path.Combine(appData, ".aetheris");
            Directory.CreateDirectory(defaultDir);
            return defaultDir;
        }

        public static string GetConfigDirectory()
        {
            // If portable config directory exists next to the executable, use it
            var portableDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
            if (Directory.Exists(portableDir) && File.Exists(Path.Combine(portableDir, "launcher_settings.json")))
            {
                return portableDir;
            }

            // Otherwise, store global launcher config in %APPDATA%/.aetheris/config
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var globalConfigDir = Path.Combine(appData, ".aetheris", "config");
            Directory.CreateDirectory(globalConfigDir);
            return globalConfigDir;
        }

        public static string GetConfigFilePath()
        {
            var dir = GetConfigDirectory();
            return Path.Combine(dir, "launcher_settings.json");
        }
    }
}
