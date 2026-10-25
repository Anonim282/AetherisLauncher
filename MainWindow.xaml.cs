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
        private readonly DiscordRpcService _discord;
        private readonly UpdateService _updateService;
        private CurseForgeImporter _importer = null!;

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
            _discord = new DiscordRpcService();
            _discord.LogMessage += msg => Dispatcher.Invoke(() => LogConsole(msg));
            _discord.Initialize();
            Closed += (s, e) => _discord.Dispose();

            _updateService = new UpdateService(_network);
            _updateService.LogMessage += msg => Dispatcher.Invoke(() => LogConsole(msg));
            Loaded += (s, e) => _ = CheckForLauncherUpdatesAsync();

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

            LogConsole("==================================================");
            LogConsole("🚀 Aetheris Launcher инициализирован и готов к работе.");
            LogConsole($"📁 Папка сборок: {_instanceManager.GetInstancesRootDirectory()}");
            LogConsole($"👤 Активный профиль: {_activeAccount.Username} ({(_activeAccount.IsOffline ? "Offline" : "Лицензия Microsoft")})");
            LogConsole($"🌐 Сеть: {(_settings.UseBmclapiMirror ? "Зеркало BMCLAPI (активно)" : "Прямое подключение")}");
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
                    return JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
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

            var wnd = new SettingsWindow(_settings, _network) { Owner = this };
            if (wnd.ShowDialog() == true)
            {
                var newDir = _settings.GetEffectiveGameRootDirectory();
                if (!string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                {
                    InitializeGameServices();
                    LogConsole($"[AETHERIS] Папка игры изменена: {newDir}");
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

                    _discord.SetPlaying(instance.Name, instance.MinecraftVersion);
                    gameProcess.EnableRaisingEvents = true;
                    gameProcess.Exited += (s, ev) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _discord.SetInMenu();
                            TxtStatus.Text = $"Игра {instance.Name} закрыта.";
                        });
                    };
                }
                catch (Exception ex)
                {
                    _discord.SetInMenu();
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

        private async Task CheckForLauncherUpdatesAsync()
        {
            try
            {
                var update = await _updateService.CheckForUpdatesAsync();
                if (update != null && update.HasUpdate)
                {
                    LogConsole($"[UPDATER] Найдено новое обновление: {update.VersionString} (текущая версия: v{update.CurrentVersion})");

                    var msg = $"Доступна новая версия лаунчера: {update.VersionString}!\n\n";
                    if (!string.IsNullOrWhiteSpace(update.ChangeLog))
                    {
                        msg += $"Что нового:\n{update.ChangeLog.Trim()}\n\n";
                    }
                    msg += "Хотите обновить лаунчер сейчас?";

                    var result = MessageBox.Show(msg, "Обновление Aetheris Launcher", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        if (!_isConsoleVisible)
                        {
                            BtnToggleConsole_Click(this, new RoutedEventArgs());
                        }

                        ProgressBarMain.Value = 0;
                        TxtStatus.Text = $"Загрузка обновления {update.VersionString}...";

                        var progress = new Progress<double>(pct =>
                        {
                            ProgressBarMain.Value = pct;
                            TxtProgressPercentage.Text = $"{(int)pct}%";
                            TxtStatus.Text = $"Загрузка обновления: {(int)pct}%";
                        });

                        await _updateService.DownloadAndApplyUpdateAsync(update.DownloadUrl, progress);
                    }
                }
                else
                {
                    LogConsole("[UPDATER] У вас установлена актуальная версия лаунчера.");
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[UPDATER] Ошибка при проверке обновлений: {ex.Message}");
            }
        }
    }
}
