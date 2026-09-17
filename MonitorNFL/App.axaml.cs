using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Monitor.Core;
using MonitorNFL.ViewModels;

namespace MonitorNFL;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.local.json", optional: true)
                .Build();

            var settings = new MonitorSettings();
            config.Bind(settings);

            var nfl = new NflSettings();
            config.GetSection("Nfl").Bind(nfl);

            desktop.MainWindow = new MainWindow(new MainViewModel(settings, nfl));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
