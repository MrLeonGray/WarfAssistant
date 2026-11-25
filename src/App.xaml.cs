using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WarfAssistant;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    static App()
    {
        // Snappy project-wide hover tooltips (faster than Windows default 400ms)
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(120));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(60));
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));

        ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkContentElement), new FrameworkPropertyMetadata(120));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(
            typeof(FrameworkContentElement), new FrameworkPropertyMetadata(60));
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(FrameworkContentElement), new FrameworkPropertyMetadata(20000));
    }

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
