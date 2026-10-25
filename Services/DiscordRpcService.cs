using System;
using DiscordRPC;
using DiscordRPC.Logging;

namespace AetherisLauncher.Services
{
    public class DiscordRpcService : IDisposable
    {
        public const string CLIENT_ID = "1563153443747536957";
        private DiscordRpcClient? _client;
        private Timestamps? _sessionStartTime;
        private Timestamps? _gameStartTime;

        public event Action<string>? LogMessage;

        public void Initialize()
        {
            try
            {
                _sessionStartTime = Timestamps.Now;

                _client = new DiscordRpcClient(CLIENT_ID)
                {
                    Logger = new ConsoleLogger { Level = LogLevel.Warning }
                };

                _client.OnReady += (sender, msg) =>
                {
                    LogMessage?.Invoke($"[Discord RPC] Подключено к аккаунту: {msg.User.Username}");
                    SetInMenu();
                };

                _client.OnError += (sender, msg) =>
                {
                    LogMessage?.Invoke($"[Discord RPC Ошибка] {msg.Message}");
                };

                _client.Initialize();
                SetInMenu();
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[Discord RPC] Не удалось инициализировать: {ex.Message}");
                _client = null;
            }
        }

        public void SetInMenu(string state = "Выбирает сборку")
        {
            if (_client == null || _client.IsDisposed) return;

            try
            {
                _client.SetPresence(new RichPresence
                {
                    Details = "В главном меню",
                    State = state,
                    Timestamps = _sessionStartTime ?? Timestamps.Now,
                    Assets = new Assets
                    {
                        LargeImageKey = "aetheris_logo_boxed",
                        LargeImageText = "Aetheris Launcher"
                    }
                });
            }
            catch
            {
                // Игнорируем сетевые сбои Discord IPC
            }
        }

        public void SetPlaying(string instanceName, string mcVersion)
        {
            if (_client == null || _client.IsDisposed) return;

            try
            {
                _gameStartTime = Timestamps.Now;

                _client.SetPresence(new RichPresence
                {
                    Details = $"В игре: {instanceName}",
                    State = $"Minecraft {mcVersion}",
                    Timestamps = _gameStartTime,
                    Assets = new Assets
                    {
                        LargeImageKey = "aetheris_logo_boxed",
                        LargeImageText = instanceName
                    }
                });
            }
            catch
            {
                // Игнорируем сетевые сбои Discord IPC
            }
        }

        public void Dispose()
        {
            try
            {
                if (_client != null && !_client.IsDisposed)
                {
                    _client.ClearPresence();
                    _client.Dispose();
                    _client = null;
                }
            }
            catch
            {
                // Игнорируем при выходе
            }
        }
    }
}
