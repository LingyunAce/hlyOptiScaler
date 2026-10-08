# OptiScaler Deployer 设计文档

**日期**：2026-10-08
**状态**：已确认，待生成实施计划
**目标硬件**：AMD Radeon RX 9070 XT（RDNA4）
**目标平台**：Windows 10/11，主要面向 Xbox PC（PC Game Pass / Microsoft Store）游戏

---

## 1. 背景与目标

### 1.1 要解决的问题

OptiScaler 是一个游戏超分/帧生成中间件，以 DLL 侧载方式运行在游戏进程中，把游戏对某一家超分 SDK 的调用转发到另一家实现上。它目前只有一个交互式控制台安装器 `setup_windows.bat`（512 行），使用者必须：

1. 自己找到游戏目录 —— 对 Xbox 游戏尤其麻烦（junction、UE 的 `Binaries\Win64` 层级）
2. 自己判断该用哪个 DLL 身份（`dxgi` / `winmm` / `version` / …）
3. 自己判断该开哪些 Input、该不该开 GPU 伪装
4. 手工编辑 51 KB 的 `OptiScaler.ini`

本工具（下称 **Deployer**）把上述四步自动化：**扫描游戏 → 体检 → 生成计划 → 用户确认 → 安全部署 → 健康检查 → 失败自动回滚**。

### 1.2 关键前提：DLSS 4 在 Windows + AMD 上不可行

这是设计的出发点，必须先说清楚：

- DLSS 的超分核心 `nvngx_dlss.dll` 依赖 CUDA + Tensor Core，只能在 NVIDIA RTX 上执行。OptiScaler 的 GPU 伪装（spoofing）只能骗过**游戏**的硬件检测，骗不过 DLSS 运行时本身。
- 目前唯一让 AMD 跑 DLSS4 的项目是 `d4r`（基于 ZLUDA 做 CUDA→RDNA 转译），但它**仅支持 Linux + Proton，Windows 下无法运行**，实测帧数低于 FSR4，且只支持超分、不支持帧生成。

**RX 9070 XT 上真正能拿到的等价物是 FSR 4 全家桶**，而 OptiScaler 官方支持：

- `FSR 4.1` 超分（ML 模型）
- `FSR-FG 4.0.0` ML 帧生成
- OptiScaler 明确标注这些 Redstone 特性**官方仅支持 RDNA4** —— 正是这张卡

所以本工具的价值主张是：**把「游戏里只有 DLSS 选项」翻译成「实际用 FSR4 + FSR4-FG 跑」**。

### 1.3 已核实的关键事实

| 事实 | 来源 |
|---|---|
| Release 产物布局为 `x64\Release\a\`，含 `OptiScaler.dll`、`OptiScaler.ini`、`OptiScaler\`（FSR/XeSS 运行时、`D3D12_OptiScaler\` Agility SDK、`plugins\`）、`Licenses\` | `OptiScaler.vcxproj` PostBuildEvent |
| 支持的 DLL 身份：`dxgi` `winmm` `version` `dbghelp` `d3d12` `wininet` `winhttp` + `OptiScaler.asi` | `setup_windows.bat` L153-160、`DllNames.h` |
| 官方提示：默认 `dxgi.dll` 兼容性最好；Vulkan 用 `winmm.dll`；**XGP/MS Store 用 `winmm`/`version` 更好** | `setup_windows.bat` L150-151 |
| `OptiScaler.log` 落在 **DLL 所在目录**（`Util::DllPath().parent_path()`） | `Config.cpp:407`、`Config.h:266` |
| `[Log] LogToFile` 默认（auto）为 **`false`** —— 默认不产生日志文件 | `OptiScaler.ini` `[Log]` 段注释 |
| `[FSR] UpscalerIndex` / `FGIndex` 的 auto 默认在 RDNA4 上就是 FSR4 | `OptiScaler.ini` `[FSR]` 段注释 |
| `[ProcessFilter] TargetProcessName` 可把注入锁定到单一进程；`ProcessExclusionList` 默认已排除 `crashpad_handler.exe` | `OptiScaler.ini` `[ProcessFilter]` 段 |
| `OptiScaler.ini` 的 `[Spoofing]` 注释里直接列了 `0x7550 = 9070 XT` 与 `AMD Radeon RX 9070 XT` | `OptiScaler.ini` `[Spoofing]` 段 |
| **官方 BAT 在目标 DLL 已存在时只问一句就 `del /F`，不备份** —— 这是「装完游戏起不来」的典型成因 | `setup_windows.bat` L188-206、L379-382 |
| OptiScaler 的 proxy 把调用转发到 **System32 原生 DLL**，不是游戏本地同名 DLL，游戏自定义导出符号会丢失 | `proxies/`、`exports/` 目录结构 |

### 1.4 Xbox 游戏的落盘位置与权限（决定性事实）

微软官方 GDK 文档《Flat 文件安装概述》《电脑 GDK 游戏的 Mod 支持》确认，2022 年 3 月之后的 Xbox PC 游戏采用平面文件安装：

- 位置为 `[盘符]:\XboxGames\<游戏名>\content\`；`WindowsApps` 下的同名目录只是指向它的 **junction**
- **「`C:\XboxGames` 文件夹不需要提升的权限即可访问」**
- **「游戏可执行文件之外的所有游戏资源都将完全可修改」**（exe 保持加密）

**推论**：往 Xbox 游戏目录放一个新的 `winmm.dll` / `version.dll`（OptiScaler 属于「新增 DLL 侧载」，不改 exe）**不需要 takeown/icacls 抢权限**。只有老式 MSIXVC / 纯 UWP 商店游戏仍在受 TrustedInstaller 保护的 `WindowsApps` 里，那部分才需要提权。这直接决定了下面的权限模型选择。

### 1.5 非目标（YAGNI）

明确不做：

- **批量部署**（用户明确要求每个游戏单独注入，避免损坏）
- **反作弊组件检测**（用户不玩在线游戏，EAC/BattlEye/Vanguard 识别与其强制确认弹框全部从 v1 移除）
- 从工具内管理游戏存档、Mod、其他类型补丁
- Linux / Proton 支持
- 修改 OptiScaler 的 C++ 源码（Deployer 只消费其构建产物）
- GOG / Ubisoft Connect / EA App 扫描器（架构预留，v1 不实现）
- WPF View 层的 UI 自动化测试

---

## 2. 权限模型（方案 A）

主进程 `asInvoker` 以普通用户权限运行，日常操作（扫 `XboxGames`、部署、备份、卸载）完全不弹 UAC。仅当探测到目标目录不可写时，才通过 `runas` 拉起独立的 **`Deployer.Worker`** 控制台进程执行提权任务。

选择理由：

- 90% 的场景（现代 Xbox 游戏）本就不需要提权，让主路径保持零 UAC 打扰
- 危险操作被压缩到一条明确、可审计、可日志化的窄通道
- 提权进程生命周期极短，攻击面小
- 独立 Worker exe（而非主 exe 加开关）意味着提权二进制无 WPF/UI 依赖、启动快、审计面极小

被否决的备选：

- **常驻管理员**（`requireAdministrator`）：每次启动弹 UAC；一个会读游戏目录、联网下载二进制、还常驻管理员的进程攻击面过大；提权进程无法接收非提权进程的拖拽
- **Windows 服务 + 瘦客户端**：对个人工具严重过度设计

**提权通道约束**：Worker 只接受一种 job 类型（执行 `DeploymentPlan`），不联网、不改注册表、不做计划之外的任何文件操作；job 文件带 SHA256 校验，执行完即删。

---

## 3. 架构与模块划分

### 3.1 目录布局

```
tools/OptiScalerDeployer/           ← 新增，与 OptiScaler/ 平级
├── OptiScalerDeployer.sln
├── Directory.Build.props            net8.0 / Nullable=enable / TreatWarningsAsErrors
├── src/
│   ├── Deployer.Core/               net8.0         纯逻辑类库，零 UI 依赖
│   ├── Deployer.App/                net8.0-windows WPF + MVVM，asInvoker
│   └── Deployer.Worker/             net8.0-windows 控制台，提权路径唯一入口
└── tests/
    ├── Deployer.Core.Tests/         xUnit
    └── Deployer.TestData/           假游戏目录 fixture
```

**不并入 `OptiScaler.sln`**。理由：主 sln 的 C++ PostBuildEvent 会往 `x64\Release\a\` 拷贝并打 7z，nightly CI（`.github/workflows/build.yml`）直接依赖它；混入 C# 项目会干扰那条流水线。Deployer 独立成 sln，通过读取 `x64\Release\a\` 单向消费主工程产物，互不干扰。

### 3.2 Core 的十一个模块

Core 内部单向依赖，`Planning` 是唯一汇聚点，UI 只消费 record 与接口。

| 模块 | 职责 | 对外接口 |
|---|---|---|
| `GameDiscovery` | 找出机器上装了哪些游戏 | `IGameScanner` → `DiscoveredGame` |
| `Inspection` | 游戏体检：PE 导入表、图形 API、已有超分 SDK、引擎与目录结构 | `IGameInspector` → `InspectionReport` |
| `Identity` | 决定用哪个 DLL 身份，规避与游戏自带 DLL 的冲突 | `IDllIdentitySelector` → `IdentityDecision` |
| `Payload` | 取 OptiScaler 二进制（本地构建 / GitHub Releases） | `IPayloadSource` → `PayloadBundle` |
| `Profiles` | AMD-RDNA4 / NVIDIA-RTX 后端策略 | `UpscalerProfile` record |
| `Ini` | **保留注释**的部分改写器，不重新序列化 | `IIniPatcher` → `IniPatchResult` |
| `Planning` | 汇聚以上全部，产出不可变的部署计划 | `IDeploymentPlanner` → `DeploymentPlan` |
| `Deployment` | 影子备份、原子执行、精确回滚 | `IDeploymentExecutor` / `IRollbackExecutor` |
| `HealthCheck` | 启动游戏 → 探针 `OptiScaler.log` → 判定 | `IHealthCheckRunner` |
| `Elevation` | 提权窄通道：job 序列化、校验、投递、结果回收 | `IJobTransport` |
| `Persistence` | 安装台账：哪个游戏装了什么版本/身份、备份在哪 | `IInstallLedger` |

### 3.3 游戏扫描器（插件式，Xbox 做深）

```csharp
public interface IGameScanner
{
    string PlatformName { get; }
    Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct);
}

public sealed record DiscoveredGame
{
    public required string Title { get; init; }
    public required GamePlatform Platform { get; init; }   // Xbox / Steam / Epic / Manual
    public string? PackageFamilyName { get; init; }        // Xbox/MSIX 专有
    public string? AppId { get; init; }                    // 用于 shell:AppsFolder 启动
    public required string InstallRoot { get; init; }
    public required IReadOnlyList<string> ExecutableCandidates { get; init; }
    public required DirectoryWritability Writability { get; init; }
    public required string ScannerSource { get; init; }
}

public enum DirectoryWritability { Writable, NeedsElevation, Blocked, Unknown }
```

`DiscoveredGame` 是**扫描阶段**的产物，描述「机器上有这么个游戏」；它可能含多个 exe 候选，部署目录尚未确定。用户在 UI 上选定具体 exe、且体检完成后，才由 planner 解析出 `GameTarget`（见 §5.1），后者才带有唯一的部署目录。两者是「发现」与「决策后」的关系，不可混用。

v1 实现四个：

- **`XboxScanner`**（重点）
  - 枚举所有固定驱动器的 `<drive>\XboxGames\*\content\`
  - 枚举 `<drive>\WindowsApps\*` 中的 reparse point（junction）并解析 target，覆盖老式安装
  - 用 WinRT `Windows.Management.Deployment.PackageManager.FindPackages()` 取 `DisplayName` / `InstalledLocation` / `Id.FamilyName`，与目录扫描结果匹配合并去重（普通权限下只能拿到当前用户的包，对本场景足够）
  - exe 识别优先级：`*-WinGDK-Shipping.exe` → `*-Win64-Shipping.exe` → 目录中体积最大且非启动器/非崩溃处理器的 exe
  - 对每个候选目录做实际可写性探测（尝试创建并删除临时文件），产出 `DirectoryWritability`
- **`SteamScanner`**：`HKCU\Software\Valve\Steam` → `SteamPath` → 解析 `libraryfolders.vdf` → 各 library 的 `steamapps\appmanifest_*.acf` → name + installdir + appid
- **`EpicScanner`**：`%PROGRAMDATA%\Epic\UnrealEngineLauncher\LauncherInstalled.dat`（JSON）
- **`ManualFolderScanner`**：用户手动添加的目录，持久化到配置

`GameLibrary` 并发调度全部 scanner，合并去重，结果缓存并支持手动刷新。

### 3.4 设计支点：Plan 与 Execute 严格分离

`DeploymentPlan` 是**纯数据、不可变**的 record，完整描述「将要发生什么」。`IDeploymentExecutor` 只负责无脑执行一个 plan。

这一个决定同时换来三样东西：

1. **可预演** —— UI 能在动手前把完整清单摊给用户看，确认后才执行
2. **可单测** —— planner 是纯函数，用内存文件系统即可覆盖全部决策分支
3. **可跨进程传输** —— 提权 Worker 只需序列化这一个 plan，不必把整套业务逻辑搬进管理员进程（Worker 因此能保持极小）

---

## 4. 数据流：一次部署的完整生命周期

```
① Discover    GameLibrary 并发跑所有 IGameScanner，合并去重 → 游戏列表
② Inspect     选中游戏后跑体检 → InspectionReport（API/SDK/引擎/可写性/警告）
③ Plan        DeploymentPlanner 纯函数计算 → DeploymentPlan
④ Review      UI 展示计划全文：将新增 N 个文件、将备份 M 个原件、INI 改 K 个键
              ── 用户在此确认或取消，这是唯一的授权点 ──
⑤ Execute     目标目录可写 → 主进程直接执行
              不可写      → 序列化 plan 到 %TEMP%\opti-job-<guid>.json
                           → runas 拉起 Deployer.Worker --job <path>
                           → Worker 执行后写 opti-result-<guid>.json + 退出码
                           → 主进程读结果，删临时文件
⑥ HealthCheck 启动游戏（Xbox: shell:AppsFolder\<PFN>!<AppId>；Steam: steam://run/<appid>）
              → 轮询 <部署目录>\OptiScaler.log，超时 60s
              → 解析：OptiScaler 版本行、选中的 Output upscaler、Error/Fatal 行
⑦ Commit / Rollback
              健康 → InstallLedger 落库，标记 Installed + Healthy
              失败 → RollbackExecutor 按 manifest 逆序还原 → 台账标记 RolledBack
                     并把日志摘要留在 UI 上供排查
```

回滚的两个触发点：⑥ 健康检查失败自动触发；UI 上「卸载」按钮手动触发。两者走同一条 `RollbackExecutor` 路径。

---

## 5. 部署计划的具体内容

### 5.1 数据结构

```csharp
public sealed record DeploymentPlan
{
    public required GameTarget        Game       { get; init; }
    public required PayloadInfo       Payload    { get; init; }
    public required IdentityDecision  Identity   { get; init; }
    public required UpscalerProfile   Profile    { get; init; }
    public required IReadOnlyList<FileWrite>   Writes   { get; init; }
    public required IReadOnlyList<BackupEntry> Backups  { get; init; }
    public required IReadOnlyList<IniEdit>     IniEdits { get; init; }
    public required ElevationRequirement       Elevation { get; init; }
    public required IReadOnlyList<PlanWarning> Warnings  { get; init; }
    public required Guid PlanId { get; init; }
}

public sealed record GameTarget(
    string Title, GamePlatform Platform, string? PackageFamilyName, string? AppId,
    string InstallRoot, string DeployDirectory, string ExecutableName, string Fingerprint);

public sealed record IdentityDecision(
    DllIdentity Chosen, string FileName, IdentityStatus Status,
    IReadOnlyList<DllIdentity> SkippedDueToConflict, string? UserPromptMessage);

public enum IdentityStatus { Resolved, RequiresUserDecision, NoCandidateAvailable }
public enum DllIdentity { Dxgi, Winmm, Version, Dbghelp, D3d12, Wininet, Winhttp, Asi }

public sealed record FileWrite(string SourcePath, string DestinationPath, long Length, string Sha256);
public sealed record BackupEntry(string OriginalPath, string BackupPath, BackupReason Reason);
public sealed record IniEdit(string Section, string Key, string? OldValue, string NewValue);
public enum BackupReason { OverwriteGameFile, OverwritePreviousOpti, IniModified }
public enum ElevationRequirement { NotRequired, Required, Declined, Blocked }

public sealed record PayloadInfo(PayloadOrigin Origin, string Version, string RootDirectory);
public enum PayloadOrigin { LocalBuild, GitHubRelease, ManualDirectory }

public sealed record PlanWarning(WarningSeverity Severity, string Message);
public enum WarningSeverity { Info, Caution, Blocking }
```

`Fingerprint` = `SHA256("<平台>|<PackageFamilyName 或 InstallRoot 绝对路径>|<exe 名>")` 取前 8 位十六进制小写。输入统一小写并做路径规范化（`Path.GetFullPath`），保证同一游戏在不同扫描轮次得到稳定值。

`Writes` 与 `Backups` 是**两个独立列表**，不是一一对应 —— OptiScaler 部署的本质是「新增一批文件」，只有极少数可能与游戏自带文件重名。硬绑成一对会让数据结构说谎。

`Warnings` 中 `Blocking` 级别的存在会让 UI 禁用「执行」按钮（v1 唯一的触发条件：体检发现游戏没有任何超分 SDK，装了也不会生效）；`Caution` 仅要求用户勾选知情后才能继续（例如身份 DLL 全冲突需覆盖游戏自带文件、或目录需提权）；`Info` 只展示（例如因冲突而换了身份、DX11 游戏走 interop 有 ~10% 损失）。

### 5.2 文件映射规则

源为 payload 根目录（`x64\Release\a\`），目标为部署目录：

| 源 | 目标 | 说明 |
|---|---|---|
| `OptiScaler.dll` | `<部署目录>\<身份名>.dll` | **重命名副本**（copy 而非 move，同一份 payload 可装到多个游戏） |
| `OptiScaler.ini` | `<部署目录>\OptiScaler.ini` | 不直拷：先经 `IniPatcher` 打 Profile + 游戏特定覆盖再落地 |
| `OptiScaler\**` | `<部署目录>\OptiScaler\**` | FSR/XeSS 运行时、`D3D12_OptiScaler\` Agility、`plugins\`、`Licenses\` |
| `Licenses\**` | `<部署目录>\Licenses\**` | |
| `setup_windows.bat` / `setup_linux.sh` / `!! EXTRACT ALL FILES...` | **跳过** | 本工具即其替代品 |
| `Remove_OptiScaler.bat` | 不存在于 payload，无需处理 | 该文件是官方 BAT **运行时生成**的，不在 `x64\Release\a\` 里。Deployer 不生成它，卸载一律走自有 `RollbackExecutor`，从而避开官方脚本的 `del /Q OptiScaler\*` |

### 5.3 部署目录的确定

一律**以选中 exe 的所在目录为准**，不是游戏根目录：

- UE 游戏（根目录存在 `Engine\`）→ `<GameRoot>\<Project>\Binaries\Win64\`
- Xbox GDK 的 UE 游戏 → `XboxGames\<Title>\content\<Project>\Binaries\Win64\`，exe 形如 `*-WinGDK-Shipping.exe`
- 其他 → exe 所在目录

这正是官方 BAT 那段 `Engine` 目录警告要防的错装位置。

### 5.4 写入顺序：身份 DLL 必须最后落

```
1. OptiScaler\**      运行时子树
2. Licenses\**
3. OptiScaler.ini     (已 patch)
4. <身份名>.dll       ← 「点火开关」，最后一步
```

理由：身份 DLL（如 `winmm.dll`）一旦出现，游戏下次启动就会加载它。若它先落地而 `OptiScaler\` 子树尚未拷完，中间任何失败都会留下一个**必崩**的半残状态。把点火开关放最后，保证「要么完整、要么不存在」。

### 5.5 原子写

单文件：

1. 目标已存在 → 先备份进影子目录，记 `BackupEntry`
2. 拷到 `<dest>.optideploy.tmp`
3. 校验 tmp 的 SHA256 与计划中记录值一致（防拷贝中途损坏 / 磁盘满）
4. `File.Replace(tmp, dest, dest + ".optideploy.bak")`；dest 原本不存在时退化为 `File.Move`
5. 成功后删 `.optideploy.bak`（原件已在影子目录，不留第二份）

整计划：执行时维护 `ExecutedSteps` 栈，**每步落盘到 `progress.json`**（进程被强杀后仍能续做回滚）。任一步失败 → 逆序回滚已完成步骤 → 抛 `DeploymentException` 附「已完成/已回滚」明细。Worker 进程同样落盘 `progress.json`，主进程在 Worker 崩溃时可据此接管回滚。

### 5.6 影子备份

- 位置：`%LOCALAPPDATA%\OptiScalerDeployer\backups\<Fingerprint>\<PlanId>\`
- `Fingerprint` = `<平台>|<PFN 或 InstallRoot>|<exe 名>` 的稳定哈希（8 位 hex），规避路径非法字符与超长
- 目录内保持相对路径镜像 + `manifest.json`（PlanId、时间戳、Payload 版本、身份、全部 Backups、全部 Writes 目标路径、Profile 快照）
- 台账 `%LOCALAPPDATA%\OptiScalerDeployer\ledger.json` 只存索引（Fingerprint → 最新 PlanId + 备份根），明细在 manifest 里
- 每个游戏保留最近 **3** 份 manifest，UI 可手动清理

---

## 6. DLL 身份决策算法

候选优先级（源自官方 BAT 的三条提示）：

```
Xbox / GDK        : winmm → version → dbghelp → wininet → winhttp → dxgi → d3d12
纯 Vulkan 游戏    : winmm → version → dbghelp → dxgi
默认 (DX11/DX12)  : dxgi  → winmm  → version → dbghelp → d3d12
```

对每个候选 `X.dll` 做冲突判定：

| 情况 | 判定 | 动作 |
|---|---|---|
| 文件不存在 | 可用 | 选中 |
| 存在，`OriginalFilename == "OptiScaler.dll"` | 上次装的 OptiScaler | 选中，静默覆盖（`OverwritePreviousOpti`） |
| 存在，是游戏自带文件 | **冲突** | 跳过该候选 + 记一条 Warning 说明为何换身份 |

`OriginalFilename` 用 `FileVersionInfo.GetVersionInfo(path).OriginalFilename` 读取，与官方 BAT 的 PowerShell 判定口径完全一致。

全部候选都冲突时 → 返回 `RequiresUserDecision`，UI 弹窗给三个选项：

- **(a)** 覆盖并备份（可从影子备份还原）
- **(b)** 改用 `OptiScaler.asi`（需游戏侧已有 ASI Loader，一般没有 → 标注不推荐）
- **(c)** 取消

---

## 7. 游戏体检（Inspection）

- **`PeImportReader`**：手写 COFF/PE 导入表解析（约 150 行，不引第三方库），返回导入的 DLL 名集合
- **`GraphicsApiDetector`**：导入 `d3d11.dll` → DX11；`d3d12.dll` / `d3d12core.dll` → DX12；`vulkan-1.dll` → Vulkan
- **`UpscalerSdkDetector`**：导入表 + 目录实际文件双路检测

  | 发现 | 推断的 Input |
  |---|---|
  | `nvngx_dlss.dll` / `_nvngx.dll` | DLSS |
  | `libxess.dll` / `libxess_dx11.dll` | XeSS |
  | `ffx_fsr2_api_x64.dll` / `ffx_fsr2_api_dx12_x64.dll` | FSR2 |
  | `ffx_fsr3upscaler_x64.dll` / `ffx_backend_dx12_x64.dll` | FSR3 |
  | `amd_fidelityfx_*.dll` | FFX API |
  | `sl.dlss*.dll` / `sl.interposer.dll` | Streamline（可用 `dlssg` 作 FGInput） |

- **`EngineDetector`**：存在 `Engine\` → UE，部署目录必须下探到 `Binaries\Win64`
- **`InspectionReport`**：汇总以上 + `Recommendation`（推荐身份、推荐 `[Inputs]` 开关、警告列表、是否「游戏无任何超分 SDK → OptiScaler 无用武之地」）

体检结果直接驱动身份决策与 Profile 中的 Input 开关，并在游戏列表里标注「不支持 / 无需 OptiScaler」。

---

## 8. Profile 与 INI 生成

### 8.1 内置 Profile

Profile = 一组 `IniEdit` + 元数据，用户可增删改，存 `%APPDATA%\OptiScalerDeployer\profiles.json`。

**`amd-rdna4`（RX 9070 XT，主力档）**

```ini
[Upscalers]
Dx12Upscaler   = ffx          ; FSR 2.3/3.1/4.x 统一走 FFX 后端
Dx11Upscaler   = ffx_12       ; 经 D3D11on12 才能吃到 FSR4（原生 DX11 只到 fsr31）
VulkanUpscaler = ffx_12       ; VK w/Dx12 interop，同上

[FSR]
UpscalerIndex  = 0            ; 0 = FSR 4.0.2。钉死，防 auto 因驱动误判回落 FSR3
FGIndex        = 0            ; 0 = FSR 4.0.0 ML-FG
Fsr4ForceModel = 0            ; 不强制，让运行时自己走 RDNA4 的 FP8 原生路径

[Spoofing]
Dxgi           = false        ; A 卡跑 FSR4 不需要伪装 N 卡，关掉能少一堆兼容性问题
Vulkan         = false

[FrameGen]
Enabled        = false        ; 默认不开，UI 上单独勾选
FGOutput       = fsrfg
```

**已确认的权衡**：`Dx11Upscaler` / `VulkanUpscaler` 用 `ffx_12`（走 DX12 interop）才能用上 FSR4，代价是约 **10% 性能损失**。默认取 `ffx_12`（画质优先），并在 UI 上暴露开关，可切到 `fsr31` / `fsr22`（原生 DX11，无损失但只有 FSR3.1 画质），切换处明确标注代价。

**`nvidia-rtx`（开发机自测档）**

```ini
[Upscalers]
Dx12Upscaler   = dlss
Dx11Upscaler   = dlss
VulkanUpscaler = dlss

[Spoofing]
Dxgi           = false        ; N 卡本来就能过检测
Vulkan         = false

[FrameGen]
Enabled        = false
FGOutput       = dlssg        ; 需要 OptiScaler\streamline\ + nvngx_dlssg.dll，不随仓库分发
```

**帧生成附加档**（勾选后才叠加，不默认开）：`FrameGen.Enabled=true`、`FGInput=upscaler`（体检发现游戏原生有 DLSSG/Streamline 时改 `dlssg`）、`FGOutput=fsrfg`、`FGIndex=0`、`OptiFG.HUDFix=true`。

### 8.2 `IniPatcher`：保留注释的部分改写

**不重新序列化整个文件。** `OptiScaler.ini` 有 51 KB，一半价值在注释里（每个键的取值范围与默认行为说明）；用 SimpleIni / IniParser 这类库会把注释全丢掉，后续排查问题会很痛苦。

逐行状态机算法：

1. 扫描时维护 `currentSection`
2. `Key=Value` 行命中某条 edit → **只替换 `=` 右侧**，保留前导空白与该行上方的注释块
3. 扫完后未命中的 edit（原文件没这个键）→ 追加到对应 section 末尾；section 也不存在则在文件尾新建并加一行来源注释
4. 返回 diff（`OldValue → NewValue`）供 UI 预演

**可断言的不变式**（直接写成单测）：所有 `;` / `#` 开头的行原样保留；输出行数 ≥ 输入行数。

**核心原则：最小干预。** 只钉死必须确定的键，其余一律保持 `auto`，让 OptiScaler 自己的 per-game quirks 逻辑继续生效 —— 它内置了大量游戏专属修复，全量覆盖会把这些全废掉。

### 8.3 必须处理的坑：默认不写日志

`[Log] LogToFile=auto` 的默认值是 **`false`**，即默认根本不生成 `OptiScaler.log`，健康检查会永远超时。

因此 planner 部署时必须额外写入 `[Log] LogToFile=true` 与 `LogLevel=2`。健康检查通过后，UI 提供选择：「关掉日志（略微提升性能）」或「保留日志（便于后续排查）」。

### 8.4 「每个游戏单独注入」的两个落地点

1. **`[ProcessFilter] TargetProcessName` 自动填该游戏的 exe 名。** `winmm.dll` / `version.dll` 这类身份会被同目录下**任何**加载它的进程命中（启动器、崩溃处理器、反作弊、EOSSDK）。锁死目标进程名，把注入范围收敛到游戏本体一个进程。该键由 planner 填，不属于 Profile。
2. **`GameFingerprint` 粒度的独立台账与独立备份**，游戏之间零共享状态，卸载 A 绝不可能碰到 B。

---

## 9. Payload 来源

```csharp
public interface IPayloadSource
{
    string Description { get; }
    Task<PayloadBundle?> TryGetAsync(CancellationToken ct);
}
```

- **`LocalBuildPayloadSource`**：读 `<repo>\x64\Release\a\`，版本从 `OptiScaler.dll` 的 `FileVersionInfo` 取
- **`GitHubReleasePayloadSource`**：`releases/latest`（可切 `releases/tags/nightly`）→ 下载 7z → 用 `SharpCompress` 解到 `%LOCALAPPDATA%\OptiScalerDeployer\payload-cache\<version>\`
- **`PayloadSourceChain`**：按「本地构建优先 → Releases 兜底」顺序尝试；设置页可切换来源、可手动指定任意 payload 目录

本地构建优先的意义：能验证自己对 OptiScaler C++ 源码的修改。

---

## 10. 错误处理与诊断

Core 抛具体异常类型，每个都带 `UserMessage`（给用户看的中文短句）+ `Diagnostic`（给日志的技术细节）：
`PayloadNotFoundException` / `GameNotWritableException` / `IdentityConflictException` / `ChecksumMismatchException` / `ElevationDeclinedException` / `HealthCheckFailedException(LogExcerpt)`。

App 层有全局 `IErrorPresenter`，把异常映射成 UI 上的「出了什么问题 / 建议怎么做 / 详细日志」三段式。

| 失败点 | 处理 |
|---|---|
| Payload 缺失（没构建、也没网） | 阻塞，给两条出路：VS2022 构建一次 / 手动指定 payload 目录 |
| SHA256 校验不符 | 立即中止并回滚该步，提示磁盘满或杀软拦截 |
| UAC 被拒 | **不算错误**，标 `Elevation.Declined`，说明「该游戏在受保护的 WindowsApps 里」 |
| Worker 崩溃 / 被杀 | 主进程读不到 result.json → 依据 Worker 落盘的 `progress.json` 接管回滚 |
| 健康检查 60s 无日志 | **不自动回滚**，给三选项：再等 / 查看日志 / 回滚（有些游戏启动就是慢，或要先过 launcher） |
| 日志出现 Error/Fatal | 展示日志摘要 + 一键回滚 |
| 下次扫描发现身份 DLL 消失 | 台账标 `NeedsReinstall`（Xbox 应用「修复/校验」会清掉它） |

健康检查判据：

- **成功信号**：日志出现 OptiScaler 版本行 + 选中的 upscaler 名称
- **失败信号**：`level >= error` 的行；重点关注 `amdxc64.dll` 加载失败、FSR4 模型缺失、spoofing 未生效

---

## 11. 测试策略

### 11.1 单元测试（xUnit + `System.IO.Abstractions` 内存文件系统）

- `IniPatcher` —— §8.2 两条不变式 + 缺失键追加位置正确 + diff 输出正确
- `DllIdentitySelector` —— 全分支：无冲突 / 上次 Opti / 游戏自带冲突 / 全冲突 / Vulkan 优先级 / Xbox 优先级
- `DeploymentPlanner` —— UE 布局、GDK 布局、扁平布局三种；写入顺序断言（身份 DLL 最后）
- `UpscalerSdkDetector` + `PeImportReader` —— 真实小样本 exe/dll 做 fixture
- `RollbackExecutor` —— 执行一个 plan 后回滚，断言目录与执行前**逐字节一致**
- `IJobTransport` —— 用 fake 实现覆盖提权通道的序列化 / 失败 / 超时 / 校验和不符

### 11.2 集成测试

`Deployer.TestData` 造三个假游戏目录：

- `FakeUeGame`：含 `Engine\` + `<Project>\Binaries\Win64\`
- `FakeGdkGame`：`content\<Project>\Binaries\Win64\*-WinGDK-Shipping.exe`
- `FakeConflictGame`：自带 `version.dll` 与 `winmm.dll`

跑完整 plan → execute → rollback 循环。Payload 可用真产物（若 `x64\Release\a\` 存在）或合成 payload（造假 dll）。

Scanner 的 `libraryfolders.vdf` / `appmanifest_*.acf` / `PackageManager` 结果全部 fixture 注入，不依赖真机环境。

### 11.3 手动验收清单（无法自动化）

- **开发机（N 卡）**：选一个 Steam DX12 游戏 → 部署 NVIDIA 档 → 启动 → `Insert` 出菜单 → 确认 Output = DLSS
- **家里（RX 9070 XT）**：选一个 Xbox 游戏 → 部署 AMD 档 → 确认 `OptiScaler.log` 里 upscaler = FSR4、`amdxc64.dll` 加载成功、`Insert` 菜单可开

### 11.4 不测

WPF View 层 —— 逻辑全部下沉到 ViewModel 与 Core，View 只做绑定。

---

## 12. 已确认的决策清单

| # | 决策 | 结论 |
|---|---|---|
| 1 | 输出后端策略 | 双 Profile（`amd-rdna4` / `nvidia-rtx`）可切换 |
| 2 | 技术栈 | C# WPF (.NET 8)，独立 sln 置于 `tools/OptiScalerDeployer/` |
| 3 | 扫描范围 | 插件式 `IGameScanner` 架构，Xbox 做深，Steam/Epic/Manual 最小实现 |
| 4 | Payload 来源 | 本地构建 `x64\Release\a\` 优先，GitHub Releases 兜底 |
| 5 | v1 功能范围 | 核心链路 + 游戏体检与自动推荐；**不做批量部署** |
| 6 | 安全模型 | 影子备份 + manifest 精确回滚 + 部署后自动健康检查回滚 |
| 7 | 权限模型 | 方案 A：主进程 `asInvoker` + 按需提权 `Deployer.Worker` |
| 8 | DX11/Vulkan 后端 | `ffx_12` 为默认（FSR4 画质，~10% 性能损失），UI 可切 `fsr31` |
| 9 | 反作弊检测 | **不做**。用户不玩在线游戏，从 v1 范围移除（见 §1.5） |

---

## 13. 风险与开放问题

| 风险 | 缓解 |
|---|---|
| 老式 MSIXVC / UWP 商店游戏在 `WindowsApps` 里，提权后仍可能因加密 exe 或包完整性检查失败 | 体检阶段识别并明确标为「不支持」，给出原因；不尝试强行部署 |
| Xbox 应用「修复/校验」会清掉侧载 DLL | 台账 `NeedsReinstall` 状态 + 扫描时自动检测并提示 |
| OptiScaler 上游 INI 键名变动 | `IniPatcher` 对「键不存在」的处理是追加而非报错；planner 在 payload 版本变化时提示重新体检 |
| FSR4 需要 `amdxc64.dll`，缺失时静默回落 FSR3 | 健康检查解析日志确认实际生效的后端，而不只看部署成功 |
