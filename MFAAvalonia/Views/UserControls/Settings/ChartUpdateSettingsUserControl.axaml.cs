using Avalonia.Controls;
using MFAAvalonia.ViewModels.UsersControls.Settings;

namespace MFAAvalonia.Views.UserControls.Settings;

public partial class ChartUpdateSettingsUserControl : UserControl
{
    public ChartUpdateSettingsUserControl()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is PerformanceProfileSettingsUserControlModel model)
                _ = model.EnsureLoadedAsync();
        };
    }
}
