using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MCCPBuilder.Core;

namespace MCCPBuilder.Launcher;

internal sealed class UpdateProgressWindow : Window
{
    private readonly string _applicationDirectory;
    private readonly UpdateBootstrapConfig _bootstrap;
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;
    private readonly TextBlock _downloadSpeed;
    private readonly Button _pauseButton;
    private readonly Button _cancelButton;
    private readonly DispatcherTimer _speedTimer;
    private readonly DownloadPauseController _pauseController = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private long _latestDownloadedBytes;
    private long _speedSampleBytes;
    private long _speedSampleTimestamp;
    private bool _downloadStageActive;

    public UpdateProgressWindow(
        string applicationDirectory,
        UpdateBootstrapConfig bootstrap)
    {
        _applicationDirectory = applicationDirectory;
        _bootstrap = bootstrap;
        Title = "正在检查更新";
        Width = 620;
        Height = 400;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(
            this,
            TextFormattingMode.Display);

        _message = new TextBlock
        {
            Text = "正在连接更新服务器…",
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinHeight = 42,
            MaxHeight = 64,
            Foreground = Brush("#F7F9FC"),
            FontSize = 15,
            LineHeight = 23,
            Margin = new Thickness(0, 8, 0, 16)
        };
        _progress = new ProgressBar
        {
            Height = 14,
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100,
            Foreground = Brush("#59B96A"),
            Background = Brush("#26313E"),
            BorderBrush = Brush("#596879"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12)
        };
        _downloadSpeed = new TextBlock
        {
            Text = "下载速度：等待下载",
            Foreground = Brush("#AEB8C7"),
            Margin = new Thickness(0, 0, 0, 18)
        };
        _pauseButton = new Button
        {
            Content = "暂停下载",
            MinWidth = 112,
            Height = 40,
            Padding = new Thickness(18, 8, 18, 8),
            Style = CreateButtonStyle("#364150", "#FFFFFF"),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = false
        };
        _pauseButton.Click += TogglePause;
        _cancelButton = new Button
        {
            Content = "取消更新",
            MinWidth = 112,
            Height = 40,
            Padding = new Thickness(18, 8, 18, 8),
            Style = CreateButtonStyle("#364150", "#FFFFFF"),
            Margin = new Thickness(10, 0, 0, 0)
        };
        _cancelButton.Click += CancelUpdate;
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _pauseButton, _cancelButton }
        };

        var statusDot = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = Brush("#59B96A"),
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        var title = new TextBlock
        {
            Text = "检查并应用更新",
            Foreground = Brush("#F7F9FC"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        };
        var subtitle = new TextBlock
        {
            Text = "更新完成前请不要关闭启动器",
            Foreground = Brush("#AEB8C7"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        var heading = new StackPanel
        {
            Children = { title, subtitle }
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { statusDot, heading }
        };
        var body = new StackPanel
        {
            Children =
            {
                header,
                _message,
                _progress,
                _downloadSpeed,
                buttonPanel
            }
        };
        var bodyContainer = new Grid
        {
            Margin = new Thickness(20),
            Children =
            {
                new Border
                {
                    Padding = new Thickness(24, 22, 24, 22),
                    CornerRadius = new CornerRadius(16),
                    Background = Brush("#F0181F29"),
                    BorderBrush = Brush("#69778799"),
                    BorderThickness = new Thickness(1),
                    Child = body
                }
            }
        };
        var titleBar = CreateTitleBar();
        var layout = new Grid();
        layout.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(48)
            });
        layout.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(bodyContainer, 1);
        layout.Children.Add(titleBar);
        layout.Children.Add(bodyContainer);
        Content = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brush("#10151D"),
            Child = layout
        };
        _speedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _speedTimer.Tick += UpdateDownloadSpeed;
        ContentRendered += StartUpdate;
        Closing += (_, eventArgs) =>
        {
            if (!Succeeded && Failure is null)
            {
                BeginCancellation();
                eventArgs.Cancel = true;
            }
        };
    }

    private Grid CreateTitleBar()
    {
        var icon = LauncherIconService.TryLoadExecutableIcon();
        FrameworkElement mark;
        if (icon is not null)
        {
            Icon = icon;
            mark = new Image
            {
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform
            };
        }
        else
        {
            mark = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = Brush("#59B96A"),
                Child = new TextBlock
                {
                    Text = "M",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush("#0B170D"),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold
                }
            };
        }

        var caption = new TextBlock
        {
            Text = Title,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#DDE4EE"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        var captionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { mark, caption }
        };

        var minimizeButton = CreateWindowButton(
            "M 2,7 L 12,7",
            closeButton: false);
        minimizeButton.ToolTip = "最小化";
        minimizeButton.Click += (_, _) =>
            WindowState = WindowState.Minimized;
        var closeButton = CreateWindowButton(
            "M 3,3 L 11,11 M 11,3 L 3,11",
            closeButton: true);
        closeButton.ToolTip = "关闭";
        closeButton.Click += (_, _) => BeginCancellation();
        var windowButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { minimizeButton, closeButton }
        };

        var titleBar = new Grid
        {
            Margin = new Thickness(14, 4, 8, 4),
            Background = Brushes.Transparent
        };
        titleBar.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        titleBar.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = GridLength.Auto
            });
        Grid.SetColumn(windowButtons, 1);
        titleBar.Children.Add(captionPanel);
        titleBar.Children.Add(windowButtons);
        titleBar.MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (eventArgs.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        };
        return titleBar;
    }

    private static Button CreateWindowButton(
        string iconGeometry,
        bool closeButton)
    {
        var icon = new Path
        {
            Data = Geometry.Parse(iconGeometry),
            Stroke = Brush("#DDE4EE"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square,
            StrokeLineJoin = PenLineJoin.Miter,
            SnapsToDevicePixels = true
        };
        return new Button
        {
            Width = 46,
            Height = 40,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Arrow,
            Style = CreateWindowButtonStyle(closeButton),
            Content = new Viewbox
            {
                Width = 14,
                Height = 14,
                Child = icon
            }
        };
    }

    public bool Succeeded { get; private set; }
    public Exception? Failure { get; private set; }
    public UpdateResult? Result { get; private set; }

    private async void StartUpdate(object? sender, EventArgs eventArgs)
    {
        ContentRendered -= StartUpdate;
        ResetSpeedSample();
        _speedTimer.Start();
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                _message.Text = value.Message;
                _latestDownloadedBytes = Math.Max(
                    _latestDownloadedBytes,
                    value.CompletedBytes);
                if (value.TotalBytes > 0)
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = Math.Clamp(
                        _latestDownloadedBytes * 100d / value.TotalBytes,
                        0,
                        100);
                }
                else
                {
                    _progress.IsIndeterminate =
                        value.Stage is "Checking" or "Preparing";
                    if (!_progress.IsIndeterminate)
                    {
                        _progress.Value = 0;
                    }
                }

                var isDownloadStage =
                    (value.Stage is "Downloading" or "LauncherUpdate") &&
                    value.TotalBytes > 0 &&
                    value.CompletedBytes < value.TotalBytes;
                if (isDownloadStage && !_downloadStageActive)
                {
                    ResetSpeedSample();
                }

                _downloadStageActive = isDownloadStage;
                _pauseButton.IsEnabled = isDownloadStage;
                if (!isDownloadStage)
                {
                    _pauseController.Resume();
                    _pauseButton.Content = "暂停下载";
                    _downloadSpeed.Text =
                        value.Stage is "Applying" or "Cleaning" or "Complete"
                            ? "下载速度：已完成"
                            : "下载速度：等待下载";
                }
            });
            var result = await new ClientUpdateService().CheckAndApplyAsync(
                _applicationDirectory,
                _bootstrap,
                progress,
                _updateCancellation.Token,
                pauseController: _pauseController);
            Result = result;
            if (result.LauncherUpdate is not null)
            {
                ProgramLog.Write(
                    $"已下载启动器新版 {result.LauncherUpdate.Version}，" +
                    "即将退出旧启动器并在原路径安装。");
            }
            else
            {
                ProgramLog.Write(
                    result.Updated
                        ? $"客户端更新完成：{result.Version}，下载 " +
                          $"{result.DownloadedFiles} 个文件。"
                        : $"客户端已是最新版本：{result.Version}。");
            }
            Succeeded = true;
        }
        catch (Exception exception)
        {
            Failure =
                exception is OperationCanceledException &&
                _updateCancellation.IsCancellationRequested
                    ? new OperationCanceledException(
                        "更新已由用户取消，已阻止启动游戏。",
                        exception)
                    : exception;
            ProgramLog.Write(
                $"强制更新检查失败：{Failure.GetType().Name}: " +
                Failure.Message);
        }
        finally
        {
            _pauseController.Resume();
            _speedTimer.Stop();
            _pauseButton.IsEnabled = false;
            _cancelButton.IsEnabled = false;
            DialogResult = Succeeded;
            Close();
            _updateCancellation.Dispose();
        }
    }

    private void TogglePause(object sender, RoutedEventArgs eventArgs)
    {
        if (_pauseController.IsPaused)
        {
            _pauseController.Resume();
            _pauseButton.Content = "暂停下载";
            _downloadSpeed.Text = "下载速度：正在计算…";
            ResetSpeedSample();
            return;
        }

        if (_pauseController.Pause())
        {
            _pauseButton.Content = "继续下载";
            _downloadSpeed.Text = "下载速度：0 B/s（已暂停）";
        }
    }

    private void CancelUpdate(object sender, RoutedEventArgs eventArgs) =>
        BeginCancellation();

    private void BeginCancellation()
    {
        if (_updateCancellation.IsCancellationRequested)
        {
            return;
        }

        _message.Text = "正在取消更新…";
        _downloadSpeed.Text = "下载速度：正在停止";
        _pauseController.Resume();
        _pauseButton.Content = "暂停下载";
        _pauseButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        _updateCancellation.Cancel();
    }

    private void UpdateDownloadSpeed(
        object? sender,
        EventArgs eventArgs)
    {
        if (!_downloadStageActive)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _speedSampleTimestamp) /
                      (double)Stopwatch.Frequency;
        if (elapsed <= 0)
        {
            return;
        }

        var completed = _latestDownloadedBytes;
        if (_pauseController.IsPaused)
        {
            _downloadSpeed.Text = "下载速度：0 B/s（已暂停）";
        }
        else
        {
            var bytesPerSecond =
                Math.Max(0, completed - _speedSampleBytes) / elapsed;
            _downloadSpeed.Text =
                $"下载速度：{FormatDownloadSpeed(bytesPerSecond)}";
        }

        _speedSampleBytes = completed;
        _speedSampleTimestamp = now;
    }

    private void ResetSpeedSample()
    {
        _speedSampleBytes = _latestDownloadedBytes;
        _speedSampleTimestamp = Stopwatch.GetTimestamp();
    }

    private static string FormatDownloadSpeed(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = bytesPerSecond;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.00} {units[unit]}";
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Style CreateButtonStyle(
        string background,
        string foreground)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(
            Border.BackgroundProperty,
            new Binding(nameof(Button.Background))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
        border.SetValue(
            Border.CornerRadiusProperty,
            new CornerRadius(8));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(
            ContentPresenter.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        content.SetValue(
            ContentPresenter.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        content.SetBinding(
            ContentPresenter.ContentProperty,
            new Binding(nameof(ContentControl.Content))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
        content.SetBinding(
            ContentPresenter.ContentTemplateProperty,
            new Binding(nameof(ContentControl.ContentTemplate))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
        content.SetBinding(
            ContentPresenter.ContentStringFormatProperty,
            new Binding(nameof(ContentControl.ContentStringFormat))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
        content.SetBinding(
            ContentPresenter.MarginProperty,
            new Binding(nameof(Button.Padding))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };
        template.Triggers.Add(
            new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
                Setters =
                {
                    new Setter(UIElement.OpacityProperty, 0.88)
                }
            });
        template.Triggers.Add(
            new Trigger
            {
                Property = Button.IsPressedProperty,
                Value = true,
                Setters =
                {
                    new Setter(UIElement.OpacityProperty, 0.7)
                }
            });
        template.Triggers.Add(
            new Trigger
            {
                Property = UIElement.IsEnabledProperty,
                Value = false,
                Setters =
                {
                    new Setter(UIElement.OpacityProperty, 0.45)
                }
            });

        var style = new Style(typeof(Button));
        style.Setters.Add(
            new Setter(
                Control.BackgroundProperty,
                Brush(background)));
        style.Setters.Add(
            new Setter(
                Control.ForegroundProperty,
                Brush(foreground)));
        style.Setters.Add(
            new Setter(
                Control.BorderThicknessProperty,
                new Thickness(0)));
        style.Setters.Add(
            new Setter(
                FrameworkElement.CursorProperty,
                System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateWindowButtonStyle(bool closeButton)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(
            Border.BackgroundProperty,
            new Binding(nameof(Button.Background))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
        border.SetValue(
            Border.CornerRadiusProperty,
            new CornerRadius(7));

        var content = new FrameworkElementFactory(
            typeof(ContentPresenter));
        content.SetValue(
            ContentPresenter.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        content.SetValue(
            ContentPresenter.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        content.SetBinding(
            ContentPresenter.ContentProperty,
            new Binding(nameof(ContentControl.Content))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };
        template.Triggers.Add(
            new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
                Setters =
                {
                    new Setter(
                        Control.BackgroundProperty,
                        Brush(closeButton ? "#D9434E" : "#354050"))
                }
            });
        template.Triggers.Add(
            new Trigger
            {
                Property = Button.IsPressedProperty,
                Value = true,
                Setters =
                {
                    new Setter(UIElement.OpacityProperty, 0.72)
                }
            });

        var style = new Style(typeof(Button));
        style.Setters.Add(
            new Setter(
                Control.BackgroundProperty,
                Brushes.Transparent));
        style.Setters.Add(
            new Setter(
                Control.BorderThicknessProperty,
                new Thickness(0)));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}

internal static class ProgramLog
{
    public static Action<string> Write { get; set; } = _ => { };
}
