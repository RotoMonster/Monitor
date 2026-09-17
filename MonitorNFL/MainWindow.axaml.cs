using Avalonia.Controls;
using MonitorNFL.ViewModels;

namespace MonitorNFL;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Start();

        Closed += (_, _) => viewModel.Stop();
    }
}
