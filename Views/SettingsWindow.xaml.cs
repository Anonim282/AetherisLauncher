using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using AetherisLauncher.Models;
using AetherisLauncher.Services;

namespace AetherisLauncher.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly LauncherSettings _settings;
        private readonly NetworkService _network;

        public SettingsWindow(LauncherSettings settings, NetworkService network)
        {
            InitializeComponent();
            _settings = settings;
            _network = network;

            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            TxtGameRoot.Text = _settings.GetEffectiveGameRootDirectory();
            SliderMemory.Value = _settings.DefaultMemoryMb;
            TxtMemoryValue.Text = $"{_settings.DefaultMemoryMb} MB";

            // Find Javas
            var javas = JavaDetector.FindInstalledJavas();
            CmbJava.ItemsSource = javas;
            if (!string.IsNullOrEmpty(_settings.SelectedJavaPath))
            {
                var selected = javas.Find(j => j.Path.Equals(_settings.SelectedJavaPath, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                    CmbJava.SelectedItem = selected;
            }
            if (CmbJava.SelectedItem == null && javas.Count > 0)
                CmbJava.SelectedIndex = 0;

            // Proxy
            switch (_settings.ProxyMode)
            {
                case ProxyMode.Direct:
                    RbDirect.IsChecked = true;
                    break;
                case ProxyMode.SystemProxy:
                    RbSystemProxy.IsChecked = true;
                    break;
                case ProxyMode.CustomProxy:
                    RbCustomProxy.IsChecked = true;
                    PanelCustomProxy.Visibility = Visibility.Visible;
                    break;
            }

            TxtProxyHost.Text = _settings.CustomProxyHost;
            TxtProxyPort.Text = _settings.CustomProxyPort.ToString();
            ChkUseMirror.IsChecked = _settings.UseBmclapiMirror;
        }

        private void SliderMemory_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtMemoryValue != null)
            {
                int val = (int)(Math.Round(e.NewValue / 512.0) * 512);
                TxtMemoryValue.Text = $"{val} MB";
            }
        }

        private void RbCustomProxy_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelCustomProxy != null)
                PanelCustomProxy.Visibility = Visibility.Visible;
        }

        private void RbCustomProxy_Unchecked(object sender, RoutedEventArgs e)
        {
            if (PanelCustomProxy != null)
                PanelCustomProxy.Visibility = Visibility.Collapsed;
        }

        
        private void BtnBrowseGameRoot_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите новую папку установки игры",
                InitialDirectory = Directory.Exists(TxtGameRoot.Text) ? TxtGameRoot.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dlg.ShowDialog() == true)
            {
                TxtGameRoot.Text = dlg.FolderName;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var newRoot = TxtGameRoot.Text.Trim();
            var oldRoot = _settings.GetEffectiveGameRootDirectory();

            if (!string.IsNullOrWhiteSpace(newRoot) && !string.Equals(Path.GetFullPath(newRoot), Path.GetFullPath(oldRoot), StringComparison.OrdinalIgnoreCase))
            {
                if (DirectoryMigrationService.HasDataToMigrate(oldRoot))
                {
                    var msgRes = MessageBox.Show(
                        $"Вы изменили папку установки игры.\n\nПереместить существующие сборки, моды и компоненты в новую папку?\n\nОткуда: {oldRoot}\nКуда: {newRoot}",
                        "Смена директории игры",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (msgRes == MessageBoxResult.Cancel)
                        return;

                    if (msgRes == MessageBoxResult.Yes)
                    {
                        try
                        {
                            await DirectoryMigrationService.MigrateGameDirectoryAsync(oldRoot, newRoot);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Ошибка при перемещении файлов: {ex.Message}", "Ошибка переноса", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
                _settings.GameRootDirectory = newRoot;
            }

            _settings.DefaultMemoryMb = (int)(Math.Round(SliderMemory.Value / 512.0) * 512);

            if (CmbJava.SelectedItem is JavaInstallation selectedJava)
            {
                _settings.SelectedJavaPath = selectedJava.Path;
            }

            if (RbDirect.IsChecked == true)
                _settings.ProxyMode = ProxyMode.Direct;
            else if (RbCustomProxy.IsChecked == true)
                _settings.ProxyMode = ProxyMode.CustomProxy;
            else
                _settings.ProxyMode = ProxyMode.SystemProxy;

            _settings.CustomProxyHost = TxtProxyHost.Text.Trim();
            if (int.TryParse(TxtProxyPort.Text.Trim(), out int port))
                _settings.CustomProxyPort = port;

            _settings.UseBmclapiMirror = ChkUseMirror.IsChecked == true;

            // Save to file
            var configPath = LauncherSettings.GetConfigFilePath();
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            // Rebuild network client with new settings
            _network.RebuildClient();

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
