using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AetherisLauncher.Models;
using AetherisLauncher.Services;

namespace AetherisLauncher.Views
{
    public partial class AccountWindow : Window
    {
        private readonly AuthService _authService;
        private CancellationTokenSource? _loginCts;
        public AuthAccount? ResultAccount { get; private set; }

        private DeviceCodeResponse? _currentDeviceCode;

        private AuthAccount? _savedMsAccount;

        public AccountWindow(AuthService authService, AuthAccount? currentAccount)
        {
            InitializeComponent();
            _authService = authService;

            if (currentAccount != null && currentAccount.IsOffline)
            {
                TxtOfflineUsername.Text = currentAccount.Username;
            }

            _savedMsAccount = _authService.LoadSavedMicrosoftAccount();
            if (_savedMsAccount != null)
            {
                PanelSavedAccount.Visibility = Visibility.Visible;
                PanelStartLogin.Visibility = Visibility.Collapsed;
                TxtSavedUsername.Text = _savedMsAccount.Username;
            }
            else
            {
                PanelSavedAccount.Visibility = Visibility.Collapsed;
                PanelStartLogin.Visibility = Visibility.Visible;
            }
        }

        private async void BtnUseSavedAccount_Click(object sender, RoutedEventArgs e)
        {
            if (_savedMsAccount != null)
            {
                BtnUseSavedAccount.IsEnabled = false;
                BtnUseSavedAccount.Content = "⏳ Обновление сессии...";
                try
                {
                    ResultAccount = await _authService.RefreshAccountAsync(_savedMsAccount);
                    _authService.SaveAccount(ResultAccount);
                    DialogResult = true;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка обновления сессии: {ex.Message}\nПопробуйте войти заново.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                    PanelSavedAccount.Visibility = Visibility.Collapsed;
                    PanelStartLogin.Visibility = Visibility.Visible;
                    BtnUseSavedAccount.IsEnabled = true;
                    BtnUseSavedAccount.Content = "✅ Выбрать этот аккаунт";
                }
            }
        }

        private void BtnRelogin_Click(object sender, RoutedEventArgs e)
        {
            PanelSavedAccount.Visibility = Visibility.Collapsed;
            PanelStartLogin.Visibility = Visibility.Visible;
        }

        private void BtnApplyOffline_Click(object sender, RoutedEventArgs e)
        {
            var nick = TxtOfflineUsername.Text.Trim();
            if (string.IsNullOrEmpty(nick))
                nick = "Player";

            ResultAccount = _authService.CreateOfflineAccount(nick);
            _authService.SaveOfflineAccount(ResultAccount);
            DialogResult = true;
            Close();
        }

        private async void BtnStartMicrosoftLogin_Click(object sender, RoutedEventArgs e)
        {
            PanelStartLogin.Visibility = Visibility.Collapsed;
            PanelDeviceCode.Visibility = Visibility.Visible;

            _loginCts = new CancellationTokenSource();

            try
            {
                _currentDeviceCode = await _authService.StartDeviceCodeLoginAsync(_loginCts.Token);
                TxtDeviceCode.Text = _currentDeviceCode.user_code;

                // Automatically copy code and open browser
                Clipboard.SetText(_currentDeviceCode.user_code);
                Process.Start(new ProcessStartInfo
                {
                    FileName = _currentDeviceCode.verification_uri,
                    UseShellExecute = true
                });

                // Poll token in background
                ResultAccount = await _authService.PollDeviceCodeTokenAsync(_currentDeviceCode, _loginCts.Token);
                _authService.SaveMicrosoftAccount(ResultAccount);
                MessageBox.Show($"Успешный вход под аккаунтом: {ResultAccount.Username}!", "Авторизация", MessageBoxButton.OK, MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                if (!_loginCts.IsCancellationRequested)
                {
                    MessageBox.Show($"Ошибка входа: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    PanelStartLogin.Visibility = Visibility.Visible;
                    PanelDeviceCode.Visibility = Visibility.Collapsed;
                }
            }
        }

        
        private void BtnCopyCode_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDeviceCode != null)
            {
                Clipboard.SetText(_currentDeviceCode.user_code);
                BtnCopyCode.Content = "✅ Скопировано!";
                _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => BtnCopyCode.Content = "📋 Скопировать код"));
            }
        }

        private void BtnOpenMicrosoftLink_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDeviceCode != null)
            {
                Clipboard.SetText(_currentDeviceCode.user_code);
                Process.Start(new ProcessStartInfo
                {
                    FileName = _currentDeviceCode.verification_uri,
                    UseShellExecute = true
                });
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _loginCts?.Cancel();
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _loginCts?.Cancel();
        }
    }
}
