using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AetherisLauncher.Services
{
    public class JavaRuntimeService
    {
        private readonly NetworkService _network;

        public JavaRuntimeService(NetworkService network)
        {
            _network = network;
        }

        public static int GetRequiredJavaMajorVersion(string mcVersion)
        {
            if (TryExtractVersionNumber(mcVersion, out int major, out int minor, out int patch))
            {
                if (minor <= 16)
                    return 8;
                if (minor == 17)
                    return 17;
                if (minor <= 20 && (minor < 20 || patch < 5))
                    return 17;
                return 21;
            }
            return 21;
        }

        public async Task<string> EnsureJavaAsync(
            string mcVersion,
            string gameRootDir,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            int targetMajor = GetRequiredJavaMajorVersion(mcVersion);
            var runtimeBaseDir = Path.Combine(gameRootDir, "runtime", $"java-{targetMajor}");

            // 1. Check local portable runtime first
            if (Directory.Exists(runtimeBaseDir))
            {
                var existingJava = FindJavaExecutable(runtimeBaseDir);
                if (File.Exists(existingJava))
                {
                    return existingJava;
                }
            }

            // 2. Check installed system Java
            var systemJavas = JavaDetector.FindInstalledJavas();
            var exactSystemJava = systemJavas.FirstOrDefault(j => j.MajorVersion == targetMajor);
            if (exactSystemJava != null && File.Exists(exactSystemJava.Path))
            {
                return exactSystemJava.Path;
            }

            // 3. Download portable OpenJDK JRE
            Directory.CreateDirectory(runtimeBaseDir);
            progress?.Report($"[Aetheris] Требуется Java {targetMajor} для Minecraft {mcVersion}. Начинается автоматическая загрузка...");

            string downloadUrl = targetMajor switch
            {
                8 => "https://github.com/adoptium/temurin8-binaries/releases/download/jdk8u442-b06/OpenJDK8U-jre_x64_windows_hotspot_8u442b06.zip",
                17 => "https://github.com/adoptium/temurin17-binaries/releases/download/jdk-17.0.14%2B7/OpenJDK17U-jre_x64_windows_hotspot_17.0.14_7.zip",
                _ => "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.6%2B7/OpenJDK21U-jre_x64_windows_hotspot_21.0.6_7.zip"
            };

            var tempZip = Path.Combine(gameRootDir, "runtime", $"temp_java_{targetMajor}_{Guid.NewGuid():N}.zip");
            try
            {
                var dlProgress = new Progress<(long downloaded, long total, string status)>(p =>
                {
                    if (p.total > 0)
                    {
                        var pct = (double)p.downloaded / p.total * 100.0;
                        progress?.Report($"[Aetheris] Скачивание Java {targetMajor}: {pct:F0}% ({p.downloaded / 1024 / 1024}MB / {p.total / 1024 / 1024}MB)");
                    }
                });

                await _network.DownloadFileAsync(downloadUrl, tempZip, dlProgress, ct);

                progress?.Report($"[Aetheris] Распаковка Java {targetMajor}...");
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(tempZip);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                            continue; // Directory entry

                        // Remove root wrapper folder from zip paths
                        var relativePath = entry.FullName;
                        var firstSlash = relativePath.IndexOfAny(new[] { '/', '\\' });
                        if (firstSlash >= 0)
                        {
                            relativePath = relativePath.Substring(firstSlash + 1);
                        }

                        var destPath = Path.Combine(runtimeBaseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);

                        entry.ExtractToFile(destPath, true);
                    }
                }, ct);

                var resolved = FindJavaExecutable(runtimeBaseDir);
                if (File.Exists(resolved))
                {
                    progress?.Report($"[Aetheris] Java {targetMajor} успешно установлена и готова к работе!");
                    return resolved;
                }

                throw new InvalidOperationException($"Не удалось обнаружить java.exe после распаковки в {runtimeBaseDir}");
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { }
                }
            }
        }

        private static string? FindJavaExecutable(string rootDir)
        {
            if (!Directory.Exists(rootDir))
                return null;

            var directBin = Path.Combine(rootDir, "bin", "java.exe");
            if (File.Exists(directBin))
                return directBin;

            var matches = Directory.GetFiles(rootDir, "java.exe", SearchOption.AllDirectories);
            return matches.FirstOrDefault(p => p.EndsWith(@"bin\java.exe", StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
        }

        private static bool TryExtractVersionNumber(string mcVersion, out int major, out int minor, out int patch)
        {
            major = 1;
            minor = 20;
            patch = 0;
            var parts = mcVersion.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor))
            {
                if (parts.Length >= 3)
                    int.TryParse(parts[2], out patch);
                return true;
            }
            return false;
        }
    }
}
