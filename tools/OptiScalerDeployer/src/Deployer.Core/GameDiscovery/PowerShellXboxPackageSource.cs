using System.Diagnostics;
using System.Text.Json;

namespace Deployer.Core.GameDiscovery;

/// <summary>Reads only the current user's packages through WinRT; no install or registry changes.</summary>
public sealed class PowerShellXboxPackageSource(TimeSpan? timeout = null) : IXboxPackageSource
{
    private const string Script = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ProgressPreference = 'SilentlyContinue'
        $ErrorActionPreference = 'Stop'
        $pm = [Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime]::new()
        $rows = @()
        foreach ($p in $pm.FindPackagesForUser('')) {
            try {
                $root = $p.InstalledLocation.Path
                $family = $p.Id.FamilyName
                if (!$root -or !$family) { continue }
                $appId = $null
                $display = $null
                $manifest = Join-Path $root 'AppxManifest.xml'
                if (Test-Path -LiteralPath $manifest) {
                    try {
                        [xml]$xml = Get-Content -LiteralPath $manifest -Raw
                        $app = $xml.SelectSingleNode("//*[local-name()='Application']")
                        if ($app) {
                            $appId = $app.GetAttribute('Id')
                            $visual = $app.SelectSingleNode("*[local-name()='VisualElements']")
                            if ($visual) { $display = $visual.GetAttribute('DisplayName') }
                        }
                    } catch { }
                }
                if (!$display) { $display = $p.DisplayName }
                $rows += [pscustomobject]@{
                    InstallRoot = $root
                    DisplayName = $display
                    PackageFamilyName = $family
                    AppId = $appId
                }
            } catch { }
        }
        ConvertTo-Json -InputObject $rows -Compress -Depth 3
        """;

    public async Task<IReadOnlyList<XboxPackage>> GetCurrentUserPackagesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return [];
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        }};
        process.StartInfo.ArgumentList.Add("-NoLogo"); process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive"); process.StartInfo.ArgumentList.Add("-WindowStyle");
        process.StartInfo.ArgumentList.Add("Hidden"); process.StartInfo.ArgumentList.Add("-EncodedCommand"); process.StartInfo.ArgumentList.Add(encoded);
        process.Start();
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("读取当前用户的 Xbox/MSIX 包信息超时。");
        }
        var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException($"WinRT 包查询失败：{error.Trim()}");
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var document = JsonDocument.Parse(output);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        var result = new List<XboxPackage>();
        foreach (var element in elements)
        {
            try
            {
                var root = element.GetProperty("InstallRoot").GetString();
                var family = element.GetProperty("PackageFamilyName").GetString();
                if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(family)) continue;
                result.Add(new(root, element.TryGetProperty("DisplayName", out var name) ? name.GetString() ?? "" : "",
                    family, element.TryGetProperty("AppId", out var id) ? id.GetString() : null));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        }
        return result;
    }
}
