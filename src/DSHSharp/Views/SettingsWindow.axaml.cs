using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DSHSharp.ViewModels;

namespace DSHSharp.Views;

public partial class SettingsWindow : Window
{
    /// <summary>供 XAML 运行时加载器/设计器使用。</summary>
    public SettingsWindow()
        : this(new SettingsViewModel(new Core.Configuration.AppSettings(), string.Empty, _ => { }, () => { }, () => { }))
    {
    }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.CloseRequested -= Close;
    }

    private async void BrowseSourcePath_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 DSH 源码仓库目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
        {
            vm.SourcePath = folders[0].Path.LocalPath;
        }
    }
}
