using Avalonia.Controls;
using MFAAvalonia.ViewModels.UsersControls.Settings;

namespace MFAAvalonia.Views.UserControls.Settings;

public partial class PerformanceProfileSettingsUserControl : UserControl
{
    public PerformanceProfileSettingsUserControl()
    {
        InitializeComponent();
        var model = new PerformanceProfileSettingsUserControlModel();
        DataContext = model;
        AttachedToVisualTree += (_, _) => _ = model.RefreshAsync();
    }
}
