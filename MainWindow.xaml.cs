using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AetherisLauncher.Models;
using AetherisLauncher.Services;
using AetherisLauncher.Views;

namespace AetherisLauncher
{
    public partial class MainWindow : Window
    {
        private LauncherSettings _settings;
        private readonly NetworkService _network;
        private readonly JavaRuntimeService _javaRuntime;
        private InstanceManager _instanceManager = null!;
        private MinecraftDownloader _downloader = null!;
        private MinecraftLauncher _launcher = null!;
        private readonly AuthService _auth;
        private CurseForgeImporter _importer = null!;
        private DiscordRpcService? _discordRpc;

        private AuthAccount _activeAccount;
        private ObservableCollection<InstanceInfo> _instances = new();
        private bool _isConsoleVisible = false;

        public MainWindow()
        {
            InitializeComponent();

            _settings = LoadSettings();
            _network = new NetworkService(_settings);
            _javaRuntime = new JavaRuntimeService(_network);
            _auth = new AuthService(_network);

            // Check First Launch
            if (!_settings.IsFirstLaunchCompleted)
            {
                var setupWnd = new FirstSetupWindow(_settings);
                setupWnd.ShowDialog();
            }

            InitializeGameServices();

            // Load saved Account or fallback to offline
            _activeAccount = LoadSavedAccount() ?? _auth.CreateOfflineAccount("Player");
            UpdateAccountUI();

            // Load Instances
            RefreshInstances();

            // Initialize Discord Rich Presence
            if (_settings.EnableDiscordRpc)
            {
                try
                {
                    _discordRpc = new DiscordRpcService(_settings.DiscordClientId);
                    _discordRpc.LogReceived += LogConsole;
                    _discordRpc.SetPresenceInLauncher();
                }
                catch (Exception ex)
                {
                    LogConsole($"[Discord RPC] Ошибка инициализации: {ex.Message}");
                }
            }

            LogConsole("==================================================");
            LogConsole("🚀 Aetheris Launcher инициализирован и готов к работе.");
            LogConsole($"📁 Папка сборок: {_instanceManager.GetInstancesRootDirectory()}");
            LogConsole($"👤 Активный профиль: {_activeAccount.Username} ({(_activeAccount.IsOffline ? "Offline" : "Лицензия Microsoft")})");
            LogConsole($"🌐 Сеть: {(_settings.UseBmclapiMirror ? "Зеркало BMCLAPI (активно)" : "Прямое подключение")}");
            LogConsole($"🎮 Discord RPC: {(_settings.EnableDiscordRpc ? "Включен" : "Отключен")}");
            LogConsole("==================================================");
        }

        private void InitializeGameServices()
        {
            var gameRootDir = _settings.GetEffectiveGameRootDirectory();
            _instanceManager = new InstanceManager(gameRootDir);
            _downloader = new MinecraftDownloader(_network, gameRootDir);
            _launcher = new MinecraftLauncher(_instanceManager, _downloader, _javaRuntime, gameRootDir);
            _importer = new CurseForgeImporter(_network, _instanceManager);

            // Hook Minecraft process logs to console
            _launcher.LogReceived += (logLine) =>
            {
                Dispatcher.Invoke(() =>
                {
                    TxtConsole.AppendText(logLine + Environment.NewLine);
                    TxtConsole.ScrollToEnd();
                });
            };
        }

        private static string GetAccountFilePath()
        {
            var dir = LauncherSettings.GetConfigDirectory();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "active_account.json");
        }

        private AuthAccount? LoadSavedAccount()
        {
            try
            {
                var path = GetAccountFilePath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AuthAccount>(json);
                }
            }
            catch { }
            return null;
        }

        private void SaveSavedAccount(AuthAccount account)
        {
            try
            {
                var path = GetAccountFilePath();
                var json = JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private LauncherSettings LoadSettings()
        {
            var path = LauncherSettings.GetConfigFilePath();
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
                    if (settings.DiscordClientId == "1342152865768214619")
                    {
                        settings.DiscordClientId = "450485984333660181";
                    }
                    return settings;
                }
                catch { }
            }
            return new LauncherSettings();
        }

        private void RefreshInstances()
        {
            var list = _instanceManager.GetAllInstances();
            foreach (var inst in list)
            {
                inst.AllocatedMemoryMb = _settings.DefaultMemoryMb;
            }
            _instances = new ObservableCollection<InstanceInfo>(list);
            InstancesContainer.ItemsSource = _instances;

            TxtInstanceCount.Text = $"({_instances.Count})";
            BorderEmptyState.Visibility = _instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateAccountUI()
        {
            TxtAccountName.Text = _activeAccount.Username;
            TxtAccountType.Text = _activeAccount.IsOffline ? "Offline Mode" : "Microsoft License";
        }

        private void AccountBorder_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var wnd = new AccountWindow(_auth, _activeAccount) { Owner = this };
            if (wnd.ShowDialog() == true && wnd.ResultAccount != null)
            {
                _activeAccount = wnd.ResultAccount;
                SaveSavedAccount(_activeAccount);
                UpdateAccountUI();
                LogConsole($"[AETHERIS] Активный профиль изменен: {_activeAccount.Username} ({(_activeAccount.IsOffline ? "Offline" : "Лицензия Microsoft")})");
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var oldDir = _settings.GetEffectiveGameRootDirectory();
            var oldRpc = _settings.EnableDiscordRpc;

            var wnd = new SettingsWindow(_settings, _network) { Owner = this };
            if (wnd.ShowDialog() == true)
            {
                var newDir = _settings.GetEffectiveGameRootDirectory();
                if (!string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                {
                    InitializeGameServices();
                    LogConsole($"[AETHERIS] Папка игры изменена: {newDir}");
                }

                // Handle Discord RPC settings update
                if (_settings.EnableDiscordRpc)
                {
                    if (_discordRpc == null)
                    {
                        _discordRpc = new DiscordRpcService(_settings.DiscordClientId);
                        _discordRpc.LogReceived += LogConsole;
                    }
                    else
                    {
                        _discordRpc.UpdateClientId(_settings.DiscordClientId);
                    }
                    _discordRpc.SetPresenceInLauncher();
                    if (!oldRpc)
                    {
                        LogConsole("[Discord RPC] Интеграция включена.");
                    }
                }
                else if (oldRpc)
                {
                    _discordRpc?.ClearPresence();
                    LogConsole("[Discord RPC] Интеграция отключена.");
                }

                RefreshInstances();
                LogConsole($"[AETHERIS] Настройки сохранены. Выделение памяти: {_settings.DefaultMemoryMb} MB");
            }
        }

        private void BtnToggleConsole_Click(object sender, RoutedEventArgs e)
        {
            _isConsoleVisible = !_isConsoleVisible;
            ColConsole.Width = _isConsoleVisible ? new GridLength(420) : new GridLength(0);
        }

        private async void BtnCopyConsole_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = TxtConsole.Text;
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                Clipboard.SetDataObject(text, true);

                if (sender is Button btn)
                {
                    var origContent = btn.Content;
                    btn.Content = "✓ Скопировано!";
                    await Task.Delay(1800);
                    btn.Content = origContent;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось скопировать логи в буфер обмена: {ex.Message}", "Копирование", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnClearConsole_Click(object sender, RoutedEventArgs e)
        {
            TxtConsole.Clear();
        }

        private async void BtnImportCurseForge_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "CurseForge Modpack (*.zip)|*.zip|Все файлы (*.*)|*.*",
                Title = "Выберите .zip архив сборки CurseForge"
            };

            if (dialog.ShowDialog() == true)
            {
                await RunCurseForgeImport(dialog.FileName);
            }
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0 && files[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    await RunCurseForgeImport(files[0]);
                }
            }
        }

        private async Task RunCurseForgeImport(string zipPath)
        {
            if (!_isConsoleVisible)
            {
                BtnToggleConsole_Click(this, new RoutedEventArgs());
            }

            ProgressBarMain.Value = 0;
            TxtStatus.Text = "Начало импорта сборки CurseForge...";
            LogConsole($"[AETHERIS] Начало импорта из архива: {zipPath}");

            var progress = new Progress<CurseForgeImportProgress>(p =>
            {
                ProgressBarMain.Value = p.Percentage;
                TxtStatus.Text = p.StatusText;
                TxtProgressPercentage.Text = $"{(int)p.Percentage}%";
                if (p.CurrentFile % 10 == 0 || p.CurrentFile == p.TotalFiles)
                {
                    LogConsole($"[IMPORT] {p.StatusText}");
                }
            });

            try
            {
                var imported = await _importer.ImportZipAsync(zipPath, null, progress);
                LogConsole($"[AETHERIS] Сборка {imported.Name} успешно импортирована!");
                MessageBox.Show($"Сборка \"{imported.Name}\" (Minecraft {imported.MinecraftVersion}, {imported.ModLoader}) успешно импортирована!",
                    "Импорт завершен", MessageBoxButton.OK, MessageBoxImage.Information);

                RefreshInstances();
                TxtStatus.Text = $"Сборка {imported.Name} готова к запуску.";
            }
            catch (Exception ex)
            {
                LogConsole($"[IMPORT ERROR] {ex}");
                MessageBox.Show($"Ошибка импорта сборки: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка при импорте сборки.";
            }
            finally
            {
                ProgressBarMain.Value = 100;
            }
        }

        private async void BtnPlayInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InstanceInfo instance)
            {
                // Auto-show console
                if (!_isConsoleVisible)
                {
                    BtnToggleConsole_Click(this, new RoutedEventArgs());
                }

                ProgressBarMain.Value = 10;
                TxtStatus.Text = $"Запуск {instance.Name}...";
                LogConsole($"================== Запуск сборки: {instance.Name} ==================");

                var progress = new Progress<string>(msg =>
                {
                    TxtStatus.Text = msg;
                    LogConsole(msg);
                });

                try
                {
                    if (!_activeAccount.IsOffline && !string.IsNullOrEmpty(_activeAccount.RefreshToken))
                    {
                        try
                        {
                            LogConsole("[Aetheris] Проверка и обновление лицензии сессии Microsoft...");
                            _activeAccount = await _auth.RefreshAccountAsync(_activeAccount);
                            SaveSavedAccount(_activeAccount);
                            UpdateAccountUI();
                        }
                        catch (Exception ex)
                        {
                            LogConsole($"[Aetheris] Предупреждение обновления токена: {ex.Message}");
                        }
                    }

                    var gameProcess = await _launcher.LaunchAsync(instance, _activeAccount, _settings, progress);
                    ProgressBarMain.Value = 100;
                    TxtStatus.Text = $"Игра {instance.Name} запущена!";

                    // Update Discord Presence to Playing
                    if (_discordRpc != null && _settings.EnableDiscordRpc)
                    {
                        var modloaderInfo = string.IsNullOrEmpty(instance.ModLoader) ? "" : $" ({instance.ModLoader})";
                        _discordRpc.SetPresenceInGame(instance.Name, $"Minecraft {instance.MinecraftVersion}{modloaderInfo}", _activeAccount.Username);

                        gameProcess.EnableRaisingEvents = true;
                        gameProcess.Exited += (s, ev) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (_settings.EnableDiscordRpc)
                                {
                                    _discordRpc.SetPresenceInLauncher();
                                }
                            });
                        };
                    }
                }
                catch (Exception ex)
                {
                    LogConsole($"[LAUNCH ERROR] {ex}");
                    MessageBox.Show($"Не удалось запустить Minecraft: {ex.Message}", "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
                    TxtStatus.Text = "Ошибка при запуске игры.";
                }
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InstanceInfo instance)
            {
                var dir = instance.GetDirectory(_instanceManager.GetInstancesRootDirectory());
                Process.Start("explorer.exe", dir);
            }
        }

        private void BtnDeleteInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InstanceInfo instance)
            {
                var result = MessageBox.Show($"Вы уверены, что хотите удалить сборку \"{instance.Name}\"? Все сохранения и миры этой сборки будут удалены.",
                    "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _instanceManager.DeleteInstance(instance);
                    RefreshInstances();
                    LogConsole($"[AETHERIS] Сборка {instance.Name} удалена.");
                }
            }
        }

        private void BtnNewInstance_Click(object sender, RoutedEventArgs e)
        {
            var defaultInst = new InstanceInfo
            {
                Name = $"Instance {DateTime.Now:dd.MM HH:mm}",
                MinecraftVersion = "1.20.1",
                ModLoader = "forge",
                ModLoaderVersion = "47.3.0",
                AllocatedMemoryMb = _settings.DefaultMemoryMb
            };

            _instanceManager.SaveInstance(defaultInst);
            RefreshInstances();
            LogConsole($"[AETHERIS] Создана новая пустая сборка {defaultInst.Name}");
        }

        private void LogConsole(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                TxtConsole.AppendText(line + Environment.NewLine);
                TxtConsole.ScrollToEnd();
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _discordRpc?.Dispose();
            base.OnClosed(e);
        }
    }
}
