using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AetherisLauncher.Models;
using AetherisLauncher.Services;

namespace AetherisLauncher.Views
{
    public partial class ImportArchiveWindow : Window
    {
        public InstanceInfo? ResultInstance { get; private set; }

        public ImportArchiveWindow(string archivePath, ArchiveScanResult scanResult, int defaultMemoryMb)
        {
            InitializeComponent();

            TxtArchiveName.Text = Path.GetFileName(archivePath);
            TxtArchiveSummary.Text = scanResult.SummaryText;

            TxtInstanceName.Text = scanResult.SuggestedName;
            CmbMinecraftVersion.Text = scanResult.DetectedMinecraftVersion;

            var loader = (scanResult.DetectedModLoader ?? "forge").ToLowerInvariant();
            foreach (ComboBoxItem item in CmbModLoader.Items)
            {
                if (item.Content?.ToString()?.Equals(loader, StringComparison.OrdinalIgnoreCase) == true)
                {
                    CmbModLoader.SelectedItem = item;
                    break;
                }
            }
            if (CmbModLoader.SelectedItem == null)
                CmbModLoader.SelectedIndex = 1; // forge

            TxtLoaderVersion.Text = scanResult.DetectedModLoaderVersion;
            TxtMemoryMb.Text = (scanResult.DetectedMemoryMb > 0 ? scanResult.DetectedMemoryMb : defaultMemoryMb).ToString();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            var name = TxtInstanceName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Введите название сборки.", "Проверка данных", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var mcVersion = CmbMinecraftVersion.Text.Trim();
            if (string.IsNullOrWhiteSpace(mcVersion))
            {
                MessageBox.Show("Укажите версию Minecraft.", "Проверка данных", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var loader = (CmbModLoader.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "vanilla";
            var loaderVersion = TxtLoaderVersion.Text.Trim();

            if (!int.TryParse(TxtMemoryMb.Text.Trim(), out int memoryMb) || memoryMb < 512)
            {
                memoryMb = 4096;
            }

            ResultInstance = new InstanceInfo
            {
                Name = name,
                MinecraftVersion = mcVersion,
                ModLoader = loader,
                ModLoaderVersion = loaderVersion,
                AllocatedMemoryMb = memoryMb
            };

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

