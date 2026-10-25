using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AetherisLauncher.Services
{
    public class UpdateCheckResult
    {
        public bool HasUpdate { get; set; }
        public Version? CurrentVersion { get; set; }
        public Version? LatestVersion { get; set; }
        public string VersionString { get; set; } = string.Empty;
        public string ChangeLog { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public long FileSize { get; set; }
    }

    public class UpdateService
    {
        private const string GITHUB_REPO = "Anonim282/AetherisLauncher";
        private readonly NetworkService _network;

        public event Action<string>? LogMessage;

        public UpdateService(NetworkService network)
        {
            _network = network;
        }

        public Version GetCurrentVersion()
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver ?? new Version(1, 0, 5, 0);
        }

        public async Task<UpdateCheckResult?> CheckForUpdatesAsync(CancellationToken ct = default)
        {
            try
            {
                var currentVer = GetCurrentVersion();
                var url = $"https://api.github.com/repos/{GITHUB_REPO}/releases/latest";

                var client = _network.GetClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("AetherisLauncher-Updater/1.0");

                var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    LogMessage?.Invoke($"[UPDATER] Проверка обновлений: статус {response.StatusCode}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagProp))
                    return null;

                var rawTag = tagProp.GetString()?.Trim() ?? string.Empty;
                var cleanTag = rawTag.TrimStart('v', 'V');

                if (!Version.TryParse(cleanTag, out var remoteVer))
                {
                    // Fallback parse for "1.0" or "1.0.5"
                    var parts = cleanTag.Split('.');
                    if (parts.Length == 2 && Version.TryParse($"{cleanTag}.0", out var v2))
                        remoteVer = v2;
                    else
                        return null;
                }

                // If remote version is greater than current
                if (remoteVer > currentVer)
                {
                    var body = root.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";
                    string downloadUrl = string.Empty;
                    long fileSize = 0;

                    if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assetsProp.EnumerateArray())
                        {
                            var name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() ?? "" : "";
                                fileSize = asset.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        return new UpdateCheckResult
                        {
                            HasUpdate = true,
                            CurrentVersion = currentVer,
                            LatestVersion = remoteVer,
                            VersionString = rawTag,
                            ChangeLog = body,
                            DownloadUrl = downloadUrl,
                            FileSize = fileSize
                        };
                    }
                }

                return new UpdateCheckResult
                {
                    HasUpdate = false,
                    CurrentVersion = currentVer,
                    LatestVersion = remoteVer
                };
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[UPDATER] Ошибка при проверке обновлений: {ex.Message}");
                return null;
            }
        }

        public async Task DownloadAndApplyUpdateAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath))
                throw new InvalidOperationException("Не удалось определить путь к текущему исполняемому файлу.");

            var currentDir = Path.GetDirectoryName(currentExePath)!;
            var tempExePath = Path.Combine(currentDir, "AetherisLauncher_update.tmp");
            var scriptPath = Path.Combine(currentDir, "update_apply.cmd");

            LogMessage?.Invoke($"[UPDATER] Загрузка обновления из {downloadUrl}...");

            var client = _network.GetClient();
            using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, ct);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        var pct = (double)totalRead / totalBytes * 100.0;
                        progress?.Report(pct);
                    }
                }
            }

            LogMessage?.Invoke("[UPDATER] Загрузка завершена. Подготовка к перезапуску...");

            // Создаем безопасный командный файл для замены .exe после завершения лаунчера
            var scriptContent = $@"@echo off
chcp 65001 > nul
timeout /t 1 /nobreak > nul
:retry
copy /y ""{tempExePath}"" ""{currentExePath}"" > nul
if errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto retry
)
del /f /q ""{tempExePath}"" > nul
start """" ""{currentExePath}""
del /f /q ""%~f0"" > nul
exit
";
            await File.WriteAllTextAsync(scriptPath, scriptContent, ct);

            // Запускаем updater скрипт в скрытом окне
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = currentDir
            };

            Process.Start(psi);

            // Закрываем текущее приложение
            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });
        }
    }
}
