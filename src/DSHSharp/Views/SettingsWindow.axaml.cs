using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DSHSharp.ViewModels;

namespace DSHSharp.Views;

public partial class SettingsWindow : Window
{
    /// <summary>供 XAML 运行时加载器/设计器使用。</summary>
    public SettingsWindow()
        : this(new SettingsViewModel(new Core.Configuration.AppSettings(), string.Empty, _ => { }, () => { }, () => { }, _ => { }))
    {
    }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.CloseRequested -= Close;
    }
}
