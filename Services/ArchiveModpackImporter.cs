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
using SharpCompress.Archives;

namespace AetherisLauncher.Services
{
    public class ArchiveImportProgress
    {
        public int CurrentFile { get; set; }
        public int TotalFiles { get; set; }
        public string StatusText { get; set; } = "";
        public double Percentage => TotalFiles > 0 ? (double)CurrentFile / TotalFiles * 100 : 0;
    }

    public class ArchiveScanResult
    {
        public string SuggestedName { get; set; } = "Imported Modpack";
        public string DetectedMinecraftVersion { get; set; } = "1.20.1";
        public string DetectedModLoader { get; set; } = "forge";
        public string DetectedModLoaderVersion { get; set; } = "";
        public int DetectedMemoryMb { get; set; } = 6144;
        public string RootPrefix { get; set; } = "";
        public int TotalFiles { get; set; }
        public int ModJarCount { get; set; }
        public bool HasConfig { get; set; }
        public bool HasResourcePacks { get; set; }
        public bool HasShaderPacks { get; set; }
        public bool HasSaves { get; set; }
        public bool DetectionConfident { get; set; }

        public string SummaryText
        {
            get
            {
                var parts = new List<string>
                {
                    $"Файлов в архиве: {TotalFiles}",
                    $"Модов (.jar): {ModJarCount}"
                };
                var extras = new List<string>();
                if (HasConfig) extras.Add("config");
                if (HasResourcePacks) extras.Add("resourcepacks");
                if (HasShaderPacks) extras.Add("shaderpacks");
                if (HasSaves) extras.Add("saves");
                if (extras.Count > 0)
                    parts.Add($"Папки: {string.Join(", ", extras)}");
                return string.Join(" | ", parts);
            }
        }
    }

    public class ArchiveModpackImporter
    {
        private readonly InstanceManager _instanceManager;

        private static readonly string[] RootMarkers =
        {
            "mods/",
            "config/",
            "versions/",
            "resourcepacks/",
            "shaderpacks/",
            "saves/",
            "libraries/",
            "options.txt",
            "instance.json",
            "mmc-pack.json",
            "minecraftinstance.json"
        };

        public ArchiveModpackImporter(InstanceManager instanceManager)
        {
            _instanceManager = instanceManager;
        }

        public static bool IsCurseForgeManifestZip(string filePath)
        {
            if (!filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                return false;

            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                bool hasManifest = zip.GetEntry("manifest.json") != null;
                bool hasModJars = zip.Entries.Any(e =>
                    e.FullName.Contains("mods/", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
                return hasManifest && !hasModJars;
            }
            catch
            {
                return false;
            }
        }

        public Task<ArchiveScanResult> ScanArchiveAsync(string archivePath, CancellationToken ct = default)
        {
            return Task.Run(() => ScanArchiveInternal(archivePath, ct), ct);
        }

        private ArchiveScanResult ScanArchiveInternal(string archivePath, CancellationToken ct)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Файл архива сборки не найден", archivePath);

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var fileEntries = archive.Entries
                .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => (Entry: e, Path: NormalizePath(e.Key!)))
                .Where(x => !string.IsNullOrEmpty(x.Path))
                .ToList();

            var rootPrefix = DetectRootPrefix(fileEntries.Select(x => x.Path).ToList());

            var result = new ArchiveScanResult
            {
                SuggestedName = Path.GetFileNameWithoutExtension(archivePath),
                RootPrefix = rootPrefix
            };

            // If wrapped in a named folder (not just ".minecraft/"), use that folder name if cleaner
            if (!string.IsNullOrEmpty(rootPrefix))
            {
                var trimmed = rootPrefix.TrimEnd('/');
                var firstSegment = trimmed.Split('/')[0];
                if (!firstSegment.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) &&
                    !firstSegment.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    result.SuggestedName = firstSegment;
                }
            }

            var relativeEntries = new List<(IArchiveEntry Entry, string RelPath)>();
            foreach (var item in fileEntries)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(rootPrefix) &&
                    !item.Path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rel = item.Path.Substring(rootPrefix.Length).TrimStart('/');
                if (string.IsNullOrEmpty(rel))
                    continue;

                relativeEntries.Add((item.Entry, rel));
            }

            result.TotalFiles = relativeEntries.Count;
            result.ModJarCount = relativeEntries.Count(x =>
                x.RelPath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                x.RelPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            result.HasConfig = relativeEntries.Any(x => x.RelPath.StartsWith("config/", StringComparison.OrdinalIgnoreCase));
            result.HasResourcePacks = relativeEntries.Any(x => x.RelPath.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase));
            result.HasShaderPacks = relativeEntries.Any(x => x.RelPath.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase));
            result.HasSaves = relativeEntries.Any(x => x.RelPath.StartsWith("saves/", StringComparison.OrdinalIgnoreCase));

            // 1. Try instance.json (Aetheris format)
            var instanceJson = relativeEntries.FirstOrDefault(x => x.RelPath.Equals("instance.json", StringComparison.OrdinalIgnoreCase));
            if (instanceJson.Entry != null && TryParseInstanceJson(instanceJson.Entry, result))
            {
                result.DetectionConfident = true;
                return result;
            }

            // 2. Try mmc-pack.json (MultiMC / Prism Launcher)
            var mmcPack = relativeEntries.FirstOrDefault(x => x.RelPath.Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase));
            if (mmcPack.Entry != null && TryParseMmcPackJson(mmcPack.Entry, result))
            {
                result.DetectionConfident = true;
                return result;
            }

            // 2.5. Try minecraftinstance.json (CurseForge App exported instance)
            var cfInstanceJson = relativeEntries.FirstOrDefault(x => x.RelPath.Equals("minecraftinstance.json", StringComparison.OrdinalIgnoreCase));
            if (cfInstanceJson.Entry != null && TryParseMinecraftInstanceJson(cfInstanceJson.Entry, result))
            {
                result.DetectionConfident = true;
                return result;
            }

            // 3. Try versions/*/*.json (TLauncher / .minecraft)
            var versionJsons = relativeEntries
                .Where(x => x.RelPath.StartsWith("versions/", StringComparison.OrdinalIgnoreCase) &&
                            x.RelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var vj in versionJsons)
            {
                if (TryParseMinecraftVersionJson(vj.Entry, result))
                {
                    result.DetectionConfident = true;
                    return result;
                }
            }

            // 4. Inspect mod .jar files inside mods/
            var modJars = relativeEntries
                .Where(x => x.RelPath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                            x.RelPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();

            if (modJars.Count > 0)
            {
                if (TryDetectFromModJars(modJars.Select(m => m.Entry), result))
                {
                    result.DetectionConfident = true;
                }
            }
            else if (result.TotalFiles > 0 && versionJsons.Count == 0)
            {
                // No mods and no version json -> likely vanilla
                result.DetectedModLoader = "vanilla";
            }

            return result;
        }

        public Task<InstanceInfo> ImportArchiveAsync(
            string archivePath,
            ArchiveScanResult scanResult,
            InstanceInfo instance,
            IProgress<ArchiveImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            return Task.Run(() => ImportArchiveInternal(archivePath, scanResult, instance, progress, ct), ct);
        }

        private InstanceInfo ImportArchiveInternal(
            string archivePath,
            ArchiveScanResult scanResult,
            InstanceInfo instance,
            IProgress<ArchiveImportProgress>? progress,
            CancellationToken ct)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Файл архива сборки не найден", archivePath);

            instance.Id = GetUniqueInstanceId(SanitizeFolderName(instance.Name));
            var instanceDir = instance.GetDirectory(_instanceManager.GetInstancesRootDirectory());
            var fullInstanceDir = Path.GetFullPath(instanceDir);
            if (!fullInstanceDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                fullInstanceDir += Path.DirectorySeparatorChar;

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entriesToExtract = archive.Entries
                .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => (Entry: e, Path: NormalizePath(e.Key!)))
                .Where(x => !string.IsNullOrEmpty(x.Path))
                .Where(x => string.IsNullOrEmpty(scanResult.RootPrefix) ||
                            x.Path.StartsWith(scanResult.RootPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Entry, RelPath: x.Path.Substring(scanResult.RootPrefix.Length).TrimStart('/')))
                .Where(x => !string.IsNullOrEmpty(x.RelPath) &&
                            !x.RelPath.Equals("instance.json", StringComparison.OrdinalIgnoreCase) &&
                            !x.RelPath.Equals("minecraftinstance.json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int total = entriesToExtract.Count;
            progress?.Report(new ArchiveImportProgress
            {
                CurrentFile = 0,
                TotalFiles = total,
                StatusText = $"Распаковка сборки ({total} файлов)..."
            });

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (entry, relPath) = entriesToExtract[i];

                var destPath = Path.GetFullPath(Path.Combine(fullInstanceDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destPath.StartsWith(fullInstanceDir, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip unsafe path traversal entries
                    continue;
                }

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                using (var inStream = entry.OpenEntryStream())
                using (var outStream = File.Create(destPath))
                {
                    inStream.CopyTo(outStream);
                }

                if ((i + 1) % 25 == 0 || i + 1 == total)
                {
                    progress?.Report(new ArchiveImportProgress
                    {
                        CurrentFile = i + 1,
                        TotalFiles = total,
                        StatusText = $"Распаковано {i + 1} из {total}..."
                    });
                }
            }

            _instanceManager.SaveInstance(instance);

            progress?.Report(new ArchiveImportProgress
            {
                CurrentFile = total,
                TotalFiles = total,
                StatusText = "Импорт сборки успешно завершен!"
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

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        private static string DetectRootPrefix(List<string> paths)
        {
            if (paths.Count == 0)
                return "";

            // Check if any root marker exists directly at root
            foreach (var marker in RootMarkers)
            {
                if (paths.Any(p => p.StartsWith(marker, StringComparison.OrdinalIgnoreCase)))
                    return "";
            }

            // Otherwise find the shallowest directory prefix that contains a root marker
            string? bestPrefix = null;
            int bestDepth = int.MaxValue;

            foreach (var path in paths)
            {
                foreach (var marker in RootMarkers)
                {
                    var idx = path.IndexOf("/" + marker, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var prefix = path.Substring(0, idx + 1);
                        var depth = prefix.Count(c => c == '/');
                        if (depth < bestDepth)
                        {
                            bestDepth = depth;
                            bestPrefix = prefix;
                        }
                    }
                }
            }

            if (bestPrefix != null)
                return bestPrefix;

            // Fallback: if all entries share a single top-level folder, strip it
            var firstSlash = paths[0].IndexOf('/');
            if (firstSlash > 0)
            {
                var topDir = paths[0].Substring(0, firstSlash + 1);
                if (paths.All(p => p.StartsWith(topDir, StringComparison.OrdinalIgnoreCase)))
                    return topDir;
            }

            return "";
        }

        private static bool TryParseInstanceJson(IArchiveEntry entry, ArchiveScanResult result)
        {
            try
            {
                using var stream = entry.OpenEntryStream();
                var info = JsonSerializer.Deserialize<InstanceInfo>(stream);
                if (info == null || string.IsNullOrWhiteSpace(info.MinecraftVersion))
                    return false;

                if (!string.IsNullOrWhiteSpace(info.Name))
                    result.SuggestedName = info.Name;
                result.DetectedMinecraftVersion = info.MinecraftVersion;
                if (!string.IsNullOrWhiteSpace(info.ModLoader))
                    result.DetectedModLoader = info.ModLoader.ToLowerInvariant();
                result.DetectedModLoaderVersion = info.ModLoaderVersion ?? "";
                if (info.AllocatedMemoryMb > 0)
                    result.DetectedMemoryMb = info.AllocatedMemoryMb;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseMmcPackJson(IArchiveEntry entry, ArchiveScanResult result)
        {
            try
            {
                using var stream = entry.OpenEntryStream();
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("components", out var components) ||
                    components.ValueKind != JsonValueKind.Array)
                    return false;

                bool foundMc = false;
                string loader = "vanilla";
                string loaderVer = "";

                foreach (var comp in components.EnumerateArray())
                {
                    var uid = comp.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : "";
                    var ver = comp.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

                    if (uid == "net.minecraft" && !string.IsNullOrEmpty(ver))
                    {
                        result.DetectedMinecraftVersion = ver;
                        foundMc = true;
                    }
                    else if (uid == "net.fabricmc.fabric-loader")
                    {
                        loader = "fabric";
                        loaderVer = ver;
                    }
                    else if (uid == "net.minecraftforge")
                    {
                        loader = "forge";
                        loaderVer = ver;
                    }
                    else if (uid == "net.neoforged")
                    {
                        loader = "neoforge";
                        loaderVer = ver;
                    }
                }

                if (foundMc)
                {
                    result.DetectedModLoader = loader;
                    result.DetectedModLoaderVersion = loaderVer;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TryParseMinecraftInstanceJson(IArchiveEntry entry, ArchiveScanResult result)
        {
            try
            {
                using var stream = entry.OpenEntryStream();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                var gameVersion = root.TryGetProperty("gameVersion", out var gvProp) ? gvProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(gameVersion))
                    return false;

                result.DetectedMinecraftVersion = gameVersion.Trim();

                if (root.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        result.SuggestedName = name.Trim();
                }

                if (root.TryGetProperty("allocatedMemory", out var memProp) && memProp.ValueKind == JsonValueKind.Number)
                {
                    if (memProp.TryGetInt32(out int memMb) && memMb >= 1024)
                        result.DetectedMemoryMb = memMb;
                }

                if (root.TryGetProperty("baseModLoader", out var loaderProp) && loaderProp.ValueKind == JsonValueKind.Object)
                {
                    var loaderName = loaderProp.TryGetProperty("name", out var lnProp) ? lnProp.GetString() ?? "" : "";
                    var forgeVer = loaderProp.TryGetProperty("forgeVersion", out var fvProp) ? fvProp.GetString() ?? "" : "";
                    int loaderType = loaderProp.TryGetProperty("type", out var ltProp) && ltProp.ValueKind == JsonValueKind.Number
                        ? ltProp.GetInt32()
                        : 0;

                    var lower = loaderName.ToLowerInvariant();
                    if (lower.StartsWith("neoforge-") || loaderType == 6)
                    {
                        result.DetectedModLoader = "neoforge";
                        result.DetectedModLoaderVersion = !string.IsNullOrWhiteSpace(forgeVer)
                            ? forgeVer
                            : (lower.StartsWith("neoforge-") ? loaderName.Substring("neoforge-".Length) : "");
                        return true;
                    }
                    if (lower.StartsWith("fabric-") || loaderType == 4)
                    {
                        result.DetectedModLoader = "fabric";
                        result.DetectedModLoaderVersion = !string.IsNullOrWhiteSpace(forgeVer)
                            ? forgeVer
                            : (lower.StartsWith("fabric-") ? loaderName.Substring("fabric-".Length) : "");
                        return true;
                    }
                    if (lower.StartsWith("forge-") || loaderType == 1)
                    {
                        result.DetectedModLoader = "forge";
                        result.DetectedModLoaderVersion = !string.IsNullOrWhiteSpace(forgeVer)
                            ? forgeVer
                            : (lower.StartsWith("forge-") ? loaderName.Substring("forge-".Length) : "");
                        return true;
                    }
                }

                if (result.ModJarCount == 0)
                {
                    result.DetectedModLoader = "vanilla";
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TryParseMinecraftVersionJson(IArchiveEntry entry, ArchiveScanResult result)
        {
            try
            {
                using var stream = entry.OpenEntryStream();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var inheritsFrom = root.TryGetProperty("inheritsFrom", out var inhProp) ? inhProp.GetString() ?? "" : "";
                var mainClass = root.TryGetProperty("mainClass", out var mcProp) ? mcProp.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(inheritsFrom))
                {
                    result.DetectedMinecraftVersion = inheritsFrom;
                }
                else
                {
                    var mcMatch = Regex.Match(id, @"\b(1\.\d+(?:\.\d+)?)\b");
                    if (mcMatch.Success)
                        result.DetectedMinecraftVersion = mcMatch.Groups[1].Value;
                    else
                        return false;
                }

                if (id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
                    mainClass.Contains("neoforged", StringComparison.OrdinalIgnoreCase))
                {
                    result.DetectedModLoader = "neoforge";
                    var m = Regex.Match(id, @"neoforge[-_]?([\d\.]+)", RegexOptions.IgnoreCase);
                    if (m.Success) result.DetectedModLoaderVersion = m.Groups[1].Value;
                }
                else if (id.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
                         mainClass.Contains("fabricmc", StringComparison.OrdinalIgnoreCase))
                {
                    result.DetectedModLoader = "fabric";
                    var m = Regex.Match(id, @"fabric-loader-([\d\.]+)-", RegexOptions.IgnoreCase);
                    if (m.Success) result.DetectedModLoaderVersion = m.Groups[1].Value;
                }
                else if (id.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                         mainClass.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                         mainClass.Contains("modlauncher", StringComparison.OrdinalIgnoreCase))
                {
                    result.DetectedModLoader = "forge";
                    var m = Regex.Match(id, @"forge[-_]?([\d\.]+)", RegexOptions.IgnoreCase);
                    if (m.Success) result.DetectedModLoaderVersion = m.Groups[1].Value;
                }
                else
                {
                    result.DetectedModLoader = "vanilla";
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDetectFromModJars(IEnumerable<IArchiveEntry> jarEntries, ArchiveScanResult result)
        {
            int fabricCount = 0;
            int forgeCount = 0;
            int neoForgeCount = 0;
            var mcVersions = new List<string>();

            foreach (var jarEntry in jarEntries)
            {
                try
                {
                    using var ms = new MemoryStream();
                    using (var s = jarEntry.OpenEntryStream())
                        s.CopyTo(ms);
                    ms.Position = 0;

                    using var jar = new ZipArchive(ms, ZipArchiveMode.Read);

                    var fabricEntry = jar.GetEntry("fabric.mod.json");
                    if (fabricEntry != null)
                    {
                        fabricCount++;
                        using var sr = new StreamReader(fabricEntry.Open());
                        var text = sr.ReadToEnd();
                        var m = Regex.Match(text, @"""minecraft""\s*:\s*""[~^>=<\s]*(1\.\d+(?:\.\d+)?)");
                        if (m.Success) mcVersions.Add(m.Groups[1].Value);
                        continue;
                    }

                    var neoEntry = jar.GetEntry("META-INF/neoforge.mods.toml");
                    if (neoEntry != null)
                    {
                        neoForgeCount++;
                        using var sr = new StreamReader(neoEntry.Open());
                        ExtractMcVersionFromModsToml(sr.ReadToEnd(), mcVersions);
                        continue;
                    }

                    var forgeEntry = jar.GetEntry("META-INF/mods.toml");
                    if (forgeEntry != null)
                    {
                        forgeCount++;
                        using var sr = new StreamReader(forgeEntry.Open());
                        ExtractMcVersionFromModsToml(sr.ReadToEnd(), mcVersions);
                        continue;
                    }

                    var mcmodEntry = jar.GetEntry("mcmod.info");
                    if (mcmodEntry != null)
                    {
                        forgeCount++;
                        using var sr = new StreamReader(mcmodEntry.Open());
                        var m = Regex.Match(sr.ReadToEnd(), @"""mcversion""\s*:\s*""(1\.\d+(?:\.\d+)?)""");
                        if (m.Success) mcVersions.Add(m.Groups[1].Value);
                    }
                }
                catch
                {
                    // Ignore malformed jar
                }

                // Also try filename hint
                var fileName = Path.GetFileName(jarEntry.Key ?? "");
                var fnMatch = Regex.Match(fileName, @"(?<!\d)(1\.(?:1[2-9]|2[0-9])(?:\.\d+)?)(?!\d)");
                if (fnMatch.Success)
                    mcVersions.Add(fnMatch.Groups[1].Value);
            }

            bool detected = false;
            if (neoForgeCount > 0 && neoForgeCount >= forgeCount && neoForgeCount >= fabricCount)
            {
                result.DetectedModLoader = "neoforge";
                detected = true;
            }
            else if (fabricCount > 0 && fabricCount >= forgeCount)
            {
                result.DetectedModLoader = "fabric";
                detected = true;
            }
            else if (forgeCount > 0)
            {
                result.DetectedModLoader = "forge";
                detected = true;
            }

            if (mcVersions.Count > 0)
            {
                // Pick the most frequent Minecraft version
                result.DetectedMinecraftVersion = mcVersions
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count())
                    .First()
                    .Key;
                detected = true;
            }

            return detected;
        }

        private static void ExtractMcVersionFromModsToml(string toml, List<string> mcVersions)
        {
            var m = Regex.Match(toml, @"modId\s*=\s*""minecraft""[\s\S]{0,200}?versionRange\s*=\s*""[\[\(\s]*(1\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                mcVersions.Add(m.Groups[1].Value);
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

