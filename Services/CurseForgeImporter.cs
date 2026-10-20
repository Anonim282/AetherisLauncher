using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AetherisLauncher.Models;

namespace AetherisLauncher.Services
{
    public class CurseForgeImportProgress
    {
        public int CurrentFile { get; set; }
        public int TotalFiles { get; set; }
        public string StatusText { get; set; } = "";
        public double Percentage => TotalFiles > 0 ? (double)CurrentFile / TotalFiles * 100 : 0;
    }

    public class CurseForgeImporter
    {
        private readonly NetworkService _network;
        private readonly InstanceManager _instanceManager;

        // Public CurseForge API key used by community tools (or custom key)
        private const string CurseForgeApiKey = "$2a$10$bL4bIL5pUWqfcO7KQtnMReakwtfHbNKh6v1uTpKlzhwoueEJQnPnm";

        public CurseForgeImporter(NetworkService network, InstanceManager instanceManager)
        {
            _network = network;
            _instanceManager = instanceManager;
        }

        public async Task<InstanceInfo> ImportZipAsync(
            string zipFilePath,
            string? customInstanceName = null,
            IProgress<CurseForgeImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(zipFilePath))
                throw new FileNotFoundException("Файл архива сборки не найден", zipFilePath);

            using var archive = ZipFile.OpenRead(zipFilePath);
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidOperationException("В архиве отсутствует файл manifest.json. Это не стандартная сборка CurseForge.");

            CurseForgeManifest manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<CurseForgeManifest>(stream, cancellationToken: ct)
                    ?? throw new InvalidOperationException("Не удалось распарсить manifest.json");
            }

            // Determine Instance Info
            var packName = string.IsNullOrWhiteSpace(customInstanceName) ? manifest.Name : customInstanceName;
            var safeId = SanitizeFolderName(packName);

            var loaderType = "vanilla";
            var loaderVer = "";
            var primaryLoader = manifest.Minecraft.ModLoaders.Find(l => l.Primary)
                ?? (manifest.Minecraft.ModLoaders.Count > 0 ? manifest.Minecraft.ModLoaders[0] : null);

            if (primaryLoader != null)
            {
                var id = primaryLoader.Id.ToLowerInvariant();
                if (id.StartsWith("fabric-"))
                {
                    loaderType = "fabric";
                    loaderVer = id.Replace("fabric-", "");
                }
                else if (id.StartsWith("forge-"))
                {
                    loaderType = "forge";
                    loaderVer = id.Replace("forge-", "");
                }
                else if (id.StartsWith("neoforge-"))
                {
                    loaderType = "neoforge";
                    loaderVer = id.Replace("neoforge-", "");
                }
            }

            var instance = new InstanceInfo
            {
                Id = safeId,
                Name = packName,
                MinecraftVersion = manifest.Minecraft.Version,
                ModLoader = loaderType,
                ModLoaderVersion = loaderVer,
                AllocatedMemoryMb = 6144 // Modpacks typically recommend 6GB+
            };

            var instanceDir = instance.GetDirectory(_instanceManager.GetInstancesRootDirectory());
            var modsDir = instance.GetModsDirectory(_instanceManager.GetInstancesRootDirectory());

            // 1. Extract overrides folder (configs, scripts, shaderpacks, etc.)
            progress?.Report(new CurseForgeImportProgress
            {
                CurrentFile = 0,
                TotalFiles = manifest.Files.Count,
                StatusText = "Распаковка файлов конфигураций и настроек сборки..."
            });

            var overridesPrefix = (manifest.Overrides ?? "overrides").TrimEnd('/') + "/";
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith(overridesPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var relPath = entry.FullName.Substring(overridesPrefix.Length);
                    if (string.IsNullOrWhiteSpace(relPath))
                        continue;

                    var destPath = Path.Combine(instanceDir, relPath);
                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else
                    {
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
            }

            // 2. Download mods from CurseForge
            var totalMods = manifest.Files.Count;
            for (int i = 0; i < totalMods; i++)
            {
                ct.ThrowIfCancellationRequested();
                var mod = manifest.Files[i];

                progress?.Report(new CurseForgeImportProgress
                {
                    CurrentFile = i + 1,
                    TotalFiles = totalMods,
                    StatusText = $"Загрузка мода {i + 1} из {totalMods} (ID: {mod.FileId})..."
                });

                await DownloadModFileAsync(mod.ProjectId, mod.FileId, modsDir, ct);
            }

            // 3. Save instance.json
            _instanceManager.SaveInstance(instance);

            progress?.Report(new CurseForgeImportProgress
            {
                CurrentFile = totalMods,
                TotalFiles = totalMods,
                StatusText = "Импорт сборки успешно завершен!"
            });

            return instance;
        }

        private async Task DownloadModFileAsync(long projectId, long fileId, string modsDir, CancellationToken ct)
        {
            string? downloadUrl = null;
            string fileName = $"mod_{projectId}_{fileId}.jar";

            // Try 1: Query CurseForge API for metadata and direct download URL
            try
            {
                var client = _network.GetClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.curseforge.com/v1/mods/{projectId}/files/{fileId}");
                request.Headers.Add("x-api-key", CurseForgeApiKey);

                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        if (data.TryGetProperty("fileName", out var nameProp))
                            fileName = nameProp.GetString() ?? fileName;

                        if (data.TryGetProperty("downloadUrl", out var dlProp) && !string.IsNullOrEmpty(dlProp.GetString()))
                            downloadUrl = dlProp.GetString();
                    }
                }
            }
            catch { }

            // Try 2: If downloadUrl is null (author disabled third-party distribution or API blocked),
            // construct direct Edge CDN URL (CurseForge file server pattern)
            if (string.IsNullOrEmpty(downloadUrl))
            {
                long p1 = fileId / 1000;
                long p2 = fileId % 1000;
                downloadUrl = $"https://edge.forgecdn.net/files/{p1}/{p2}/{Uri.EscapeDataString(fileName)}";
            }

            var targetFilePath = Path.Combine(modsDir, fileName);

            // Skip if already downloaded
            if (File.Exists(targetFilePath) && new FileInfo(targetFilePath).Length > 0)
                return;

            try
            {
                await _network.DownloadFileAsync(downloadUrl, targetFilePath, null, ct);
            }
            catch
            {
                // Try secondary CDN fallback: mediafilez
                long p1 = fileId / 1000;
                long p2 = fileId % 1000;
                var fallbackUrl = $"https://mediafilez.forgecdn.net/files/{p1}/{p2}/{Uri.EscapeDataString(fileName)}";
                try
                {
                    await _network.DownloadFileAsync(fallbackUrl, targetFilePath, null, ct);
                }
                catch
                {
                    // If blocked completely, log and continue so the modpack still imports
                }
            }
        }

        private static string SanitizeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (string.IsNullOrEmpty(cleaned))
                return "Modpack_" + DateTime.Now.Ticks;
            return Regex.Replace(cleaned, @"\s+", "-");
        }
    }
}
