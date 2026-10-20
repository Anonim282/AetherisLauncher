using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherisLauncher.Models;

namespace AetherisLauncher.Services
{
    public class DeviceCodeResponse
    {
        public string user_code { get; set; } = "";
        public string device_code { get; set; } = "";
        public string verification_uri { get; set; } = "";
        public int expires_in { get; set; }
        public int interval { get; set; } = 5;
        public string message { get; set; } = "";
    }

    public class AuthService
    {
        private readonly NetworkService _network;
        private const string ClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

        public AuthService(NetworkService network)
        {
            _network = network;
        }

        
        public string GetMicrosoftAccountFilePath()
        {
            var dir = LauncherSettings.GetConfigDirectory();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "microsoft_account.json");
        }

        public string GetOfflineAccountFilePath()
        {
            var dir = LauncherSettings.GetConfigDirectory();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "offline_account.json");
        }

        public void SaveMicrosoftAccount(AuthAccount account)
        {
            try
            {
                var path = GetMicrosoftAccountFilePath();
                var json = JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public void SaveOfflineAccount(AuthAccount account)
        {
            try
            {
                var path = GetOfflineAccountFilePath();
                var json = JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public AuthAccount? LoadSavedMicrosoftAccount()
        {
            var path = GetMicrosoftAccountFilePath();
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AuthAccount>(json);
                }
                catch { }
            }
            return null;
        }

        public async Task<AuthAccount> RefreshAccountAsync(AuthAccount account, CancellationToken ct = default)
        {
            if (account.IsOffline || string.IsNullOrEmpty(account.RefreshToken))
                return account;

            var client = _network.GetClient();
            var dict = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "client_id", ClientId },
                { "refresh_token", account.RefreshToken },
                { "scope", "XboxLive.signin offline_access" }
            };

            using var formContent = new FormUrlEncodedContent(dict);
            using var response = await client.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", formContent, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var msTokenProp))
            {
                var msAccessToken = msTokenProp.GetString()!;
                var refreshToken = root.TryGetProperty("refresh_token", out var rProp) ? rProp.GetString() : account.RefreshToken;

                var refreshedAccount = await AuthenticateWithMinecraftAsync(msAccessToken, refreshToken ?? "", ct);
                SaveMicrosoftAccount(refreshedAccount);
                SaveAccount(refreshedAccount);
                return refreshedAccount;
            }

            return account;
        }

        public string GetAccountFilePath()
        {
            var dir = LauncherSettings.GetConfigDirectory();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "active_account.json");
        }

        public void SaveAccount(AuthAccount account)
        {
            var path = GetAccountFilePath();
            var json = JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public AuthAccount? LoadSavedAccount()
        {
            var path = GetAccountFilePath();
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AuthAccount>(json);
                }
                catch { }
            }
            return null;
        }

        public AuthAccount CreateOfflineAccount(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                username = "Player";

            var acc = new AuthAccount
            {
                Username = username,
                Uuid = GenerateOfflinePlayerUuid(username),
                Xuid = "0",
                AccessToken = "0",
                IsOffline = true,
                ExpiresAt = DateTime.UtcNow.AddYears(10)
            };
            SaveAccount(acc);
            return acc;
        }

        public static string GenerateOfflinePlayerUuid(string username)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public async Task<DeviceCodeResponse> StartDeviceCodeLoginAsync(CancellationToken ct = default)
        {
            var client = _network.GetClient();
            var dict = new Dictionary<string, string>
            {
                { "client_id", ClientId },
                { "scope", "XboxLive.signin offline_access" }
            };

            using var content = new FormUrlEncodedContent(dict);
            using var response = await client.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", content, ct);
            
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Microsoft Error ({response.StatusCode}): {json}");
            }

            return JsonSerializer.Deserialize<DeviceCodeResponse>(json)
                ?? throw new InvalidOperationException("Не удалось получить код авторизации Microsoft");
        }

        public async Task<AuthAccount> PollDeviceCodeTokenAsync(DeviceCodeResponse deviceCode, CancellationToken ct = default)
        {
            var client = _network.GetClient();
            var pollInterval = TimeSpan.FromSeconds(Math.Max(5, deviceCode.interval));

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(pollInterval, ct);

                var dict = new Dictionary<string, string>
                {
                    { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" },
                    { "client_id", ClientId },
                    { "device_code", deviceCode.device_code }
                };

                using var formContent = new FormUrlEncodedContent(dict);
                using var response = await client.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", formContent, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errProp))
                {
                    var err = errProp.GetString();
                    if (err == "authorization_pending")
                        continue;

                    throw new InvalidOperationException($"Ошибка входа Microsoft: {err}");
                }

                if (root.TryGetProperty("access_token", out var msTokenProp))
                {
                    var msAccessToken = msTokenProp.GetString()!;
                    var refreshToken = root.TryGetProperty("refresh_token", out var rProp) ? rProp.GetString() : "";

                    var account = await AuthenticateWithMinecraftAsync(msAccessToken, refreshToken ?? "", ct);
                    SaveAccount(account);
                    return account;
                }
            }

            throw new OperationCanceledException();
        }

        private async Task<AuthAccount> AuthenticateWithMinecraftAsync(string msAccessToken, string refreshToken, CancellationToken ct)
        {
            var client = _network.GetClient();

            // 1. Xbox Live User Authenticate
            var xblReq = new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = $"d={msAccessToken}"
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            };

            var xblContent = new StringContent(JsonSerializer.Serialize(xblReq), Encoding.UTF8, "application/json");
            var xblRes = await client.PostAsync("https://user.auth.xboxlive.com/user/authenticate", xblContent, ct);
            xblRes.EnsureSuccessStatusCode();

            var xblJson = await xblRes.Content.ReadAsStringAsync(ct);
            using var xblDoc = JsonDocument.Parse(xblJson);
            var xblToken = xblDoc.RootElement.GetProperty("Token").GetString()!;
            var xui = xblDoc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0];
            var userHash = xui.GetProperty("uhs").GetString()!;
            
            string xuid = "";
            if (xui.TryGetProperty("xid", out var xidProp))
                xuid = xidProp.GetString() ?? "";

            // 2. XSTS Token
            var xstsReq = new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xblToken }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            };

            var xstsContent = new StringContent(JsonSerializer.Serialize(xstsReq), Encoding.UTF8, "application/json");
            var xstsRes = await client.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsContent, ct);
            xstsRes.EnsureSuccessStatusCode();

            var xstsJson = await xstsRes.Content.ReadAsStringAsync(ct);
            using var xstsDoc = JsonDocument.Parse(xstsJson);
            var xstsToken = xstsDoc.RootElement.GetProperty("Token").GetString()!;

            // 3. Minecraft Service Login
            var mcLoginReq = new
            {
                identityToken = $"XBL3.0 x={userHash};{xstsToken}"
            };

            var mcContent = new StringContent(JsonSerializer.Serialize(mcLoginReq), Encoding.UTF8, "application/json");
            var mcRes = await client.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcContent, ct);
            mcRes.EnsureSuccessStatusCode();

            var mcJson = await mcRes.Content.ReadAsStringAsync(ct);
            using var mcDoc = JsonDocument.Parse(mcJson);
            var mcAccessToken = mcDoc.RootElement.GetProperty("access_token").GetString()!;

            // 4. Get Minecraft Profile
            using var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            profileReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcAccessToken);

            var profileRes = await client.SendAsync(profileReq, ct);
            profileRes.EnsureSuccessStatusCode();

            var profileJson = await profileRes.Content.ReadAsStringAsync(ct);
            using var profileDoc = JsonDocument.Parse(profileJson);
            var pRoot = profileDoc.RootElement;

            var username = pRoot.GetProperty("name").GetString()!;
            var uuid = pRoot.GetProperty("id").GetString()!;

            return new AuthAccount
            {
                Username = username,
                Uuid = uuid,
                Xuid = xuid,
                AccessToken = mcAccessToken,
                RefreshToken = refreshToken,
                IsOffline = false,
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            };
        }
    }
}

