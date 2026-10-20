using System.Linq;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AetherisLauncher.Services
{
    public class DirectoryMigrationService
    {
        private static readonly string[] FoldersToMigrate = new[]
        {
            "instances",
            "assets",
            "libraries",
            "versions",
            "runtime",
            "natives"
        };

        public static bool HasDataToMigrate(string sourceDir)
        {
            if (!Directory.Exists(sourceDir))
                return false;

            foreach (var folder in FoldersToMigrate)
            {
                var path = Path.Combine(sourceDir, folder);
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                    return true;
            }
            return false;
        }

        public static async Task MigrateGameDirectoryAsync(
            string sourceDir,
            string targetDir,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(targetDir);

            foreach (var folderName in FoldersToMigrate)
            {
                var srcFolder = Path.Combine(sourceDir, folderName);
                if (!Directory.Exists(srcFolder))
                    continue;

                var dstFolder = Path.Combine(targetDir, folderName);
                Directory.CreateDirectory(dstFolder);

                progress?.Report($"Перемещение папки {folderName}...");
                await MoveDirectoryRecursiveAsync(srcFolder, dstFolder, progress, ct);
            }
        }

        private static async Task MoveDirectoryRecursiveAsync(
            string src,
            string dst,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            Directory.CreateDirectory(dst);

            // Move files
            foreach (var file in Directory.GetFiles(src))
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(dst, fileName);

                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }

                try
                {
                    File.Move(file, destFile);
                }
                catch
                {
                    // Fallback to copy & delete across different drives
                    await Task.Run(() =>
                    {
                        File.Copy(file, destFile, true);
                        File.Delete(file);
                    }, ct);
                }
            }

            // Move subdirectories
            foreach (var dir in Directory.GetDirectories(src))
            {
                ct.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(dir);
                var destSubDir = Path.Combine(dst, dirName);
                await MoveDirectoryRecursiveAsync(dir, destSubDir, progress, ct);
                
                try
                {
                    Directory.Delete(dir, false);
                }
                catch { }
            }
        }
    }
}
