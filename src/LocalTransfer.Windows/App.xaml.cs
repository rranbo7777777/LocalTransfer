using System.ComponentModel;
using LocalTransfer.Coordinator;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace LocalTransfer.Windows;

public partial class App : System.Windows.Application
{
    private CoordinatorHost? _coordinator;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;
    private bool _trayHintShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _coordinator = new CoordinatorHost();
            await _coordinator.StartAsync();
            var window = new MainWindow(_coordinator);
            MainWindow = window;
            window.Closing += OnMainWindowClosing;
            InitializeTrayIcon(window);
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"协调服务启动失败：{exception.Message}",
                "局域传输",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        if (_coordinator is not null)
        {
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }

    private void InitializeTrayIcon(Window window)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem("打开局域传输");
        showItem.Click += (_, _) => Dispatcher.Invoke(() => ShowMainWindow(window));
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "局域传输",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                   ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowMainWindow(window));
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting || sender is not Window window)
        {
            return;
        }

        e.Cancel = true;
        window.Hide();
        if (!_trayHintShown && _trayIcon is not null)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(
                2500,
                "局域传输仍在运行",
                "双击托盘图标可重新打开窗口。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private static void ShowMainWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Shutdown();
    }
}

