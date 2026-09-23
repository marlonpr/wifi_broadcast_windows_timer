using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace FactoryTimer.Controller;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        viewModel = new MainViewModel(DispatcherQueue);
        ((FrameworkElement)Content).DataContext = viewModel;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 980));
        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        ((FrameworkElement)Content).Loaded += Content_Loaded;
    }

    private void ApplyWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ESP32ControllerWindows.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Icon loading is cosmetic and must never prevent the controller from starting.
        }
    }

    private void Content_Loaded(object sender, RoutedEventArgs e) => viewModel.Initialize();

    private async void Start_Click(object sender, RoutedEventArgs e) => await viewModel.SendStartAsync();

    private async void PrepareManualStartAt_Click(object sender, RoutedEventArgs e) =>
        await viewModel.PrepareManualStartAtTestAsync();

    private async void ManualStartAtEsp01_Click(object sender, RoutedEventArgs e) =>
        await viewModel.SendManualStartAtToDeviceAsync("ESP01");

    private async void ManualStartAtEsp02_Click(object sender, RoutedEventArgs e) =>
        await viewModel.SendManualStartAtToDeviceAsync("ESP02");

    private async void ManualStartAtEsp03_Click(object sender, RoutedEventArgs e) =>
        await viewModel.SendManualStartAtToDeviceAsync("ESP03");

    private async void ManualStartAtEsp04_Click(object sender, RoutedEventArgs e) =>
        await viewModel.SendManualStartAtToDeviceAsync("ESP04");

    private async void ManualStartAtEsp05_Click(object sender, RoutedEventArgs e) =>
        await viewModel.SendManualStartAtToDeviceAsync("ESP05");

    private async void CancelManualStartAt_Click(object sender, RoutedEventArgs e) =>
        await viewModel.CancelManualStartAtTestAsync();

    private async void Reset_Click(object sender, RoutedEventArgs e) => await viewModel.SendResetAsync();

    private async void Brightness_Click(object sender, RoutedEventArgs e) =>
        await viewModel.SendBrightnessAsync();

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        viewModel.SelectAllParticipants();

    private void DeselectAll_Click(object sender, RoutedEventArgs e) =>
        viewModel.DeselectAllParticipants();

    private async void Sync_Click(object sender, RoutedEventArgs e) => await viewModel.SynchronizeAsync();

    private async void Benchmark_Click(object sender, RoutedEventArgs e) => await viewModel.RunBenchmarkAsync();

    private void StopBenchmark_Click(object sender, RoutedEventArgs e) => viewModel.CancelBenchmark();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!viewModel.HasManualStartAtActivity) return;

        args.Cancel = true;
        viewModel.ReportCloseBlockedByManualStartAtActivity();
    }

    private void Window_Closed(object sender, WindowEventArgs args) => viewModel.Dispose();
}
