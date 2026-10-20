using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherisLauncher.Models;

namespace AetherisLauncher.Services
{
    public class MinecraftLauncher
    {
        private readonly InstanceManager _instanceManager;
        private readonly MinecraftDownloader _downloader;
        private readonly string _sharedRoot;

        public event Action<string>? LogReceived;

        private readonly JavaRuntimeService _javaRuntime;

        public MinecraftLauncher(
            InstanceManager instanceManager,
            MinecraftDownloader downloader,
            JavaRuntimeService javaRuntime,
            string? sharedRoot = null)
        {
            _instanceManager = instanceManager;
            _downloader = downloader;
            _javaRuntime = javaRuntime;
            _sharedRoot = sharedRoot ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        public async Task<Process> LaunchAsync(
            InstanceInfo instance,
            AuthAccount account,
            LauncherSettings settings,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report("[Aetheris] Проверка и подготовка базовых файлов игры...");
            var mcVersion = instance.MinecraftVersion;

            // 1. Ensure vanilla files
            var vanillaJsonPath = await _downloader.EnsureVanillaVersionAsync(mcVersion, progress, ct);

            // 2. Ensure modloader
            string activeJsonPath = vanillaJsonPath;
            bool isNeoForge = instance.ModLoader.Equals("neoforge", StringComparison.OrdinalIgnoreCase);
            bool isForge = instance.ModLoader.Equals("forge", StringComparison.OrdinalIgnoreCase);
            bool isFabric = instance.ModLoader.Equals("fabric", StringComparison.OrdinalIgnoreCase);

            if (isNeoForge)
            {
                var loaderVer = string.IsNullOrEmpty(instance.ModLoaderVersion) ? "21.1.259" : instance.ModLoaderVersion;
                activeJsonPath = await _downloader.EnsureNeoForgeVersionAsync(mcVersion, loaderVer, progress, ct);
            }
            else if (isForge)
            {
                var loaderVer = string.IsNullOrEmpty(instance.ModLoaderVersion) ? "43.5.0" : instance.ModLoaderVersion;
                activeJsonPath = await _downloader.EnsureForgeVersionAsync(mcVersion, loaderVer, progress, ct);
            }
            else if (isFabric)
            {
                var loaderVer = string.IsNullOrEmpty(instance.ModLoaderVersion) ? "0.15.11" : instance.ModLoaderVersion;
                activeJsonPath = await _downloader.EnsureFabricVersionAsync(mcVersion, loaderVer, progress, ct);
            }

            // 3. Resolve Java (portable on-demand download for specific Minecraft version)
            string javaExe;
            if (!string.IsNullOrEmpty(instance.CustomJavaPath) && File.Exists(instance.CustomJavaPath))
            {
                javaExe = instance.CustomJavaPath;
            }
            else
            {
                javaExe = await _javaRuntime.EnsureJavaAsync(mcVersion, _sharedRoot, progress, ct);
            }

            progress?.Report($"[Aetheris] Выбрана Java: {javaExe}");

            // 4. Build Classpath & Collect Libraries
            var classpathEntries = new List<string>();
            var libDir = _downloader.GetLibrariesDirectory();

            // ModLoader libraries
            if (activeJsonPath != vanillaJsonPath)
            {
                var modJson = await File.ReadAllTextAsync(activeJsonPath, ct);
                using var mDoc = JsonDocument.Parse(modJson);
                if (mDoc.RootElement.TryGetProperty("libraries", out var mLibs))
                {
                    foreach (var lib in mLibs.EnumerateArray())
                    {
                        if (lib.TryGetProperty("downloads", out var dls) &&
                            dls.TryGetProperty("artifact", out var art) &&
                            art.TryGetProperty("path", out var pProp))
                        {
                            var fullPath = Path.Combine(libDir, pProp.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(fullPath))
                                classpathEntries.Add(fullPath);
                        }
                        else if (lib.TryGetProperty("name", out var nProp))
                        {
                            var relPath = MinecraftDownloader.ConvertMavenNameToPath(nProp.GetString()!);
                            var fullPath = Path.Combine(libDir, relPath);
                            if (File.Exists(fullPath))
                                classpathEntries.Add(fullPath);
                        }
                    }
                }
            }

            // Vanilla libraries
            var vanillaJson = await File.ReadAllTextAsync(vanillaJsonPath, ct);
            using var vDoc = JsonDocument.Parse(vanillaJson);
            var vRoot = vDoc.RootElement;

            if (vRoot.TryGetProperty("libraries", out var vLibs))
            {
                foreach (var lib in vLibs.EnumerateArray())
                {
                    if (lib.TryGetProperty("downloads", out var dls) &&
                        dls.TryGetProperty("artifact", out var art) &&
                        art.TryGetProperty("path", out var pathProp))
                    {
                        var relPath = pathProp.GetString()!.Replace('/', Path.DirectorySeparatorChar);
                        var fullPath = Path.Combine(libDir, relPath);
                        if (File.Exists(fullPath))
                            classpathEntries.Add(fullPath);
                    }
                }
            }

            // For Forge / NeoForge: do NOT add vanilla client.jar to classpath if patched client jar exists,
            // otherwise Java module system crashes with: Modules minecraft and client export package net.minecraft.*
            var clientJar = Path.Combine(_downloader.GetVersionsDirectory(), mcVersion, $"{mcVersion}.jar");

            if (isNeoForge)
            {
                // In NeoForge 1.21+, client-*-srg.jar is loaded internally by ProductionClientProvider as the 'minecraft' module.
                // It must NOT be added to -cp, otherwise JVM creates an automatic module named 'client' from it,
                // causing JPMS ResolutionException: "Modules client and minecraft export package net.minecraft...".
                // Only resources (*-extra.jar) of the current Minecraft version are placed on classpath.
                var mcClientDir = Path.Combine(libDir, "net", "minecraft", "client");
                if (Directory.Exists(mcClientDir))
                {
                    foreach (var extraJar in Directory.GetFiles(mcClientDir, "*-extra.jar", SearchOption.AllDirectories))
                    {
                        if (extraJar.Contains(mcVersion))
                        {
                            classpathEntries.Add(extraJar);
                        }
                    }
                }
            }
            else if (isForge)
            {
                var forgeClientJar = Path.Combine(libDir, "net", "minecraftforge", "forge", $"{mcVersion}-{instance.ModLoaderVersion}", $"forge-{mcVersion}-{instance.ModLoaderVersion}-client.jar");
                if (File.Exists(forgeClientJar))
                {
                    classpathEntries.Add(forgeClientJar);
                }
                else if (File.Exists(clientJar))
                {
                    classpathEntries.Add(clientJar);
                }

                var mcClientDir = Path.Combine(libDir, "net", "minecraft", "client");
                if (Directory.Exists(mcClientDir))
                {
                    foreach (var extraJar in Directory.GetFiles(mcClientDir, "*-extra.jar", SearchOption.AllDirectories))
                    {
                        if (extraJar.Contains(mcVersion))
                        {
                            classpathEntries.Add(extraJar);
                        }
                    }
                }
            }
            else
            {
                if (File.Exists(clientJar))
                {
                    classpathEntries.Add(clientJar);
                }
            }

            var classpath = string.Join(";", classpathEntries.Distinct());

            // 5. Determine main class and arguments
            string mainClass = "net.minecraft.client.main.Main";
            var activeDoc = JsonDocument.Parse(await File.ReadAllTextAsync(activeJsonPath, ct));
            var activeRoot = activeDoc.RootElement;

            if (activeRoot.TryGetProperty("mainClass", out var mainProp))
            {
                mainClass = mainProp.GetString() ?? mainClass;
            }

            var instanceGameDir = instance.GetDirectory(_instanceManager.GetInstancesRootDirectory());
            var assetsDir = _downloader.GetAssetsDirectory();
            var nativesDir = Path.Combine(_sharedRoot, "natives", mcVersion);
            Directory.CreateDirectory(nativesDir);

            string assetIndex = mcVersion;
            if (vRoot.TryGetProperty("assetIndex", out var assetIndexObj) &&
                assetIndexObj.TryGetProperty("id", out var aIdProp))
            {
                assetIndex = aIdProp.GetString() ?? mcVersion;
            }

            // 6. Build JVM Arguments
            var memoryMb = settings.DefaultMemoryMb > 0 ? settings.DefaultMemoryMb : (instance.AllocatedMemoryMb > 0 ? instance.AllocatedMemoryMb : 4096);
            var jvmArgList = new List<string>
            {
                $"-Xms512M",
                $"-Xmx{memoryMb}M",
                $"-Dfile.encoding=UTF-8",
                $"-Dsun.stdout.encoding=UTF-8",
                $"-Dsun.stderr.encoding=UTF-8",
                $"-Djava.net.preferIPv4Stack=true",
                $"-Djava.library.path=\"{nativesDir}\""
            };

            // Workaround for NeoForge Early Display Window crash
            if (isNeoForge)
            {
                jvmArgList.Add("-Dfml.earlyWindowControl=false");
            }

            if (!string.IsNullOrEmpty(instance.CustomJvmArgs))
            {
                jvmArgList.Add(instance.CustomJvmArgs);
            }

            // Configure JVM network & proxy settings
            if (settings.ProxyMode == ProxyMode.SystemProxy)
            {
                jvmArgList.Add("-Djava.net.useSystemProxies=true");
                jvmArgList.Add("-Dhttp.nonProxyHosts=\"localhost|127.0.0.1|26.*\"");
            }
            else if (settings.ProxyMode == ProxyMode.CustomProxy && !string.IsNullOrWhiteSpace(settings.CustomProxyHost))
            {
                jvmArgList.Add($"-Dhttp.proxyHost={settings.CustomProxyHost}");
                jvmArgList.Add($"-Dhttp.proxyPort={settings.CustomProxyPort}");
                jvmArgList.Add($"-Dhttps.proxyHost={settings.CustomProxyHost}");
                jvmArgList.Add($"-Dhttps.proxyPort={settings.CustomProxyPort}");
                jvmArgList.Add("-Dhttp.nonProxyHosts=\"localhost|127.0.0.1|26.*\"");
            }

            // Direct clean route for Mojang authentication and session servers (bypasses ISP DPI timeouts)
            var configDir = Path.Combine(_sharedRoot, "config");
            Directory.CreateDirectory(configDir);
            var hostsFile = Path.Combine(configDir, "mojang_hosts.txt");
            File.WriteAllText(hostsFile, "13.107.213.19 sessionserver.mojang.com\r\n13.107.213.19 api.minecraftservices.com\r\n13.107.213.19 authserver.mojang.com\r\n");
            jvmArgList.Add($"-Djdk.net.hosts.file=\"{hostsFile}\"");

            // If NeoForge, parse its JVM arguments (modules, exports, -p)
            if ((isNeoForge || isForge) && activeRoot.TryGetProperty("arguments", out var argsObj) &&
                argsObj.TryGetProperty("jvm", out var jvmArgsArray))
            {
                foreach (var arg in jvmArgsArray.EnumerateArray())
                {
                    if (arg.ValueKind == JsonValueKind.String)
                    {
                        var val = arg.GetString()!;
                        val = val.Replace("${library_directory}", libDir.Replace('\\', '/'));
                        val = val.Replace("${classpath_separator}", ";");
                        val = val.Replace("${version_name}", mcVersion);
                        val = val.Replace("${classpath}", classpath);
                        jvmArgList.Add(val);
                    }
                }
            }

            // 7. Build Game Arguments
            var gameArgList = new List<string>
            {
                $"--username \"{account.Username}\"",
                $"--version \"{mcVersion}\"",
                $"--gameDir \"{instanceGameDir}\"",
                $"--assetsDir \"{assetsDir}\"",
                $"--assetIndex \"{assetIndex}\"",
                $"--uuid \"{account.Uuid}\"",
                $"--accessToken \"{(account.IsOffline ? "0" : account.AccessToken)}\"",
                $"--userType {(account.IsOffline ? "mojang" : "msa")}",
                $"--versionType Aetheris"
            };

            // If NeoForge, append game args
            if ((isNeoForge || isForge) && activeRoot.TryGetProperty("arguments", out var gArgsObj) &&
                gArgsObj.TryGetProperty("game", out var gameArgsArray))
            {
                foreach (var arg in gameArgsArray.EnumerateArray())
                {
                    if (arg.ValueKind == JsonValueKind.String)
                    {
                        gameArgList.Add(arg.GetString()!);
                    }
                }
            }

            var finalArgs = $"{string.Join(" ", jvmArgList)} -cp \"{classpath}\" {mainClass} {string.Join(" ", gameArgList)}";

            progress?.Report("[Aetheris] Запуск процесса Java Minecraft...");
            LogReceived?.Invoke($"[LAUNCHER] Запуск: {javaExe}");
            LogReceived?.Invoke($"[LAUNCHER] Рабочая папка: {instanceGameDir}");
            LogReceived?.Invoke($"[LAUNCHER] Игрок: {account.Username} ({(account.IsOffline ? "Offline" : "Microsoft License")})");
            LogReceived?.Invoke($"[LAUNCHER] Выделено памяти: {memoryMb} MB (глобальная настройка)");

            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = finalArgs,
                WorkingDirectory = instanceGameDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    LogReceived?.Invoke($"[STDOUT] {e.Data}");
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    LogReceived?.Invoke($"[STDERR] {e.Data}");
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            instance.LastPlayed = DateTime.UtcNow;
            _instanceManager.SaveInstance(instance);

            progress?.Report("Minecraft успешно запущен!");
            return proc;
        }
    }
}
