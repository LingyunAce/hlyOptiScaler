using System.Windows;
using Deployer.App.Models;
using Deployer.App.Services;
using Deployer.App.ViewModels;

namespace Deployer.App;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var preview = Array.FindIndex(e.Args, arg => string.Equals(arg, "--preview", StringComparison.OrdinalIgnoreCase));
        if (preview >= 0)
        {
            if (preview + 1 >= e.Args.Length) { Shutdown(2); return; }
            var output = e.Args[preview + 1];
            try { await PreviewRenderer.RenderAsync(output); Shutdown(0); }
            catch (Exception ex)
            {
                try { await File.WriteAllTextAsync(output + ".error.txt", ex.ToString()); } catch { }
                Shutdown(1);
            }
            return;
        }

        try
        {
            var options = new DeployerAppOptions();
            var service = new DefaultDeployerAppService(options);
            var viewModel = new MainViewModel(service, options);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}\n\n建议：检查数据目录权限后重试。\n\n诊断：\n{ex}", "OptiScaler 部署工具", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
