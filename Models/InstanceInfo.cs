using System;
using System.IO;

namespace AetherisLauncher.Models
{
    public class InstanceInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "Minecraft Instance";
        public string MinecraftVersion { get; set; } = "1.20.1";
        public string ModLoader { get; set; } = "fabric"; // "vanilla", "fabric", "forge", "neoforge"
        public string ModLoaderVersion { get; set; } = "";
        public string? IconPath { get; set; }
        public string? CustomJavaPath { get; set; }
        public int AllocatedMemoryMb { get; set; } = 4096;
        public string CustomJvmArgs { get; set; } = "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastPlayed { get; set; }
        public int TotalPlayedMinutes { get; set; }

        public string GetDirectory(string rootPath)
        {
            var baseDir = Path.GetFileName(rootPath).Equals("instances", StringComparison.OrdinalIgnoreCase)
                ? rootPath
                : Path.Combine(rootPath, "instances");
            var dir = Path.Combine(baseDir, Id);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string GetModsDirectory(string rootPath)
        {
            var dir = Path.Combine(GetDirectory(rootPath), "mods");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string GetConfigDirectory(string rootPath)
        {
            var dir = Path.Combine(GetDirectory(rootPath), "config");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
