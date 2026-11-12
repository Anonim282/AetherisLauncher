using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AetherisLauncher.Models;

namespace AetherisLauncher.Services
{
    public class ModrinthImporter
    {
        private readonly NetworkService _network;
        private readonly InstanceManager _instanceManager;

        public ModrinthImporter(NetworkService network, InstanceManager instanceManager)
        {
            _network = network;
            _instanceManager = instanceManager;
        }

        public static bool IsModrinthPack(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (filePath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                return zip.GetEntry("modrinth.index.json") != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<InstanceInfo> ImportMrpackAsync(
            string mrpackPath,
            string? customInstanceName = null,
            IProgress<ArchiveImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(mrpackPath))
                throw new FileNotFoundException("Файл модпака .mrpack не найден", mrpackPath);

            using var archive = ZipFile.OpenRead(mrpackPath);
            var indexEntry = archive.GetEntry("modrinth.index.json")
                ?? throw new InvalidOperationException("В архиве отсутствует modrinth.index.json. Это не корректный Modrinth модпак (.mrpack).");

            using var indexStream = indexEntry.Open();
            using var doc = await JsonDocument.ParseAsync(indexStream, cancellationToken: ct);
            var root = doc.RootElement;

            var packName = !string.IsNullOrWhiteSpace(customInstanceName)
                ? customInstanceName.Trim()
                : (root.TryGetProperty("name", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString())
                    ? nProp.GetString()!.Trim()
                    : Path.GetFileNameWithoutExtension(mrpackPath));

            string mcVersion = "1.20.1";
            string loaderType = "vanilla";
            string loaderVer = "";

            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                if (deps.TryGetProperty("minecraft", out var mcProp) && !string.IsNullOrWhiteSpace(mcProp.GetString()))
                    mcVersion = mcProp.GetString()!.Trim();

                if (deps.TryGetProperty("neoforge", out var neoProp) && !string.IsNullOrWhiteSpace(neoProp.GetString()))
                {
                    loaderType = "neoforge";
                    loaderVer = neoProp.GetString()!.Trim();
                }
                else if (deps.TryGetProperty("forge", out var forgeProp) && !string.IsNullOrWhiteSpace(forgeProp.GetString()))
                {
                    loaderType = "forge";
                    loaderVer = forgeProp.GetString()!.Trim();
                }
                else if (deps.TryGetProperty("fabric-loader", out var fabProp) && !string.IsNullOrWhiteSpace(fabProp.GetString()))
                {
                    loaderType = "fabric";
                    loaderVer = fabProp.GetString()!.Trim();
                }
            }

            var instance = new InstanceInfo
            {
                Id = GetUniqueInstanceId(SanitizeFolderName(packName)),
                Name = packName,
                MinecraftVersion = mcVersion,
                ModLoader = loaderType,
                ModLoaderVersion = loaderVer,
                AllocatedMemoryMb = 6144
            };

            var instanceDir = instance.GetDirectory(_instanceManager.GetInstancesRootDirectory());
            var fullInstanceDir = Path.GetFullPath(instanceDir);
            if (!fullInstanceDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                fullInstanceDir += Path.DirectorySeparatorChar;

            // Collect client files to download
            var filesToDownload = new List<(string RelPath, List<string> Urls)>();
            if (root.TryGetProperty("files", out var filesArr) && filesArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in filesArr.EnumerateArray())
                {
                    if (item.TryGetProperty("env", out var envProp) &&
                        envProp.ValueKind == JsonValueKind.Object &&
                        envProp.TryGetProperty("client", out var clientEnv) &&
                        string.Equals(clientEnv.GetString(), "unsupported", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relPath = item.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(relPath))
                        continue;

                    var urls = new List<string>();
                    if (item.TryGetProperty("downloads", out var dlArr) && dlArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var u in dlArr.EnumerateArray())
                        {
                            var url = u.GetString();
                            if (!string.IsNullOrWhiteSpace(url))
                                urls.Add(url);
                        }
                    }

                    if (urls.Count > 0)
                        filesToDownload.Add((relPath, urls));
                }
            }

            int total = filesToDownload.Count;
            progress?.Report(new ArchiveImportProgress
            {
                CurrentFile = 0,
                TotalFiles = Math.Max(total, 1),
                StatusText = "Распаковка overrides Modrinth..."
            });

            // 1. Extract overrides/ and client-overrides/
            string[] overridePrefixes = { "overrides/", "client-overrides/" };
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith("/"))
                    continue;

                var normalized = entry.FullName.Replace('\\', '/');
                var matchedPrefix = overridePrefixes.FirstOrDefault(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (matchedPrefix == null)
                    continue;

                var relPath = normalized.Substring(matchedPrefix.Length).TrimStart('/');
                if (string.IsNullOrWhiteSpace(relPath))
                    continue;

                var destPath = Path.GetFullPath(Path.Combine(fullInstanceDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destPath.StartsWith(fullInstanceDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                entry.ExtractToFile(destPath, overwrite: true);
            }

            // 2. Download mods/resources
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (relPath, urls) = filesToDownload[i];
                var fileName = Path.GetFileName(relPath);

                progress?.Report(new ArchiveImportProgress
                {
                    CurrentFile = i + 1,
                    TotalFiles = total,
                    StatusText = $"Загрузка файла {i + 1} из {total}: {fileName}..."
                });

                var destPath = Path.GetFullPath(Path.Combine(fullInstanceDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destPath.StartsWith(fullInstanceDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                    continue;

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                foreach (var url in urls)
                {
                    try
                    {
                        await _network.DownloadFileAsync(url, destPath, null, ct);
                        break;
                    }
                    catch
                    {
                        // Try next mirror URL if available
                    }
                }
            }

            _instanceManager.SaveInstance(instance);

            progress?.Report(new ArchiveImportProgress
            {
                CurrentFile = Math.Max(total, 1),
                TotalFiles = Math.Max(total, 1),
                StatusText = "Импорт Modrinth сборки успешно завершен!"
            });

            return instance;
        }

        private string GetUniqueInstanceId(string baseId)
        {
            var root = _instanceManager.GetInstancesRootDirectory();
            var candidate = baseId;
            int suffix = 2;
            while (Directory.Exists(Path.Combine(root, candidate)))
            {
                candidate = $"{baseId}-{suffix}";
                suffix++;
            }
            return candidate;
        }

        private static string SanitizeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (string.IsNullOrEmpty(cleaned))
                return "Modrinth_" + DateTime.Now.Ticks;
            return Regex.Replace(cleaned, @"\s+", "-");
        }
    }
}

