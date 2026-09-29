using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Geometries;
using System.Reflection;
using System.Runtime.InteropServices;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Markdown rendering tests skipped: Windows font rendering required");
    return;
}
AppBuilder.Configure<Application>().UseWin32().UseSkia().SetupWithoutStarting();
var font = new FontFamily("avares://SukiUI/CustomFont#Alibaba PuHuiTi 3.0,Microsoft YaHei,Microsoft JhengHei,PingFang SC,Noto Sans CJK SC,Ubuntu,Segoe UI");

foreach (var size in new[] { 13d, 14d, 18d })
foreach (var width in new[] { 670d, 400d, 260d })
foreach (var sample in new[]
{
    (Prefix: "🛠️ 修复 ", Code: "ときめきエクスペリエンス！ (月島まりなver.)", Suffix: " 的重复曲库条目导致准备页与最终封面无法确认歌曲的问题。"),
    (Prefix: "🖥️ 程序入口统一为 ", Code: "MaaBanGDream.exe", Suffix: "，完整包、更新包与启动器同步使用新名称。")
})
{
    var text = new CTextBlock { FontFamily = font, FontSize = size, Foreground = Brushes.White, LineSpacing = 6 };
    text.Content.Add(new CRun { Text = sample.Prefix });
    text.Content.Add(new CCode
    {
        Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(4), Background = Brushes.Gray,
        Content = [new CRun { Text = sample.Code }]
    });
    text.Content.Add(new CRun { Text = sample.Suffix });
    text.Measure(new Size(width, double.PositiveInfinity));
    text.Arrange(new Rect(0, 0, width, text.DesiredSize.Height));
    var geometries = ((IEnumerable<CGeometry>)typeof(CTextBlock)
        .GetField("_metries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(text)!).ToArray();
    var prefix = geometries[0];
    var line = (TextLine)prefix.GetType().GetProperty("Line")!.GetValue(prefix)!;
    Assert(prefix.Width + 0.001 >= line.WidthIncludingTrailingWhitespace,
        $"prefix space lost at font={size}, width={width}: {prefix.Width} < {line.WidthIncludingTrailingWhitespace}");
    var firstCode = geometries.OfType<DecoratorGeometry>().First();
    if (firstCode.Left > 0)
        Assert(firstCode.Left + 0.001 >= prefix.Left + line.WidthIncludingTrailingWhitespace,
            "inline code background starts before the preceding run ends");

    // 对真实字体栅格化结果检查，不能只依赖布局数字判断文字有没有被盖住。
    var prefixOnly = new CTextBlock { FontFamily = font, FontSize = size, Foreground = Brushes.White };
    prefixOnly.Content.Add(new CRun { Text = sample.Prefix });
    var host = new Border { Background = Brushes.Black, Child = prefixOnly, Width = 200, Height = 80 };
    host.Measure(new Size(200, 80));
    host.Arrange(new Rect(0, 0, 200, 80));
    using var bitmap = new RenderTargetBitmap(new PixelSize(400, 160), new Vector(192, 192));
    bitmap.Render(host);
    var stride = 400 * 4;
    var bytes = new byte[stride * 160];
    var buffer = Marshal.AllocHGlobal(bytes.Length);
    try
    {
        bitmap.CopyPixels(new PixelRect(0, 0, 400, 160), buffer, bytes.Length, stride);
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
    }
    finally { Marshal.FreeHGlobal(buffer); }
    var inkRight = 0;
    for (var y = 0; y < 160; y++)
    for (var x = 0; x < 400; x++)
    {
        var offset = y * stride + x * 4;
        if (bytes[offset] + bytes[offset + 1] + bytes[offset + 2] > 30)
            inkRight = Math.Max(inkRight, x + 1);
    }
    if (firstCode.Left > 0)
        Assert(firstCode.Left * 2 + 1 >= inkRight,
            $"inline background covers prefix pixels: left={firstCode.Left * 2}, inkRight={inkRight}");
}
Console.WriteLine("Markdown rendering tests passed: mixed fonts, trailing spaces, inline backgrounds and wrapping");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
