using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using System.Diagnostics;

// 独立于安装目录的 UI 生命周期，文件替换始终在后台线程执行。
internal sealed class UpdaterApplication : Application
{
    private static Action? Update;
    private static string ThemeName = "Light";

    internal static void Run(Action update, string theme)
    {
        Update = update;
        ThemeName = theme;
        AppBuilder.Configure<UpdaterApplication>().UseWin32().UseSkia().StartWithClassicDesktopLifetime([]);
    }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeName.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            var window = new UpdateProgressWindow();
            lifetime.MainWindow = window;
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var finished = false;
            window.Closing += (_, args) => args.Cancel = !finished;
            window.Closed += (_, _) => lifetime.Shutdown(Environment.ExitCode);
            Program.ReportProgress = (title, detail, percent) => Dispatcher.UIThread.Post(() => window.SetProgress(title, detail, percent));
            window.Opened += async (_, _) =>
            {
                await Task.Run(() => Update!());
                Program.ReportProgress = null;
                finished = true;
                if (Program.FailureMessage is { } error)
                    window.ShowFailure(error);
                else
                    lifetime.Shutdown(0);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock Heading = new() { Text = "正在准备更新", FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock Detail = new() { Text = "MaaBanGDream", FontSize = 14, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
    private readonly ProgressBar Progress = new() { Height = 5, IsIndeterminate = true, Minimum = 0, Maximum = 100 };
    private readonly TextBlock Percent = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false };

    public UpdateProgressWindow()
    {
        Title = "MaaBanGDream 更新";
        Width = 520;
        var screen = Screens.Primary;
        if (screen != null) Width = Math.Min(520, screen.WorkingArea.Width / screen.Scaling - 32);
        SizeToContent = SizeToContent.Height;
        MinHeight = 250;
        MaxHeight = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = 32;
        using var icon = typeof(UpdaterApplication).Assembly.GetManifestResourceStream("MFAUpdater.logo.ico");
        if (icon != null) Icon = new WindowIcon(icon);

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 12) };
        using var bitmap = typeof(UpdaterApplication).Assembly.GetManifestResourceStream("MFAUpdater.brand.png");
        if (bitmap != null) brand.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(bitmap), Width = 32, Height = 32 });
        brand.Children.Add(new TextBlock { Text = "MaaBanGDream", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(30, 40, 30, 28) };
        content.Children.Add(brand);
        content.Children.Add(Heading);
        content.Children.Add(new ScrollViewer { Content = Detail, MaxHeight = 200, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        content.Children.Add(Progress);
        content.Children.Add(Percent);
        content.Children.Add(Actions);
        var dark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var tint = Color.Parse(dark ? "#25282E" : "#F5F7F8");
        Content = new ExperimentalAcrylicBorder
        {
            Material = new ExperimentalAcrylicMaterial
            {
                BackgroundSource = AcrylicBackgroundSource.Digger,
                TintColor = tint, TintOpacity = 0.82,
                MaterialOpacity = 0.85, FallbackColor = tint,
            },
            Child = content,
        };
        Foreground = dark ? Brushes.White : Brushes.Black;
        PointerPressed += (_, args) => { if (args.GetPosition(this).Y < 36) BeginMoveDrag(args); };
    }

    internal void SetProgress(string title, string detail, double? percent)
    {
        Heading.Text = title;
        Detail.Text = detail;
        Progress.IsIndeterminate = !percent.HasValue;
        if (percent.HasValue) Progress.Value = Math.Clamp(percent.Value, 0, 100);
        Percent.Text = percent.HasValue ? $"{percent.Value:0}%" : string.Empty;
    }

    internal void ShowFailure(string error)
    {
        SetProgress("更新未完成", error, null);
        Progress.IsVisible = false;
        Actions.IsVisible = true;
        var logs = new Button { Content = "打开日志", MinHeight = 36 };
        logs.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = Program.GetLogDirectory(), UseShellExecute = true }); }
            catch (Exception) { Detail.Text = error + "\n日志目录：" + Program.GetLogDirectory(); }
        };
        var close = new Button { Content = "关闭", MinHeight = 36 };
        close.Click += (_, _) => Close();
        Actions.Children.Add(logs);
        Actions.Children.Add(close);
    }
}
