using Avalonia.Collections;
using Avalonia.Controls;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public static class LiveStopConfirmationDialog
{
    public static (SukiMessageBoxHost Host, Button Stop) Create()
    {
        var cancel = new Button { Content = "继续演出", IsDefault = true, IsCancel = true };
        var stop = new Button { Content = "停止任务", IsDefault = false, IsCancel = false };
        var host = new SukiMessageBoxHost
        {
            Header = "确认停止演出？",
            Content = "演出正在进行。确认后将停止任务并释放触控；继续演出不会暂停当前任务。",
            IconPreset = SukiMessageBoxIcons.Warning,
            ActionButtonsSource = new AvaloniaList<Button> { cancel, stop }
        };
        return (host, stop);
    }

    public static async Task<bool> ShowAsync()
    {
        var (host, stop) = Create();
        var result = await SukiMessageBox.ShowDialog(host, new SukiMessageBoxOptions { Title = "停止任务" });
        return ReferenceEquals(result, stop);
    }
}
