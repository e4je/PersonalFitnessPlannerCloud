using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PersonalFitnessPlanner.App.Services;
using PersonalFitnessPlanner.App.ViewModels;

namespace PersonalFitnessPlanner.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var runtime = ParseRuntimeOptions(e.Args);
        Directory.CreateDirectory(runtime.DataDirectory);
        Directory.CreateDirectory(Path.Combine(runtime.DataDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(runtime.DataDirectory, "cache"));
        Directory.CreateDirectory(Path.Combine(runtime.DataDirectory, "backups"));

        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddSingleton<IAppDataService, InfrastructureAppDataAdapter>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider(validateScopes: true);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteEmergencyLog(runtime.DataDirectory, args.Exception);
            _services.GetService<IAppDataService>()?.LogError(args.Exception, "DispatcherUnhandledException");
            if (runtime.SmokeTest)
            {
                args.Handled = true;
                Shutdown(1);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteEmergencyLog(runtime.DataDirectory, ex);
        };

        try
        {
            MainWindow = _services.GetRequiredService<MainWindow>();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            WriteEmergencyLog(runtime.DataDirectory, ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static AppRuntimeOptions ParseRuntimeOptions(string[] args)
    {
        string? dataDirectory = null;
        var offline = false;
        var smokeTest = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length:
                    dataDirectory = Path.GetFullPath(args[++i]);
                    break;
                case "--offline":
                    offline = true;
                    break;
                case "--smoke-test":
                    smokeTest = true;
                    offline = true;
                    break;
            }
        }

        dataDirectory ??= DataDirectoryPointer.ResolveDefault();
        return new AppRuntimeOptions(dataDirectory, offline, smokeTest);
    }

    private static void WriteEmergencyLog(string dataDirectory, Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, $"crash-{DateTime.UtcNow:yyyyMMdd}.log"),
                $"{DateTimeOffset.UtcNow:O} {exception}\n");
        }
        catch
        {
            // The application must never recurse while reporting a fatal exception.
        }
    }
}
