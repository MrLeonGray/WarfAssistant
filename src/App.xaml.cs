using System;
using System.Threading.Tasks;
using System.Windows;

namespace WarfAssistant;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    $"Произошла неожиданная ошибка интерфейса:\n{args.Exception.Message}",
                    "WarfAssistant",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.NotificationService.Instance.Dispose();
        base.OnExit(e);
    }
}
