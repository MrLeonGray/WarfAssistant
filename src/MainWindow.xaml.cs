using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WarfAssistant.Services;

namespace WarfAssistant
{
    public partial class MainWindow : Window
    {
        private Rect _lastNormalBounds = new(100, 100, 1380, 860);
        private bool _placementLoaded;

        public MainWindow()
        {
            InitializeComponent();
            ApplySavedWindowPlacement();
            SourceInitialized += MainWindow_SourceInitialized;
            LocationChanged += (_, _) => RecordNormalBounds();
            SizeChanged += (_, _) => RecordNormalBounds();
            Closing += MainWindow_Closing;
        }

        private void ApplySavedWindowPlacement()
        {
            var wp = StorageService.Instance.LoadWindowPlacement();
            double minW = Math.Max(1200, MinWidth);
            double minH = Math.Max(740, MinHeight);

            if (wp != null && wp.Width >= minW && wp.Height >= minH)
            {
                double vLeft = SystemParameters.VirtualScreenLeft;
                double vTop = SystemParameters.VirtualScreenTop;
                double vW = SystemParameters.VirtualScreenWidth;
                double vH = SystemParameters.VirtualScreenHeight;

                double w = Math.Min(wp.Width, Math.Max(minW, vW));
                double h = Math.Min(wp.Height, Math.Max(minH, vH));
                double l = double.IsNaN(wp.Left) ? vLeft + (vW - w) * 0.5 : Math.Clamp(wp.Left, vLeft - w + 160, vLeft + vW - 160);
                double t = double.IsNaN(wp.Top) ? vTop + (vH - h) * 0.5 : Math.Clamp(wp.Top, vTop, vTop + vH - 80);

                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
                Width = w;
                Height = h;
                _lastNormalBounds = new Rect(l, t, w, h);

                if (wp.IsMaximized)
                    WindowState = WindowState.Maximized;
            }
            else
            {
                _lastNormalBounds = new Rect(Left, Top, Math.Max(minW, Width), Math.Max(minH, Height));
            }
            _placementLoaded = true;
        }

        private void RecordNormalBounds()
        {
            if (!_placementLoaded || WindowState != WindowState.Normal) return;
            if (Width >= MinWidth && Height >= MinHeight && !double.IsNaN(Left) && !double.IsNaN(Top))
                _lastNormalBounds = new Rect(Left, Top, Width, Height);
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            RecordNormalBounds();
            StorageService.Instance.SaveWindowPlacement(new WindowPlacementData
            {
                Left = _lastNormalBounds.Left,
                Top = _lastNormalBounds.Top,
                Width = Math.Max(MinWidth, _lastNormalBounds.Width),
                Height = Math.Max(MinHeight, _lastNormalBounds.Height),
                IsMaximized = WindowState == WindowState.Maximized
            });
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var hwndSource = HwndSource.FromHwnd(handle);
            hwndSource?.AddHook(WindowProc);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            const int MONITOR_DEFAULTTONEAREST = 0x00000002;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO();
                monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    RECT rcWorkArea = monitorInfo.rcWork;
                    RECT rcMonitorArea = monitorInfo.rcMonitor;

                    mmi.ptMaxPosition.x = rcWorkArea.left - rcMonitorArea.left;
                    mmi.ptMaxPosition.y = rcWorkArea.top - rcMonitorArea.top;
                    mmi.ptMaxSize.x = rcWorkArea.right - rcWorkArea.left;
                    mmi.ptMaxSize.y = rcWorkArea.bottom - rcWorkArea.top;
                    mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                    mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;

                    var dpi = VisualTreeHelper.GetDpi(this);
                    double minW = Math.Max(1200, MinWidth);
                    double minH = Math.Max(740, MinHeight);
                    mmi.ptMinTrackSize.x = (int)Math.Ceiling(minW * dpi.DpiScaleX);
                    mmi.ptMinTrackSize.y = (int)Math.Ceiling(minH * dpi.DpiScaleY);
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized 
                ? WindowState.Normal 
                : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
