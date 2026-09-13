using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ZeroSync.ViewModels;
using Application = System.Windows.Application;

namespace ZeroSync
{
    public partial class MainWindow : Window
    {
        private NotifyIcon _trayIcon;
        private bool _forceClose = false; // true khi chọn "Thoát" từ tray menu

        public MainWindow()
        {
            InitializeComponent();

            // Auto-scroll log với độ ưu tiên Background giúp hoãn việc cuộn cho đến khi UI rảnh rỗi, tránh giật lag khi có nhiều log
            var vm = (MainViewModel)DataContext;
            vm.LogEntries.CollectionChanged += (s, e) =>
            {
                if (LogListBox.Items.Count > 0)
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                    {
                        if (LogListBox.Items.Count > 0)
                            LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
                    }));
                }
            };

            InitTray();

            // Xử lý StartMinimized: sau khi window render xong mới ẩn
            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm != null && vm.StartMinimized && vm.MinimizeToTray)
            {
                HideToTray(showBalloon: false);
            }
        }

        private void InitTray()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "ZeroSync",
                Visible = false
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Mở ZeroSync", null, (s, e) => ShowWindow());
            menu.Items.Add("-");
            menu.Items.Add("Thoát", null, (s, e) =>
            {
                _forceClose = true;
                _trayIcon.Visible = false;
                var vm = DataContext as MainViewModel;
                vm?.OnClosing();
                _trayIcon?.Dispose();
                Application.Current.Shutdown();
            });
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => ShowWindow();
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            _trayIcon.Visible = false;
        }

        private void HideToTray(bool showBalloon = true)
        {
            Hide();
            _trayIcon.Visible = true;
            if (showBalloon)
                _trayIcon.ShowBalloonTip(2000, "ZeroSync", "Ứng dụng đang chạy ẩn ở đây", ToolTipIcon.Info);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            var vm = DataContext as MainViewModel;
            if (WindowState == WindowState.Minimized && vm != null && vm.MinimizeToTray)
            {
                HideToTray(showBalloon: true);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceClose) return; // Đã xử lý ở menu Thoát

            var vm = DataContext as MainViewModel;
            if (vm != null && vm.MinimizeToTray)
            {
                // Ẩn xuống tray thay vì đóng thật
                e.Cancel = true;
                HideToTray(showBalloon: true);
                return;
            }

            // Đóng thật
            vm?.OnClosing();
            _trayIcon?.Dispose();
        }
    }
}
