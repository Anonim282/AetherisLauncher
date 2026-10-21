using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AetherisLauncher.Services
{
    public class DiscordRpcService : IDisposable
    {
        private NamedPipeClientStream? _pipe;
        private readonly string _clientId;
        private readonly int _processId;
        private bool _isConnected;
        private bool _isDisposed;
        private readonly CancellationTokenSource _cts = new();

        private string? _currentDetails;
        private string? _currentState;
        private long? _startTimestamp;
        private string? _largeImage;
        private string? _largeText;
        private string? _smallImage;
        private string? _smallText;

        public event Action<string>? LogReceived;

        public DiscordRpcService(string clientId)
        {
            _clientId = string.IsNullOrWhiteSpace(clientId) ? "1342152865768214619" : clientId;
            _processId = Process.GetCurrentProcess().Id;

            // Start background worker for connection maintenance
            Task.Run(ConnectionWorkerAsync);
        }

        public void SetPresenceInLauncher()
        {
            _currentDetails = "В меню лаунчера";
            _currentState = "Выбирает сборку";
            _startTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _largeImage = "aetheris_logo";
            _largeText = "Aetheris Launcher";
            _smallImage = null;
            _smallText = null;

            UpdatePresence();
        }

        public void SetPresenceInGame(string instanceName, string version, string username)
        {
            _currentDetails = $"Играет: {instanceName}";
            _currentState = $"{version} • {username}";
            _startTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _largeImage = "aetheris_logo";
            _largeText = instanceName;
            _smallImage = "minecraft";
            _smallText = $"Игрок: {username}";

            UpdatePresence();
        }

        public void ClearPresence()
        {
            _currentDetails = null;
            _currentState = null;
            _startTimestamp = null;

            if (!_isConnected || _pipe == null || !_pipe.IsConnected) return;

            try
            {
                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = _processId,
                        activity = (object?)null
                    },
                    nonce = Guid.NewGuid().ToString()
                };

                SendFrame(1, JsonSerializer.Serialize(payload));
            }
            catch { }
        }

        private void UpdatePresence()
        {
            if (!_isConnected || _pipe == null || !_pipe.IsConnected) return;

            try
            {
                object? timestamps = _startTimestamp.HasValue ? new { start = _startTimestamp.Value } : null;

                object? assets = null;
                if (!string.IsNullOrEmpty(_largeImage) || !string.IsNullOrEmpty(_smallImage))
                {
                    assets = new
                    {
                        large_image = _largeImage ?? "aetheris_logo",
                        large_text = _largeText ?? "Aetheris Launcher",
                        small_image = _smallImage,
                        small_text = _smallText
                    };
                }

                var activity = new
                {
                    details = _currentDetails,
                    state = _currentState,
                    timestamps,
                    assets,
                    buttons = new[]
                    {
                        new { label = "Aetheris Launcher", url = "https://github.com/Anonim282/AetherisLauncher" }
                    }
                };

                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = _processId,
                        activity
                    },
                    nonce = Guid.NewGuid().ToString()
                };

                string json = JsonSerializer.Serialize(payload);
                SendFrame(1, json);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[Discord RPC] Ошибка отправки активности: {ex.Message}");
                Disconnect();
            }
        }

        private async Task ConnectionWorkerAsync()
        {
            while (!_isDisposed && !_cts.Token.IsCancellationRequested)
            {
                if (!_isConnected)
                {
                    TryConnect();
                    if (_isConnected && !string.IsNullOrEmpty(_currentDetails))
                    {
                        UpdatePresence();
                    }
                }

                try
                {
                    await Task.Delay(10000, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void TryConnect()
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    string pipeName = $"discord-ipc-{i}";
                    _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    _pipe.Connect(500);

                    // Send Handshake
                    var handshake = new
                    {
                        v = 1,
                        client_id = _clientId
                    };

                    string json = JsonSerializer.Serialize(handshake);
                    SendFrame(0, json);

                    // Read handshake response
                    var (op, response) = ReadFrame();
                    if (op == 1 && response.Contains("READY"))
                    {
                        _isConnected = true;
                        LogReceived?.Invoke($"[Discord RPC] Подключено к Discord через {pipeName}");
                        return;
                    }

                    Disconnect();
                }
                catch
                {
                    Disconnect();
                }
            }
        }

        private void SendFrame(int opCode, string json)
        {
            if (_pipe == null || !_pipe.IsConnected) return;

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];

            BitConverter.GetBytes(opCode).CopyTo(header, 0);
            BitConverter.GetBytes(jsonBytes.Length).CopyTo(header, 4);

            _pipe.Write(header, 0, 8);
            _pipe.Write(jsonBytes, 0, jsonBytes.Length);
            _pipe.Flush();
        }

        private (int OpCode, string Json) ReadFrame()
        {
            if (_pipe == null || !_pipe.IsConnected) return (-1, "");

            byte[] header = new byte[8];
            int read = 0;
            while (read < 8)
            {
                int r = _pipe.Read(header, read, 8 - read);
                if (r <= 0) throw new IOException("Discord pipe closed");
                read += r;
            }

            int op = BitConverter.ToInt32(header, 0);
            int length = BitConverter.ToInt32(header, 4);

            byte[] buffer = new byte[length];
            read = 0;
            while (read < length)
            {
                int r = _pipe.Read(buffer, read, length - read);
                if (r <= 0) throw new IOException("Discord pipe closed");
                read += r;
            }

            string json = Encoding.UTF8.GetString(buffer);
            return (op, json);
        }

        private void Disconnect()
        {
            _isConnected = false;
            try
            {
                _pipe?.Dispose();
            }
            catch { }
            _pipe = null;
        }

        public void Dispose()
        {
            _isDisposed = true;
            _cts.Cancel();
            ClearPresence();
            Disconnect();
            _cts.Dispose();
        }
    }
}
