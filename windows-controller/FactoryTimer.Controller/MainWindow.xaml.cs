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
        viewModel = new MainViewModel(DispatcherQueue);
        ((FrameworkElement)Content).DataContext = viewModel;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 980));
        Closed += Window_Closed;
        ((FrameworkElement)Content).Loaded += Content_Loaded;
    }

    private void Content_Loaded(object sender, RoutedEventArgs e) => viewModel.Initialize();

    private async void Start_Click(object sender, RoutedEventArgs e) => await viewModel.SendStartAsync();

    private async void Reset_Click(object sender, RoutedEventArgs e) => await viewModel.SendResetAsync();

    private async void Sync_Click(object sender, RoutedEventArgs e) => await viewModel.SynchronizeAsync();

    private async void Benchmark_Click(object sender, RoutedEventArgs e) => await viewModel.RunBenchmarkAsync();

    private void StopBenchmark_Click(object sender, RoutedEventArgs e) => viewModel.CancelBenchmark();

    private void Window_Closed(object sender, WindowEventArgs args) => viewModel.Dispose();
}
