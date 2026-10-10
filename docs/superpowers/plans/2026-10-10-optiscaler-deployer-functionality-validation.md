# OptiScaler Deployer 功能可行性验收

日期：2026-10-10，香港时间。范围：隔离验证，不改动或启动现有游戏，不请求 UAC。

## 结论

Plan A–D 的主要部署路径已实现。本次确认发布程序可以打开和正常退出，隔离目录中的发现、体检、计划审核、部署、历史、独立 Worker 和恢复流程可行。修复后的自动化测试全部通过。

不能据此宣称完整目标产品和真实游戏验收全部完成。来源/channel 的桌面选择尚未实现；受保护目录手动恢复仍需管理员 CLI。真实游戏启动、真实 UAC、RDNA4、FSR4 模型、帧生成和画质/性能效果均未验证。

## 本次证据

| 检查 | 结果 | 实际范围 |
| --- | --- | --- |
| Release 全套自动化测试 | 355 通过，0 失败，0 跳过 | Core 318 + App 37，修复后的完整运行 |
| 新回归测试 | 8 通过 | 稳定版别名、旧日志、Steam 平台启动、HTTP 诊断；修复前 5 项按预期失败 |
| 发布程序 | 通过 | Release 发布退出码 0；主窗口创建、响应、正常关闭退出码 0 |
| 隔离验收 | 22 项通过 | 使用发布 App/Core 程序集及实际发布 Worker；测试数据生成器只生成合成 x64 PE |
| 手动发现及 Profile 保存 | 通过 | 配置路径和扫描范围均显式限定在隔离目录 |
| 计划审核保护 | 通过 | 未确认计划拒绝执行，文件内容保持一致 |
| 部署、台账、历史 | 通过 | 合成 DX12/DLSS 导入 EXE；真实文件系统写入与 manifest |
| 独立 Worker | 通过 | 实际子进程读取 job，验证 SHA，写入结果；重复 job 被拒绝；未经过 runas/UAC |
| 回滚 | 通过 | 合成 payload、Worker、官方真实 payload 均按 manifest 恢复；全部原文件 SHA256、目录集合一致，无关 Mods 文件保留 |
| 日志健康解析 | 通过 | 真实文件读取，合成启动器写成功日志；未启动游戏，不证明运行时健康 |
| 实时 stable API | 当前不可用 | 本轮先取得官方 metadata，后 GitHub 返回 HTTP 403 API rate limit exceeded |
| 官方资产下载、SHA256、7z 展开、缓存 | 通过 | API 限额后仅 metadata 使用本轮已获取的官方快照；资产仍从官方地址真实下载，生产下载器完成校验与解压 |
| 官方 payload 规划与部署 | 通过 | 生成 19 项写入，在隔离游戏目录实际应用并恢复；未执行其中的 native DLL |
| 官方 stable AMD 配置 | 通过 | 实际下载 INI 上生成 Dx12=fsr31，Dx11/Vulkan=fsr31_12；只证明配置兼容性 |
| nightly | 当前不可用 | 早先官方 tag 返回 assets=[]，最后复查也受 HTTP 403 限额影响 |

实时 API 不可用保留为一项外部可用性失败，未计入“22 项通过”。隔离验收进程退出码为 0；结果 JSON 同时记录 `liveMetadataAvailable=false`。

## 官方资产

- 版本 tag：`v0.9.4`，DLL 文件版本：`0.9.4.0`。
- 文件：`Optiscaler_0.9.4-final.20260718._MM.7z`，55,016,448 bytes。
- 官方 URL：<https://github.com/optiscaler/OptiScaler/releases/download/v0.9.4/Optiscaler_0.9.4-final.20260718._MM.7z>。
- 官方 metadata SHA256 与实际校验记录一致：`575cb4df866116093df75af607e37fd70e10f5163e0f23fd5c804142e80ef0ad`。
- 缓存记录 21 个文件，第二次读取验证缓存完整性。本次没有绕过下载器的 host、digest、展开限额或路径检查。

## 本次修复

1. 官方稳定版的 `[Upscalers]` 声明 `fsr31`/`fsr31_12`，较新本地版本声明 `ffx`/`ffx_12`。此前 AMD Profile 在稳定版可能静默落到错误后端。Planner 现在依据 payload 的该 section 声明转换旧版别名，计划和最终 INI 展示实际值；新版本保留原名称，不改用户 Profile。
2. Steam 健康启动使用经过验证的非零数字 AppId，生成 `steam://run/<appid>`，不直接启动游戏 EXE。自动化只检查启动请求，未启动真实 Steam 游戏。
3. 旧版健康日志支持原始 `fsr31`/`fsr31_12` 与 `FSR 3.1` 后端族信号。该信号不证明 FSR4 模型或实际 GPU 生效。
4. GitHub metadata HTTP 失败记录状态码和重试/手动目录建议，并传递到桌面错误诊断。

## 环境与边界

本机 Windows 11 家庭版 10.0.22631，64 位；.NET 8.0.14/8.0.31 桌面运行时。GPU 为 RTX 4060 Laptop GPU（31.0.15.5186）、Intel UHD Graphics（32.0.101.6078），另有 Oray 虚拟显示驱动。此环境不能证明目标 RX 9070 XT/RDNA4 的 FSR4 效果。

仓库没有现成 `x64/` native payload。可使用官方包或手动选择兼容产物；本次仅构建并验收 .NET 部署工具，没有完成 OptiScaler C++ native 构建。使用稳定版旧 schema 也不证明设计文档中的新版模型设置全部生效。

仍需后续验收或完善：

- Steam、Epic、Xbox/Game Pass 在真实游戏中的注入、启动和运行兼容性。
- UAC 同意/拒绝、受保护目录部署和管理员 CLI 恢复。Worker 隔离子进程成功不等于真实提权已验收。
- 初次 DX12 初始化的一些路径不输出明确的 backend-success 日志，可能保守超时；超时保留部署，不自动恢复。
- FSR4 模型、帧生成、GPU/驱动、画质、帧率及目标版本能力。
- 桌面来源和 stable/nightly 选择；目前为手动目录或自动本地→stable，channel 只有程序配置入口。
- 真实联网查询恢复后正常的端到端首次获取。API 限额期间，已有缓存也不能保证自动来源链可用，可手动选择已验证的缓存 `content` 目录。

## 复现与产物

发布目录：`tools/OptiScalerDeployer/artifacts/acceptance/published/`。启动入口：`opti-deploy-app.exe`，应保留整个目录及 `worker/`。

隔离证据：`tools/OptiScalerDeployer/artifacts/acceptance/results.json`。

最终隔离根：`tools/OptiScalerDeployer/artifacts/acceptance/run-4b6b861363f546448a03fc743f994f2b/`。游戏文件已恢复；备份、manifest、job、官方缓存与验收证据保留供检查。现有真实游戏没有被扫描、部署或启动。

验收程序：`tools/OptiScalerDeployer/artifacts/acceptance/Harness.csproj`。它引用发布程序集，metadata 限额时明确使用已记录的官方快照；不会修改生产程序的来源策略。

```powershell
dotnet test tools/OptiScalerDeployer/OptiScalerDeployer.sln -c Release --no-restore -v minimal
dotnet publish tools/OptiScalerDeployer/src/Deployer.App/Deployer.App.csproj -c Release -o tools/OptiScalerDeployer/artifacts/acceptance/published
dotnet run --project tools/OptiScalerDeployer/artifacts/acceptance/Harness.csproj -c Release -- D:/AI/project/hlyOptiScaler/tools/OptiScalerDeployer/artifacts/acceptance
```

实现委派服务本轮多次返回 502 Upstream access forbidden。根据用户代理规则中的 worker unavailable 备用条款，由主代理完成最小修复、隔离验收程序和报告；只读复审未发现新增阻塞问题。未提交或推送代码。
