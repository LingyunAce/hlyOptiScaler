# OptiScaler Deployer（WPF + CLI）

Plan A/B 提供 CLI，Plan C 增加 Windows WPF 桌面界面，Plan D 增加受限提权 Worker、官方 Releases payload 和部署后的健康检查。自动化测试不会证明真实游戏、驱动、GPU 或网络环境兼容。

## 构建

需要 .NET 8 SDK。在本目录运行：

```powershell
dotnet build .\OptiScalerDeployer.sln -c Release
dotnet test .\OptiScalerDeployer.sln -c Release
```

在 Windows 上构建桌面应用后，可直接运行 `src\Deployer.App\bin\Release\net8.0-windows\opti-deploy-app.exe`。也可从本目录运行 `dotnet run --project .\src\Deployer.App`。CLI 可运行文件位于 `src\Deployer.Cli\bin\Release\net8.0\opti-deploy.exe`。

从仓库根目录运行 `启动OptiScaler.cmd` 会依次检查 `src/Deployer.App/bin/Release/net8.0-windows/`、`artifacts/publish/` 和当前本机验收副本。它要求 App 与 `worker/` 子目录的运行文件齐全；请保留整个输出目录，不要只复制 EXE。三处都没有完整目录时，脚本会提示从仓库根目录运行 `dotnet build .\tools\OptiScalerDeployer\OptiScalerDeployer.sln -c Release`，然后重新启动。

## WPF 桌面界面（Plan C）

Plan D 当前实现包含受限提权 Worker、官方 GitHub stable/nightly payload 校验与缓存、部署后健康检查和 RID-aware 发布。2026-10-13 Release 构建成功；全量测试 336 个 Core + 38 个 App，共 374 个通过，0 失败或跳过。安全修复验收用的可运行目录由该 Release 构建输出组装，位于 `artifacts/security-fixes-20261013/verified-release`；单独 `dotnet publish` 仍受 Worker 发布阶段的 E-SafeNet/MSBuild 错误阻塞，因此该目录不是安装包或 publish 产物。健康检查只验证日志中的版本、后端族和注入信号；其中 `FSR 3.X` 不代表 FSR4 模型或 GPU/驱动兼容性。部分首次 DX12 初始化路径缺少明确的后端成功日志，会保守超时；超时不自动回滚。官方 stable 资产下载及隔离部署/恢复已验收，实时 API 当前限额；真实 UAC 和游戏启动仍需手动验收。安全修复范围与限制见 [2026-10-13 安全修复验证记录](../../docs/superpowers/plans/2026-10-13-optiscaler-deployer-security-fixes.md)。

提权 Worker 只允许从 `Program Files` 受保护目录启动。便携版放在用户可写目录时，普通目录部署仍可使用；需要管理员权限的部署会提示先把完整发布目录安装到受保护位置，避免用户可写的 Worker 被 `runas` 提权执行。

为避免 junction 或符号链接改变恢复的实际写入目标，当前版本会拒绝在管理员权限进程中执行路径型回滚，包括 CLI 回滚和 Worker 内部自动恢复。受保护游戏目录的手动回滚暂不支持管理员方式；请保留清单与备份，不要从管理员终端运行 CLI。若普通用户权限足以写入目标目录，可恢复目录权限后以普通用户启动程序并回滚。普通权限仍无法恢复时，本版本暂不支持安全的管理员恢复。

在 Windows 上运行：

```powershell
dotnet run --project .\src\Deployer.App
```

界面启动时不会自动扫描本机游戏。点“刷新”后才扫描 Xbox、Steam、Epic 和已保存的手动目录；也可添加或移除一个手动游戏目录。选择游戏后，按候选列表选定实际游戏 exe，再选 Profile 和是否启用帧生成。Payload 可指定本地目录；留空优先使用本地构建，缺失时获取官方 GitHub stable 发布；若 GitHub API 限流，stable 会尝试固定校验值的官方 v0.9.4 资产，该版本可能不是最新。显式选择的目录不会自动回退。

点“生成 / 更新计划”后，界面展示体检、目标部署目录、身份 DLL、文件写入、备份、INI 修改和警告。检查完整计划并勾选确认后，才可部署。计划输入改变时，旧计划和确认会失效。需提权的部署由独立 Worker 按已审核计划执行，UAC 取消不会写入游戏目录。

“打开历史 / 恢复”可查看清单并按单项回滚，也可以选择 manifest 文件恢复。取消部署时 Core 会尝试回滚；若中断或恢复结果不确定，请使用界面显示的 manifest 检查并手动恢复。数据目录对话框可分别设置备份、台账和 Profile 文件路径。Profile 支持克隆内置档、编辑和删除用户档，内置档不可覆盖。

桌面界面使用无头 ViewModel 与应用服务测试；WPF 窗口本身不做 UI 自动化。自动化测试使用合成 payload 和隔离临时目录，不证明真实游戏、驱动或 GPU 运行兼容性。

## 1. 生成并检查计划

```powershell
dotnet run --project .\src\Deployer.Cli -- plan --payload "D:\OptiScaler\x64\Release\a" --install-root "D:\Games\Example" --deploy-dir "D:\Games\Example\Binaries\Win64" --exe "Example.exe" --profile amd-rdna4 --backup-root "D:\OptiBackups" --out "D:\Plans\example.json"
```

必需参数是 `--payload`、`--install-root` 和 `--exe`。payload 目录需要包含 `OptiScaler.dll` 与 `OptiScaler.ini`；`--deploy-dir` 默认为游戏根目录，UE 游戏通常应指向 `Binaries\Win64`。`--exe` 必须是部署目录中存在的单个 `.exe` 文件名。

命令会先检查选定程序的原生 x64 PE 导入、游戏文件中的 SDK、图形 API、游戏目录布局和目标目录可写性，再打印身份 DLL、按顺序写入的文件、备份目标、INI 修改和警告。它只做静态检查，不能证明运行时会加载相应 SDK。`--vulkan` 已移除，因为 API 必须来自 PE 检测；`--no-sdk` 强制计划阻塞并以退出码 2 结束。执行前请检查计划输出和 JSON。`--out` 不能放在游戏目录或 payload 目录内。

常用可选参数：

- `--title "游戏名"`、`--platform Xbox|Steam|Epic|Manual`：计划显示信息和指纹输入。
- `--profile <id>`：选择 profile，默认 `amd-rdna4`。
- `--profiles <file>`：指定用户 Profile JSON。省略时使用 `%APPDATA%\OptiScalerDeployer\profiles.json`。损坏配置会给出提示并保留原内容；无法安全另存时不允许覆盖它。
- `--backup-root <dir>`：备份根目录，默认 `%LOCALAPPDATA%\OptiScalerDeployer\backups`。
- `--framegen`：在计划中叠加帧生成设置。
- `--allow-overwrite`：允许身份选择器在冲突情况下覆盖游戏自带 DLL；该原件会先进入影子备份。
- `--package-family-name <name>`、`--app-id <id>`：向 Xbox 目标指纹和计划传递扫描得到的包标识；扫描输出会显示这两个值。

## 2. 扫描游戏与静态体检

```powershell
dotnet run --project .\src\Deployer.Cli -- scan
dotnet run --project .\src\Deployer.Cli -- inspect --install-root "D:\Games\Example" --deploy-dir "D:\Games\Example\Binaries\Win64" --exe "Example.exe" --platform Steam
```

`scan` 查询当前用户可见的 Xbox 包，并扫描 Steam、Epic 和已保存的手动目录；它不要求管理员权限，也不会安装或改动持久游戏文件。扫描会在候选安装目录创建并删除唯一的临时可写性探测文件。输出候选可执行文件及 Xbox `PackageFamilyName` / `AppId`，可将选中的 exe 父目录作为 `--deploy-dir`，将扫描元数据复制到 `plan` 参数。`scan --manual-root <dir>` 只扫描指定目录，适合小范围检查；`manual-add --title <name> --folder <dir>` 和 `manual-remove --folder <dir>` 管理持久手动目录，可用 `--manual-config <file>` 指定配置位置。

`inspect` 检查一个明确选择的 `.exe`，不会保存状态或持久修改游戏/配置文件；为确认可写性，它会在部署目录创建并删除唯一的临时探测文件。扫描若有警告，台账对账会跳过；只有扫描完整、匹配已发现且可读取的部署目录，且身份 DLL 确认缺失时，才会把对应台账项标为 `NeedsReinstall`。通过 `scan --ledger <file>` 显式选择台账文件。

## 3. 部署计划

```powershell
dotnet run --project .\src\Deployer.Cli -- apply --plan "D:\Plans\example.json" --backup-root "D:\OptiBackups" --ledger "D:\OptiData\ledger.json"
```

apply 的 `--backup-root` 必须与生成计划时的值一致；`--ledger` 是 apply/rollback/ledger 使用的独立路径，不属于 plan 参数。部署会先保存一个不包含游戏写入目标的空白 manifest，再备份并校验全部原件，然后发布完整回滚清单并开始写游戏目录。身份 DLL 最后写入。Core 收到取消令牌时会尝试回滚；当前 CLI 使用 `CancellationToken.None`，中断后应根据已保存的 manifest 手动运行 rollback。

同一个 `PlanId` 只能执行一次。重复 apply 会被拒绝，以保护第一次部署前的原件备份。重新部署请重新生成计划。CLI 不支持 `--force`，带未知参数会报错。部署成功后才更新台账；若台账保存失败，游戏仍保持已部署状态，错误信息会提供 manifest 路径以便后续回滚。

## 4. 查看台账

```powershell
dotnet run --project .\src\Deployer.Cli -- ledger --ledger "D:\OptiData\ledger.json"
```

省略 `--ledger` 时读取 `%LOCALAPPDATA%\OptiScalerDeployer\ledger.json`。每项会显示 Fingerprint、状态、DLL 身份、版本、部署目录和最新 manifest 路径。损坏或无法读取的台账会显示原因并以失败退出，不会被当成空台账覆盖。

## 5. 回滚

从部署成功输出或台账中复制 manifest 路径：

```powershell
dotnet run --project .\src\Deployer.Cli -- rollback --manifest "D:\OptiBackups\0123abcd\<PlanId>\manifest.json" --backup-root "D:\OptiBackups" --ledger "D:\OptiData\ledger.json"
```

回滚会校验备份，再原子还原被覆盖文件并删除清单记录的新增文件。若备份校验或还原失败，原路径不会被当成新增文件删除。只有回滚的 PlanId 与台账 `LatestPlanId` 相同时，CLI 才会将台账状态设为 `RolledBack`；游戏已恢复但台账更新失败时会明确报告两者状态。

多次部署需生成不同 PlanId。只回滚最新计划会回到上一版部署；要完全卸载，按 manifest 创建时间从新到旧回滚所有仍有效的计划。当前没有自动 Prune：较旧的备份可能是回到初装前状态所需的恢复链。`BackupStore.Prune` 是 Core 显式 API，不由 CLI 自动调用。

## 退出码

- `0`：命令成功；计划没有阻塞警告。
- `1`：参数、文件、部署、回滚或台账操作失败；`scan` 有局部来源失败或手动目录配置无效时也返回此码。
- `2`：`plan` 成功生成了含阻塞警告的计划，或 `inspect` 检查到不支持的程序/不可用的目录；计划不能 apply。

Debug/Release 构建与测试以当前验证记录为准。预览使用 mock service，不执行真实扫描、游戏写入或启动。健康通过后可生成关闭日志的新完整计划，仍需检查和确认；关闭日志后暂停健康检查。受保护目录的清单和备份需保留，当前版本不支持安全的管理员回滚。

## 2026-10-10 功能验收

修复后的 Release 测试 355 项通过，发布程序集/Worker 的隔离验收 22 项通过。官方 v0.9.4 资产完成真实下载、SHA256、7z 展开、缓存、隔离部署和完整恢复；metadata 使用本轮已取得的官方快照，实时 API 后续返回 HTTP 403 限额，未计为端到端联网成功。

稳定版 AMD 后端别名按 payload `[Upscalers]` 适配为 `fsr31`/`fsr31_12`，新版保留 `ffx`/`ffx_12`。Steam 健康启动使用 `steam://run/<appid>`。日志健康信号仅证明后端族，不证明 FSR4 模型生效。

真实游戏、UAC、GPU/驱动和帧生成仍未验收。桌面 channel/来源选择仍是目标产品缺项；当前为手动目录或自动本地→stable。stable metadata 遇 HTTP 403/429 时会使用固定 SHA256/官方 URL 的 v0.9.4 资产回退，该版本可能不是最新版本，所有正常下载大小、来源、解包和缓存校验仍生效。完整证据见 [功能验收报告](../../docs/superpowers/plans/2026-10-10-optiscaler-deployer-functionality-validation.md)。

Xbox 自动发现保留盘符下默认 `XboxGames`，并读取该盘 `.GamingRoot` v1 标记指向的自定义库；手动添加已识别的 Xbox 库父目录会按其中各游戏拆分显示。完整验收记录见 [安全修复验证](../../docs/superpowers/plans/2026-10-13-optiscaler-deployer-security-fixes.md)。context-mode MCP 小规模试用报告见 [context-mode 试用记录](../../docs/superpowers/plans/2026-10-13-context-mode-trial.md)。
