using Avalonia.Controls;
using Avalonia.Input;
using MFAAvalonia.ViewModels.UsersControls.Settings;

namespace MFAAvalonia.Views.UserControls.Settings;

public partial class PerformanceProfileSettingsUserControl : UserControl
{
    public PerformanceProfileSettingsUserControl()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is PerformanceProfileSettingsUserControlModel model)
                _ = model.EnsureLoadedAsync();
        };
    }

    private async void ProfilesGrid_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: PerformanceProfileItem profile }
            || DataContext is not PerformanceProfileSettingsUserControlModel model)
            return;
        await model.SetCurrentProfileAsync(profile);
    }
}
