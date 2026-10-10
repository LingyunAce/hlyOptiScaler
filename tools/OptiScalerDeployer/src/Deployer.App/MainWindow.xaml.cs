using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Deployer.App.Models;
using Deployer.App.ViewModels;
using Deployer.Core.Domain;

namespace Deployer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closeRequested, _actualCloseAllowed;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        _vm.CloseRequested += OnCloseRequested;
        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_actualCloseAllowed || _vm.CanClose) return;
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        _vm.RequestClose();
    }
    private void OnCloseRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { _actualCloseAllowed = true; Close(); });

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = "选择游戏安装目录", Multiselect = false };
        if (folder.ShowDialog() != true) return;
        var titleDialog = new TextInputWindow("添加手动游戏目录", "游戏名称", System.IO.Path.GetFileName(folder.FolderName));
        titleDialog.Owner = this; if (titleDialog.ShowDialog() != true) return;
        await _vm.AddManualFolderAsync(titleDialog.Value, folder.FolderName);
    }

    private void BrowsePayload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含 OptiScaler.dll 的 Payload 目录", Multiselect = false };
        if (dialog.ShowDialog() == true) _vm.PayloadDirectory = dialog.FolderName;
    }

    private void SavePlan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "保存当前部署计划", Filter = "JSON 文件 (*.json)|*.json", FileName = "deployment-plan.json", AddExtension = true };
        if (dialog.ShowDialog(this) == true) _vm.SavePlanCommand.Execute(dialog.FileName);
    }

    private void RollbackManifest_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要恢复的 manifest.json", Filter = "部署清单 (manifest.json)|manifest.json|JSON 文件 (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _vm.RollbackCommand.Execute(dialog.FileName);
    }

    private async void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ReloadHistoryAsync();
        var dialog = new HistoryWindow(_vm.History) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedManifestPath is { } path) _vm.RollbackCommand.Execute(path);
    }
    private void AllowOverwrite_Click(object sender, RoutedEventArgs e) => _vm.ResolveOverwriteCommand.Execute(null);
    private void PreferAsi_Click(object sender, RoutedEventArgs e) => _vm.PreferAsiCommand.Execute(null);
    private void CancelIdentity_Click(object sender, RoutedEventArgs e) => _vm.CancelPlanCommand.Execute(null);
    private void HealthLog_Click(object sender, RoutedEventArgs e)
    {
        new Window
        {
            Owner = this, Title = "本次健康检查日志", Width = 800, Height = 500,
            Content = new TextBox { Text = _vm.HealthLog, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) },
        }.ShowDialog();
    }
    private async void DisableLogging_Click(object sender, RoutedEventArgs e) => await _vm.DisableLoggingAsync();

    private async void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is null) return;
        if (_vm.SelectedProfile.IsBuiltin) _vm.CloneProfileCommand.Execute(null);
        if (_vm.SelectedProfile is not { } profile) return;
        var dialog = new ProfileEditorWindow(profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is not { } edited) return;
        await _vm.SaveProfileAsync(edited);
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is not { IsBuiltin: false } profile) return;
        if (MessageBox.Show(this, $"删除用户配置“{profile.DisplayName}”？此操作不会修改游戏文件。", "删除用户配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _vm.DeleteProfileCommand.Execute(profile.Id);
    }

    private async void StoragePaths_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new StoragePathsWindow(_vm.BackupRoot, _vm.LedgerPath, _vm.ProfilesPath) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await _vm.UpdateStoragePathsAsync(dialog.BackupRoot, dialog.LedgerPath, dialog.ProfilesPath);
    }
    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        var details = string.Join(Environment.NewLine, new[] { _vm.ErrorMessage, _vm.SuggestedAction, _vm.DiagnosticDetails }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (details.Length == 0) return;
        try { Clipboard.SetText(details); }
        catch (System.Runtime.InteropServices.COMException exception) { MessageBox.Show(this, $"复制失败：{exception.Message}", "剪贴板不可用", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

public sealed class ProfileEditorWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<ProfileEditRow> _rows;
    private readonly TextBox _name = new() { Margin = new Thickness(3), Padding = new Thickness(8, 6, 8, 6) };
    private readonly TextBox _description = new() { Margin = new Thickness(3), Padding = new Thickness(8, 6, 8, 6), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
    private readonly DataGrid _edits;
    private readonly UpscalerProfile _original;
    public UpscalerProfile? Profile { get; private set; }

    public ProfileEditorWindow(UpscalerProfile profile)
    {
        _original = profile;
        _rows = new(profile.Edits.Select(x => new ProfileEditRow(x)));
        Title = "编辑 Profile"; Width = 760; Height = 600; MinWidth = 560; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = profile.DisplayName; _description.Text = profile.Description;
        _edits = new DataGrid { ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false, Margin = new Thickness(3) };
        _edits.Columns.Add(new DataGridTextColumn { Header = "INI Section", Binding = new System.Windows.Data.Binding(nameof(ProfileEditRow.Section)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 150 });
        _edits.Columns.Add(new DataGridTextColumn { Header = "键", Binding = new System.Windows.Data.Binding(nameof(ProfileEditRow.Key)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 170 });
        _edits.Columns.Add(new DataGridTextColumn { Header = "新值", Binding = new System.Windows.Data.Binding(nameof(ProfileEditRow.NewValue)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var form = new Grid { Margin = new Thickness(14) };
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var nameLabel = new TextBlock { Text = "名称", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) }; Grid.SetRow(nameLabel, 0); form.Children.Add(nameLabel); Grid.SetRow(_name, 0); Grid.SetColumn(_name, 1); form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); form.Children.Add(_name);
        var descriptionLabel = new TextBlock { Text = "说明", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 10, 3, 3) }; Grid.SetRow(descriptionLabel, 1); form.Children.Add(descriptionLabel); Grid.SetRow(_description, 1); Grid.SetColumn(_description, 1); form.Children.Add(_description);
        var group = new GroupBox { Header = "INI 覆盖项（可编辑）", Content = _edits, Margin = new Thickness(3, 10, 3, 6) }; Grid.SetRow(group, 2); Grid.SetColumnSpan(group, 2); form.Children.Add(group);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = new Button { Content = "添加 INI 项" }; add.Click += (_, _) => { _edits.CommitEdit(DataGridEditingUnit.Cell, true); _rows.Add(new ProfileEditRow(new IniEdit("", "", null, ""))); };
        var save = new Button { Content = "保存用户档", IsDefault = true }; save.Click += (_, _) => Save();
        var cancel = new Button { Content = "取消", IsCancel = true };
        actions.Children.Add(add); actions.Children.Add(save); actions.Children.Add(cancel); Grid.SetRow(actions, 3); Grid.SetColumnSpan(actions, 2); form.Children.Add(actions); Content = form;
    }

    private void Save()
    {
        _edits.CommitEdit(DataGridEditingUnit.Cell, true); _edits.CommitEdit(DataGridEditingUnit.Row, true);
        if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show(this, "请填写 Profile 名称。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Profile = _original with { DisplayName = _name.Text.Trim(), Description = _description.Text.Trim(), IsBuiltin = false, Edits = _rows.Select(x => x.ToIniEdit()).ToArray() };
        DialogResult = true;
    }
}

public sealed class TextInputWindow : Window
{
    private readonly TextBox _input = new() { Margin = new Thickness(4), Padding = new Thickness(8, 6, 8, 6) };
    public string Value => _input.Text.Trim();
    public TextInputWindow(string title, string label, string initial)
    {
        Title = title; Width = 420; Height = 170; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(12) }; root.Children.Add(new TextBlock { Text = label }); _input.Text = initial; root.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "确定", IsDefault = true }; ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(Value)) DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); root.Children.Add(buttons); Content = root;
    }
}

public sealed class StoragePathsWindow : Window
{
    private readonly TextBox _backup = new(); private readonly TextBox _ledger = new(); private readonly TextBox _profiles = new();
    public string BackupRoot => _backup.Text; public string LedgerPath => _ledger.Text; public string ProfilesPath => _profiles.Text;
    public StoragePathsWindow(string backupRoot, string ledgerPath, string profilesPath)
    {
        Title = "数据与备份目录"; Width = 760; Height = 280; MinWidth = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _backup.Text = backupRoot; _ledger.Text = ledgerPath; _profiles.Text = profilesPath;
        var root = new Grid { Margin = new Thickness(12) }; root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(root, 0, "备份根目录", _backup); AddRow(root, 1, "安装台账文件", _ledger); AddRow(root, 2, "Profile 文件", _profiles);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "应用", IsDefault = true }; save.Click += (_, _) => { if (new[] { BackupRoot, LedgerPath, ProfilesPath }.All(x => !string.IsNullOrWhiteSpace(x))) DialogResult = true; };
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); Grid.SetRow(buttons, 3); Grid.SetColumnSpan(buttons, 2); root.Children.Add(buttons); Content = root;
    }
    private static void AddRow(Grid grid, int row, string label, TextBox input) { grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) }); Grid.SetRow(grid.Children[^1], row); grid.Children.Add(input); Grid.SetRow(input, row); Grid.SetColumn(input, 1); }
}

public sealed class HistoryWindow : Window
{
    public string? SelectedManifestPath { get; private set; }
    public HistoryWindow(IEnumerable<HistoryItem> history)
    {
        Title = "部署历史与恢复"; Width = 1000; Height = 600; MinWidth = 760; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid { ItemsSource = history.ToArray(), AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(12) };
        grid.Columns.Add(new DataGridTextColumn { Header = "游戏", Binding = new System.Windows.Data.Binding("Manifest.GameTitle"), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "部署时间 (UTC)", Binding = new System.Windows.Data.Binding("Manifest.CreatedUtc"), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "清单", Binding = new System.Windows.Data.Binding("ManifestPath"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "台账状态", Binding = new System.Windows.Data.Binding("State"), Width = 130 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "最新计划", Binding = new System.Windows.Data.Binding("IsLatest"), Width = 90 });
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var note = new TextBlock { Text = "仅回滚明确选中的部署清单；这会恢复该次备份并删除该次新增文件，不代表清空全部历史。", Margin = new Thickness(14), Foreground = System.Windows.Media.Brushes.DarkSlateGray }; root.Children.Add(note); Grid.SetRow(grid, 1); root.Children.Add(grid);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        var rollback = new Button { Content = "恢复所选清单" }; rollback.Click += (_, _) => { if (grid.SelectedItem is HistoryItem item) { SelectedManifestPath = item.ManifestPath; DialogResult = true; } };
        buttons.Children.Add(rollback); buttons.Children.Add(new Button { Content = "关闭", IsCancel = true }); Grid.SetRow(buttons, 2); root.Children.Add(buttons); Content = root;
    }
}

















