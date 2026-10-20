using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AetherisLauncher.Models;

namespace AetherisLauncher.Services
{
    public class NetworkService
    {
        private readonly LauncherSettings _settings;
        private HttpClient? _client;

        public NetworkService(LauncherSettings settings)
        {
            _settings = settings;
            RebuildClient();
        }

        public void RebuildClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(30),
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;

                    // Direct clean Anycast IP for Mojang authentication and services
                    if (host.Equals("api.minecraftservices.com", StringComparison.OrdinalIgnoreCase) ||
                        host.Equals("sessionserver.mojang.com", StringComparison.OrdinalIgnoreCase) ||
                        host.Equals("authserver.mojang.com", StringComparison.OrdinalIgnoreCase))
                    {
                        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                        socket.NoDelay = true;
                        try
                        {
                            await socket.ConnectAsync("13.107.213.19", port, cancellationToken);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }

                    var defaultSocket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    defaultSocket.NoDelay = true;
                    try
                    {
                        await defaultSocket.ConnectAsync(host, port, cancellationToken);
                        return new System.Net.Sockets.NetworkStream(defaultSocket, ownsSocket: true);
                    }
                    catch
                    {
                        defaultSocket.Dispose();
                        throw;
                    }
                }
            };

            switch (_settings.ProxyMode)
            {
                case ProxyMode.Direct:
                    handler.UseProxy = false;
                    break;
                case ProxyMode.SystemProxy:
                    handler.UseProxy = true;
                    handler.Proxy = WebRequest.GetSystemWebProxy();
                    break;
                case ProxyMode.CustomProxy:
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy($"{_settings.CustomProxyHost}:{_settings.CustomProxyPort}");
                    break;
            }

            _client?.Dispose();
            _client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(90)
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AetherisLauncher/1.0");
        }

        public HttpClient GetClient()
        {
            if (_client == null)
            {
                RebuildClient();
            }
            return _client!;
        }

        public string ResolveUrl(string originalUrl)
        {
            if (!_settings.UseBmclapiMirror)
                return originalUrl;

            // Map Mojang assets, client jars, and manifests to BMCLAPI
            if (originalUrl.StartsWith("https://piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
                return originalUrl.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com");

            if (originalUrl.StartsWith("https://launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
                return originalUrl.Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com");

            if (originalUrl.StartsWith("https://piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
                return originalUrl.Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com");

            if (originalUrl.StartsWith("https://launcher.mojang.com", StringComparison.OrdinalIgnoreCase))
                return originalUrl.Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com");

            if (originalUrl.StartsWith("https://resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase))
                return originalUrl.Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets");

            if (originalUrl.StartsWith("https://meta.fabricmc.net", StringComparison.OrdinalIgnoreCase))
                return originalUrl.Replace("https://meta.fabricmc.net", "https://bmclapi2.bangbang93.com/fabric-meta");

            if (originalUrl.StartsWith("https://maven.fabricmc.net", StringComparison.OrdinalIgnoreCase))
                return originalUrl.Replace("https://maven.fabricmc.net", "https://bmclapi2.bangbang93.com/maven");

            // Note: libraries.minecraft.net and maven.neoforged.net are directly accessible and fast without double Chinese CDN redirects
            return originalUrl;
        }

        public async Task DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<(long downloaded, long total, string status)>? progress = null,
            CancellationToken ct = default)
        {
            var targetDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            var primaryUrl = ResolveUrl(url);

            try
            {
                await ExecuteDownloadAsync(primaryUrl, destinationPath, progress, ct);
            }
            catch (Exception) when (primaryUrl != url)
            {
                // Fallback to original URL if mirror failed
                await ExecuteDownloadAsync(url, destinationPath, progress, ct);
            }
            catch (Exception) when (primaryUrl == url && url.StartsWith("https://libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback from libraries.minecraft.net to BMCLAPI
                var mirrorUrl = url.Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven");
                await ExecuteDownloadAsync(mirrorUrl, destinationPath, progress, ct);
            }
        }

        private async Task ExecuteDownloadAsync(
            string targetUrl,
            string destinationPath,
            IProgress<(long downloaded, long total, string status)>? progress,
            CancellationToken ct)
        {
            var client = GetClient();
            using var response = await client.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                progress?.Report((totalRead, totalBytes, Path.GetFileName(destinationPath)));
            }
        }
    }
}
