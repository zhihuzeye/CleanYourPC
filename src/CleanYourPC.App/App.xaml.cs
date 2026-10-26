using System.IO;
using System.Text.Json;
using System.Windows;
using CleanYourPC.Core.Models;
using CleanYourPC.Core.Services;

namespace CleanYourPC.App;

public partial class App : Application
{
    public App()
    {
        // 捕获 UI 线程未处理异常，防止程序直接崩溃停止工作
        DispatcherUnhandledException += (sender, e) =>
        {
            var logPath = CrashLogger.LogError("WPF UI 线程异常", e.Exception);
            try
            {
                MessageBox.Show(
                    $"CleanYourPC 拦截到异常并已保护程序继续运行：\n{e.Exception.Message}\n\n详细排查日志已实时记录至：\n{logPath}",
                    "CleanYourPC 异常保护",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { }
            e.Handled = true; // 阻止 Windows 强制关闭程序
        };

        // 捕获非 UI 线程后台异常
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            CrashLogger.LogError("AppDomain 后台未处理异常", ex, $"IsTerminating: {e.IsTerminating}");
        };

        // 捕获异步任务未观测异常
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            CrashLogger.LogError("TaskScheduler 异步未观测异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}