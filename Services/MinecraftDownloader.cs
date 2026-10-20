using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AetherisLauncher.Services
{
    public class VersionManifestItem
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public string url { get; set; } = "";
    }

    public class VersionManifestV2
    {
        public List<VersionManifestItem> versions { get; set; } = new();
    }

    public class MinecraftDownloader
    {
        private readonly NetworkService _network;
        private readonly string _sharedRootDir;

        public MinecraftDownloader(NetworkService network, string? sharedRootDir = null)
        {
            _network = network;
            _sharedRootDir = sharedRootDir ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        public string GetVersionsDirectory() => Path.Combine(_sharedRootDir, "versions");
        public string GetLibrariesDirectory() => Path.Combine(_sharedRootDir, "libraries");
        public string GetAssetsDirectory() => Path.Combine(_sharedRootDir, "assets");

        public async Task<string> EnsureVanillaVersionAsync(
            string versionId,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var versionDir = Path.Combine(GetVersionsDirectory(), versionId);
            var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
            var clientJarPath = Path.Combine(versionDir, $"{versionId}.jar");

            Directory.CreateDirectory(versionDir);

            // 1. Fetch version JSON if not present
            if (!File.Exists(versionJsonPath) || new FileInfo(versionJsonPath).Length == 0)
            {
                progress?.Report($"[Aetheris] Загрузка манифеста Minecraft {versionId}...");
                try
                {
                    var mirrorJsonUrl = $"https://bmclapi2.bangbang93.com/version/{versionId}/json";
                    await _network.DownloadFileAsync(mirrorJsonUrl, versionJsonPath, null, ct);
                }
                catch
                {
                    var manifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
                    var manifestClient = _network.GetClient();
                    var manifestJson = await manifestClient.GetStringAsync(_network.ResolveUrl(manifestUrl), ct);
                    var vManifest = JsonSerializer.Deserialize<VersionManifestV2>(manifestJson);

                    var targetVer = vManifest?.versions.Find(v => v.id.Equals(versionId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Версия Minecraft {versionId} не найдена в манифесте Mojang");

                    await _network.DownloadFileAsync(targetVer.url, versionJsonPath, null, ct);
                }
            }

            var verJsonContent = await File.ReadAllTextAsync(versionJsonPath, ct);
            using var verDoc = JsonDocument.Parse(verJsonContent);
            var root = verDoc.RootElement;

            // 2. Download client.jar
            if (!File.Exists(clientJarPath) || new FileInfo(clientJarPath).Length == 0)
            {
                progress?.Report($"[Aetheris] Загрузка чистого клиента {versionId}.jar...");
                string? clientUrl = null;

                if (root.TryGetProperty("downloads", out var downloads) &&
                    downloads.TryGetProperty("client", out var clientObj) &&
                    clientObj.TryGetProperty("url", out var clientUrlProp))
                {
                    clientUrl = clientUrlProp.GetString();
                }

                // Use BMCLAPI direct endpoint for reliable high-speed client download without ISP throttling
                clientUrl = $"https://bmclapi2.bangbang93.com/version/{versionId}/client";

                await _network.DownloadFileAsync(clientUrl, clientJarPath, null, ct);
            }

            // 3. Download vanilla libraries
            if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
            {
                var libCount = libraries.GetArrayLength();
                int idx = 0;
                foreach (var lib in libraries.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    idx++;

                    if (lib.TryGetProperty("rules", out var rules) && !IsAllowedByRules(rules))
                        continue;

                    if (lib.TryGetProperty("downloads", out var libDownloads) &&
                        libDownloads.TryGetProperty("artifact", out var artifact))
                    {
                        if (artifact.TryGetProperty("path", out var pathProp) &&
                            artifact.TryGetProperty("url", out var urlProp))
                        {
                            var relPath = pathProp.GetString();
                            var url = urlProp.GetString();
                            if (!string.IsNullOrEmpty(relPath) && !string.IsNullOrEmpty(url))
                            {
                                var fullLibPath = Path.Combine(GetLibrariesDirectory(), relPath.Replace('/', Path.DirectorySeparatorChar));
                                if (!File.Exists(fullLibPath) || new FileInfo(fullLibPath).Length == 0)
                                {
                                    progress?.Report($"[Vanilla Lib] ({idx}/{libCount}) {Path.GetFileName(relPath)}");
                                    try
                                    {
                                        await _network.DownloadFileAsync(url, fullLibPath, null, ct);
                                    }
                                    catch (Exception ex)
                                    {
                                        progress?.Report($"[Предупреждение] Не удалось загрузить {Path.GetFileName(relPath)}: {ex.Message}. Повтор...");
                                        try
                                        {
                                            await Task.Delay(500, ct);
                                            await _network.DownloadFileAsync(url, fullLibPath, null, ct);
                                        }
                                        catch
                                        {
                                            // Non-fatal if single secondary library fails
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 4. Download asset index and assets
            if (root.TryGetProperty("assetIndex", out var assetIndex) &&
                assetIndex.TryGetProperty("url", out var assetIndexUrlProp) &&
                assetIndex.TryGetProperty("id", out var assetIndexIdProp))
            {
                var assetIndexId = assetIndexIdProp.GetString()!;
                var indexFile = Path.Combine(GetAssetsDirectory(), "indexes", $"{assetIndexId}.json");
                if (!File.Exists(indexFile))
                {
                    progress?.Report($"[Aetheris] Загрузка индекса ресурсов {assetIndexId}...");
                    try
                    {
                        await _network.DownloadFileAsync(assetIndexUrlProp.GetString()!, indexFile, null, ct);
                    }
                    catch { }
                }

                await EnsureAssetsAsync(indexFile, progress, ct);
            }

            return versionJsonPath;
        }

        public async Task EnsureAssetsAsync(
            string indexFilePath,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(indexFilePath)) return;

            var assetsDir = GetAssetsDirectory();
            var objectsDir = Path.Combine(assetsDir, "objects");
            Directory.CreateDirectory(objectsDir);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexFilePath, ct));
            if (!doc.RootElement.TryGetProperty("objects", out var objectsProp)) return;

            var missingHashes = new List<string>();
            foreach (var prop in objectsProp.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("hash", out var hashProp))
                {
                    var hash = hashProp.GetString();
                    if (!string.IsNullOrEmpty(hash) && hash.Length >= 2)
                    {
                        var sub = hash[..2];
                        var filePath = Path.Combine(objectsDir, sub, hash);
                        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                        {
                            missingHashes.Add(hash);
                        }
                    }
                }
            }

            if (missingHashes.Count == 0) return;

            int total = missingHashes.Count;
            int completed = 0;
            progress?.Report($"[Aetheris] Загрузка ресурсов и языковых пакетов (0/{total})...");

            await Parallel.ForEachAsync(missingHashes, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (hash, token) =>
            {
                var sub = hash[..2];
                var filePath = Path.Combine(objectsDir, sub, hash);
                var url = $"https://resources.download.minecraft.net/{sub}/{hash}";

                try
                {
                    await _network.DownloadFileAsync(url, filePath, null, token);
                }
                catch
                {
                    // Continue with other assets if one fails
                }

                var count = Interlocked.Increment(ref completed);
                if (count % 100 == 0 || count == total)
                {
                    progress?.Report($"[Aetheris] Загрузка ресурсов и языковых пакетов ({count}/{total})...");
                }
            });
        }

        public async Task<string> EnsureNeoForgeVersionAsync(
            string gameVersion,
            string loaderVersion,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var neoId = $"neoforge-{loaderVersion}";
            var versionDir = Path.Combine(GetVersionsDirectory(), neoId);
            var versionJsonPath = Path.Combine(versionDir, $"{neoId}.json");
            Directory.CreateDirectory(versionDir);

            if (!File.Exists(versionJsonPath) || new FileInfo(versionJsonPath).Length == 0)
            {
                progress?.Report($"[NeoForge] Загрузка инсталлера NeoForge {loaderVersion}...");
                var installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
                var installerTemp = Path.Combine(versionDir, "installer.jar");

                await _network.DownloadFileAsync(installerUrl, installerTemp, null, ct);

                using (var archive = ZipFile.OpenRead(installerTemp))
                {
                    var entry = archive.GetEntry("version.json")
                        ?? throw new InvalidOperationException("version.json не найден внутри NeoForge installer.jar");
                    entry.ExtractToFile(versionJsonPath, overwrite: true);
                }

                try { File.Delete(installerTemp); } catch { }
            }

            var neoJson = await File.ReadAllTextAsync(versionJsonPath, ct);
            using var doc = JsonDocument.Parse(neoJson);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
            {
                var libCount = libraries.GetArrayLength();
                int idx = 0;
                foreach (var lib in libraries.EnumerateArray())
                {
                    idx++;
                    if (lib.TryGetProperty("downloads", out var dls) &&
                        dls.TryGetProperty("artifact", out var art) &&
                        art.TryGetProperty("path", out var pathProp) &&
                        art.TryGetProperty("url", out var urlProp))
                    {
                        var relPath = pathProp.GetString()!.Replace('/', Path.DirectorySeparatorChar);
                        var fullPath = Path.Combine(GetLibrariesDirectory(), relPath);
                        var url = urlProp.GetString();

                        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                        {
                            progress?.Report($"[NeoForge Lib] ({idx}/{libCount}) {Path.GetFileName(relPath)}");
                            if (!string.IsNullOrEmpty(url))
                            {
                                try
                                {
                                    await _network.DownloadFileAsync(url, fullPath, null, ct);
                                }
                                catch (Exception ex)
                                {
                                    progress?.Report($"[Предупреждение] Не удалось скачать {Path.GetFileName(relPath)}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }

            return versionJsonPath;
        }

        
        public async Task<string> EnsureForgeVersionAsync(
            string gameVersion,
            string loaderVersion,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var forgeId = $"forge-{gameVersion}-{loaderVersion}";
            var versionDir = Path.Combine(GetVersionsDirectory(), forgeId);
            var versionJsonPath = Path.Combine(versionDir, $"{forgeId}.json");
            Directory.CreateDirectory(versionDir);

            if (!File.Exists(versionJsonPath) || new FileInfo(versionJsonPath).Length == 0)
            {
                progress?.Report($"[Forge] Загрузка инсталлятора Forge {gameVersion}-{loaderVersion}...");
                var installerUrl = $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{gameVersion}-{loaderVersion}/forge-{gameVersion}-{loaderVersion}-installer.jar";
                var installerTemp = Path.Combine(versionDir, "installer.jar");

                await _network.DownloadFileAsync(installerUrl, installerTemp, null, ct);

                using (var archive = ZipFile.OpenRead(installerTemp))
                {
                    var entry = archive.GetEntry("version.json")
                        ?? throw new InvalidOperationException("version.json не найден внутри Forge installer.jar");
                    entry.ExtractToFile(versionJsonPath, overwrite: true);
                }
            }

            var forgeJson = await File.ReadAllTextAsync(versionJsonPath, ct);
            using var doc = JsonDocument.Parse(forgeJson);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
            {
                var libCount = libraries.GetArrayLength();
                int idx = 0;
                foreach (var lib in libraries.EnumerateArray())
                {
                    idx++;
                    if (lib.TryGetProperty("downloads", out var dls) &&
                        dls.TryGetProperty("artifact", out var art) &&
                        art.TryGetProperty("path", out var pathProp) &&
                        art.TryGetProperty("url", out var urlProp))
                    {
                        var relPath = pathProp.GetString()!.Replace('/', Path.DirectorySeparatorChar);
                        var fullPath = Path.Combine(GetLibrariesDirectory(), relPath);
                        var url = urlProp.GetString();

                        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                        {
                            progress?.Report($"[Forge Lib] ({idx}/{libCount}) {Path.GetFileName(relPath)}");
                            if (!string.IsNullOrEmpty(url))
                            {
                                try
                                {
                                    await _network.DownloadFileAsync(url, fullPath, null, ct);
                                }
                                catch (Exception ex)
                                {
                                    progress?.Report($"[Предупреждение] Не удалось скачать {Path.GetFileName(relPath)}: {ex.Message}");
                                }
                            }
                        }
                    }
                    else if (lib.TryGetProperty("name", out var nameProp))
                    {
                        var relPath = ConvertMavenNameToPath(nameProp.GetString()!);
                        var fullPath = Path.Combine(GetLibrariesDirectory(), relPath);
                        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                        {
                            var mavenUrl = "https://maven.minecraftforge.net/" + relPath.Replace('\\', '/');
                            try
                            {
                                await _network.DownloadFileAsync(mavenUrl, fullPath, null, ct);
                            }
                            catch { }
                        }
                    }
                }
            }

            return versionJsonPath;
        }

        public async Task<string> EnsureFabricVersionAsync(
            string gameVersion,
            string loaderVersion,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var fabricVersionId = $"fabric-loader-{loaderVersion}-{gameVersion}";
            var versionDir = Path.Combine(GetVersionsDirectory(), fabricVersionId);
            var versionJsonPath = Path.Combine(versionDir, $"{fabricVersionId}.json");
            Directory.CreateDirectory(versionDir);

            if (!File.Exists(versionJsonPath))
            {
                progress?.Report($"[Fabric] Загрузка манифеста Fabric Loader {loaderVersion} для {gameVersion}...");
                var fabricMetaUrl = $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}/{loaderVersion}/profile/json";
                await _network.DownloadFileAsync(fabricMetaUrl, versionJsonPath, null, ct);
            }

            var fabricJson = await File.ReadAllTextAsync(versionJsonPath, ct);
            using var doc = JsonDocument.Parse(fabricJson);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
            {
                foreach (var lib in libraries.EnumerateArray())
                {
                    if (lib.TryGetProperty("name", out var nameProp) && lib.TryGetProperty("url", out var urlProp))
                    {
                        var name = nameProp.GetString()!;
                        var baseUrl = urlProp.GetString()!;
                        var libRelPath = ConvertMavenNameToPath(name);
                        var libFullPath = Path.Combine(GetLibrariesDirectory(), libRelPath);

                        if (!File.Exists(libFullPath) || new FileInfo(libFullPath).Length == 0)
                        {
                            var downloadUrl = baseUrl.TrimEnd('/') + "/" + libRelPath.Replace('\\', '/');
                            progress?.Report($"[Fabric Lib] {Path.GetFileName(libRelPath)}");
                            try
                            {
                                await _network.DownloadFileAsync(downloadUrl, libFullPath, null, ct);
                            }
                            catch { }
                        }
                    }
                }
            }

            return versionJsonPath;
        }

        private static bool IsAllowedByRules(JsonElement rules)
        {
            bool allowed = false;
            foreach (var rule in rules.EnumerateArray())
            {
                var action = rule.GetProperty("action").GetString();
                bool isMatch = true;

                if (rule.TryGetProperty("os", out var os))
                {
                    if (os.TryGetProperty("name", out var osName))
                    {
                        isMatch = osName.GetString() == "windows";
                    }
                }

                if (isMatch)
                {
                    allowed = (action == "allow");
                }
            }
            return allowed;
        }

        public static string ConvertMavenNameToPath(string mavenName)
        {
            var parts = mavenName.Split(':');
            var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
            var artifact = parts[1];
            var versionAndRest = parts[2];

            string version = versionAndRest;
            string extension = "jar";

            if (version.Contains('@'))
            {
                var extParts = version.Split('@');
                version = extParts[0];
                extension = extParts[1];
            }

            return Path.Combine(group, artifact, version, $"{artifact}-{version}.{extension}");
        }
    }
}
