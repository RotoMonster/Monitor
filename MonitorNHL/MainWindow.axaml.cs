using Avalonia.Controls;
using MonitorNHL.ViewModels;

namespace MonitorNHL;

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
