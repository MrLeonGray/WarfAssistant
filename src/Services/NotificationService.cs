using System;
using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Windows.Forms;
using WarfAssistant.Services;

namespace WarfAssistant.Services
{
    /// <summary>
    /// Displays native Windows tray balloon/toast notifications and optional sound alerts
    /// for tracked Warframe cycles, Duviri mood changes, and Baro Ki'Teer arrivals.
    /// </summary>
    public sealed class NotificationService : IDisposable
    {
        private static readonly Lazy<NotificationService> _lazy = new(() => new NotificationService());
        public static NotificationService Instance => _lazy.Value;

        private readonly object _lock = new();
        private NotifyIcon? _trayIcon;
        private bool _disposed;

        public bool DesktopNotificationsEnabled { get; private set; }
        public bool SoundNotificationsEnabled { get; private set; }

        private NotificationService()
        {
            var (desktop, sound) = StorageService.Instance.LoadNotificationSettings();
            DesktopNotificationsEnabled = desktop;
            SoundNotificationsEnabled = sound;
        }

        public void SetPreferences(bool desktopEnabled, bool soundEnabled)
        {
            DesktopNotificationsEnabled = desktopEnabled;
            SoundNotificationsEnabled = soundEnabled;
            StorageService.Instance.SaveNotificationSettings(desktopEnabled, soundEnabled);
        }

        public void Notify(string title, string message)
        {
            if (_disposed) return;

            if (SoundNotificationsEnabled)
            {
                try { SystemSounds.Asterisk.Play(); } catch { }
            }

            if (!DesktopNotificationsEnabled) return;

            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted)
                {
                    _ = disp.BeginInvoke(new Action(() => ShowBalloonOnUi(title, message)));
                }
            }
            catch { }
        }

        private void ShowBalloonOnUi(string title, string message)
        {
            lock (_lock)
            {
                if (_disposed) return;
                EnsureTrayIconLocked();
                try
                {
                    _trayIcon?.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
                }
                catch { }
            }
        }

        private void EnsureTrayIconLocked()
        {
            if (_trayIcon != null) return;
            try
            {
                Icon? appIcon = null;
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    appIcon = Icon.ExtractAssociatedIcon(exePath);

                _trayIcon = new NotifyIcon
                {
                    Icon = appIcon ?? SystemIcons.Information,
                    Text = "WarfAssistant",
                    Visible = true
                };
            }
            catch { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_trayIcon != null)
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Dispose();
                        _trayIcon = null;
                    }
                }
                catch { }
            }
        }
    }
}
