using System.Windows;
using System.Windows.Input;
using MCCPBuilder.Core;

namespace MCCPBuilder.Launcher;

public partial class OfficialGameInstallWindow : Window
{
    private readonly string _applicationDirectory;
    private readonly OfficialGameRuntimeConfig _config;
    private readonly ResourceRuntimeConfig _resources;

    internal OfficialGameInstallWindow(
        string applicationDirectory,
        OfficialGameRuntimeConfig config,
        ResourceRuntimeConfig resources)
    {
        _applicationDirectory = applicationDirectory;
        _config = config;
        _resources = resources;
        InitializeComponent();
        Loaded += LoadedAsync;
    }

    public bool Succeeded { get; private set; }
    public Exception? Failure { get; private set; }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var progress = new Progress<OfficialGameInstallProgress>(value =>
            {
                ActivityText.Text = value.Activity;
                CountText.Text = value.TotalFiles <= 0
                    ? "处理中"
                    : $"{value.CompletedFiles} / {value.TotalFiles}";
                InstallProgress.IsIndeterminate = value.TotalFiles <= 0;
                InstallProgress.Value = value.TotalFiles <= 0
                    ? 0
                    : value.CompletedFiles * 100d / value.TotalFiles;
            });
            if (_config.Enabled)
            {
                await new OfficialGameInstallService().EnsureInstalledAsync(
                    new(
                        _applicationDirectory,
                        _config.MinecraftRoot,
                        _config.VersionDirectory,
                        _config.ClientJar,
                        _config.Manifest,
                        _config.DownloadConcurrency,
                        _config.ForgeBranding.Enabled,
                        _config.ForgeBranding.Jar,
                        _config.ForgeBranding.Text),
                    progress);
            }
            if (!_resources.Delivery.Equals("CustomServer", StringComparison.OrdinalIgnoreCase))
            {
                await new ExternalResourceInstallService().EnsureInstalledAsync(
                    _applicationDirectory,
                    _resources.Manifest,
                    _config.DownloadConcurrency,
                    progress);
            }
            Succeeded = true;
        }
        catch (Exception exception)
        {
            Failure = exception;
        }
        finally
        {
            Close();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
