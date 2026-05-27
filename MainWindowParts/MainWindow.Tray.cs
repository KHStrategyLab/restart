#nullable disable

using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;
using System.Windows.Controls;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private void InitializeTrayIcon()
        {
            _notifyIcon = new TaskbarIcon
            {
                ToolTipText = "KHStrategyLab 감시 중",
                Icon = System.Drawing.SystemIcons.Application
            };

            _notifyIcon.TrayMouseDoubleClick += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };

            var menu = new ContextMenu();

            var open = new MenuItem { Header = "프로그램 열기" };
            open.Click += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };

            var exit = new MenuItem { Header = "완전 종료" };
            exit.Click += (s, e) =>
            {
                _isForceClose = true;
                Application.Current.Shutdown();
            };

            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);

            _notifyIcon.ContextMenu = menu;
        }
    }
}
