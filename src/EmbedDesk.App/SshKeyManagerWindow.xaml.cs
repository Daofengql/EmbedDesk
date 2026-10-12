using System.Windows;
using EmbedDesk.App.ViewModels;
using Microsoft.Win32;

namespace EmbedDesk.App;

public partial class SshKeyManagerWindow : Window
{
    private readonly RemoteDebugWorkspaceViewModel _workspace;
    public System.Collections.ObjectModel.ObservableCollection<string> SshKeys => _workspace.SshKeys;
    public string? SelectedKey { get; set; }
    public SshKeyManagerWindow(RemoteDebugWorkspaceViewModel workspace) { InitializeComponent(); _workspace = workspace; DataContext = this; }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SSH 私钥|*.pem;*.key;id_*|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) foreach (var path in dialog.FileNames) _workspace.AddSshKey(path);
    }
    private void Remove_Click(object sender, RoutedEventArgs e) => _workspace.RemoveSshKey(SelectedKey);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
