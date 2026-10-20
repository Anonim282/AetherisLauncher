using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using AetherisLauncher.Models;
using AetherisLauncher.Services;

namespace AetherisLauncher.Views
{
    public partial class FirstSetupWindow : Window
    {
        private readonly LauncherSettings _settings;
        private readonly string _sourceDir;

        public FirstSetupWindow(LauncherSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            _sourceDir = AppDomain.CurrentDomain.BaseDirectory;
            TxtInstallPath.Text = _settings.GetEffectiveGameRootDirectory();

            // Check if current base directory has instances or files to migrate
            if (DirectoryMigrationService.HasDataToMigrate(_sourceDir))
            {
                PanelMigration.Visibility = Visibility.Visible;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Выберите папку для Aetheris Launcher",
                InitialDirectory = Directory.Exists(TxtInstallPath.Text) ? TxtInstallPath.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dlg.ShowDialog() == true)
            {
                TxtInstallPath.Text = dlg.FolderName;
            }
        }

        private async void BtnContinue_Click(object sender, RoutedEventArgs e)
        {
            var targetDir = TxtInstallPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                MessageBox.Show("Пожалуйста, укажите папку для установки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(targetDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось создать папку по указанному пути: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BtnContinue.IsEnabled = false;
            BtnBrowse.IsEnabled = false;

            if (ChkMigrate.IsChecked == true && DirectoryMigrationService.HasDataToMigrate(_sourceDir) && !string.Equals(Path.GetFullPath(_sourceDir), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Text = "⏳ Перемещение существующих файлов в новую папку установки...";
                var progress = new Progress<string>(msg => TxtStatus.Text = msg);

                try
                {
                    await DirectoryMigrationService.MigrateGameDirectoryAsync(_sourceDir, targetDir, progress);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Предупреждение при переносе файлов: {ex.Message}", "Перенос данных", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            _settings.GameRootDirectory = targetDir;
            _settings.IsFirstLaunchCompleted = true;

            var configPath = LauncherSettings.GetConfigFilePath();
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            DialogResult = true;
            Close();
        }
    }
}
