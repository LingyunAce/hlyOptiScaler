# OptiScaler Deployer — Plan A: Core 引擎 实施计划

> **状态说明：** 本文前半部分包含历史执行步骤；实施与验证结果请以每个 Task 的当前状态摘要、Task 16 和对应源码为准。

**Goal:** 构建 OptiScaler Deployer 的核心引擎 —— 一个无 UI、可完全单测的 C# 类库，能对任意游戏目录**安全部署**（影子备份 + 原子写 + 写入顺序保证）并**精确回滚** OptiScaler，配一个最小 CLI 做端到端验证。

**Architecture:** `Deployer.Core` 模块单向依赖，`DeploymentPlanner` 是唯一汇聚点。`DeploymentPlan` 是不可变纯数据 record；执行器按安全顺序完成备份、写入和回滚。大多数 Core 测试使用内存文件系统；CLI 集成测试使用隔离临时目录，无需安装真实游戏或 GPU 验收。

**Tech Stack:** .NET 8 / C# 12 / xUnit / System.IO.Abstractions（+ TestingHelpers 的 `MockFileSystem`）/ System.Text.Json / `dotnet` CLI

**Spec:** `docs/superpowers/specs/2026-10-08-optiscaler-deployer-design.md`（§5 部署计划、§6 身份决策、§8 Profile 与 INI、§11 测试策略）

> **实施记录说明：** 本计划中的原始实现代码块、命令输出和 `Expected: 通过 N` 数字是设计阶段的历史参考，不保证与当前源码一致，也不能直接作为执行指引。已完成任务的安全行为以 `tools/OptiScalerDeployer/src/` 为准；Task 10–12、14、15 已用当前行为摘要与源码/测试链接替代旧的不安全实现示例。最终验证数字只记录协调者实际运行的结果。

---

## 计划分片说明

Spec 覆盖 4 个可独立交付的子系统，塞进一份计划会产生上千步骤且中途没有可运行软件。已拆为 4 份计划：

| 计划 | 内容 | 产出 |
|---|---|---|
| **A（本文件）** | Core 引擎：Ini / Profiles / Payload / Identity / Planning / Deployment / Ledger + 最小 CLI | 对任意目录安全部署并回滚，全单测覆盖 |
| B | 游戏发现与体检：PE 导入表解析 + 四个 Scanner | CLI 列出全部游戏与体检报告，接入 A |
| C | WPF UI | 图形界面 |
| D | 提权 Worker + 健康检查 + GitHub Releases payload | WindowsApps 老游戏、部署后自动验证回滚 |

**A 与后续计划的接缝**：`InspectionSummary`（游戏体检结果的数据形状）在本计划定义，但**填充它的检测器**（`PeImportReader` / `GraphicsApiDetector` / `UpscalerSdkDetector`）属于计划 B。本计划的 CLI 用手工填写的 `InspectionSummary` 驱动 planner，接缝即此。

---

## 文件结构

所有路径相对仓库根 `d:\AI\project\hlyOptiScaler`。

```
tools/OptiScalerDeployer/
├── OptiScalerDeployer.sln
├── Directory.Build.props                 公共编译开关
├── Directory.Packages.props              集中包版本管理（CPM）
├── .gitignore                            bin/ obj/
├── src/
│   ├── Deployer.Core/                    net8.0 类库，零 UI 依赖
│   │   ├── Deployer.Core.csproj
│   │   ├── Abstractions/
│   │   │   ├── IFileVersionReader.cs     读 PE 版本资源（OriginalFilename）
│   │   │   └── WindowsFileVersionReader.cs  真实实现，包装 FileVersionInfo
│   │   ├── Serialization/
│   │   │   └── DeployerJson.cs           唯一 JsonSerializerOptions 来源
│   │   ├── Util/
│   │   │   └── Hashing.cs                SHA256 文件/字符串
│   │   ├── Errors/
│   │   │   └── DeployerException.cs      基类 + 6 个具体异常
│   │   ├── Domain/
│   │   │   ├── Enums.cs                  GamePlatform / DirectoryWritability / DllIdentity /
│   │   │   │                             IdentityStatus / BackupReason / ElevationRequirement /
│   │   │   │                             WarningSeverity / PayloadOrigin / GraphicsApi / UpscalerSdk
│   │   │   ├── DllIdentityMap.cs         DllIdentity → 落地文件名
│   │   │   ├── GameTarget.cs
│   │   │   ├── InspectionSummary.cs
│   │   │   ├── UpscalerProfile.cs
│   │   │   ├── PayloadBundle.cs          + PayloadInfo
│   │   │   ├── IniEdit.cs                + FileWrite + BackupEntry + PlanWarning
│   │   │   ├── DeploymentPlan.cs
│   │   │   ├── DeploymentManifest.cs     影子备份内的 manifest.json 形状
│   │   │   ├── DeploymentProgress.cs     progress.json 诊断信息
│   │   │   └── InstallRecord.cs          + InstallState
│   │   ├── Ini/
│   │   │   ├── IIniPatcher.cs            + IniPatchResult
│   │   │   └── IniPatcher.cs             保留注释的逐行状态机
│   │   ├── Profiles/
│   │   │   ├── BuiltinProfiles.cs        amd-rdna4 / nvidia-rtx / framegen 附加档
│   │   │   └── ProfileStore.cs           profiles.json 读写 + 内置兜底
│   │   ├── Payload/
│   │   │   ├── IPayloadSource.cs
│   │   │   ├── LocalBuildPayloadSource.cs  读 x64\Release\a\
│   │   │   └── PayloadSourceChain.cs     顺序尝试，全失败抛 PayloadNotFoundException
│   │   ├── Identity/
│   │   │   ├── IDllIdentitySelector.cs   + IdentityContext
│   │   │   └── DllIdentitySelector.cs    优先级表 + OriginalFilename 冲突判定
│   │   ├── Planning/
│   │   │   ├── IFingerprintCalculator.cs + FingerprintCalculator.cs
│   │   │   ├── PayloadWalker.cs          payload → 有序 FileWrite 列表
│   │   │   ├── IDeploymentPlanner.cs     + PlanRequest
│   │   │   └── DeploymentPlanner.cs      唯一汇聚点
│   │   ├── Deployment/
│   │   │   ├── AtomicFileWriter.cs       tmp → 校验 → File.Replace
│   │   │   ├── IBackupStore.cs           + BackupStore.cs   影子备份 + manifest
│   │   │   ├── IDeploymentExecutor.cs    + DeploymentExecutor.cs + DeploymentResult
│   │   │   └── IRollbackExecutor.cs      + RollbackExecutor.cs   + RollbackResult
│   │   └── Persistence/
│   │       ├── IInstallLedger.cs
│   │       └── JsonInstallLedger.cs      %LOCALAPPDATA%\OptiScalerDeployer\ledger.json
│   └── Deployer.Cli/                     net8.0 控制台，端到端验证入口
│       ├── Deployer.Cli.csproj
│       └── Program.cs                    plan / apply / rollback / ledger 四个子命令
└── tests/
    ├── Deployer.TestData/                测试夹具工厂（非测试项目）
    │   ├── Deployer.TestData.csproj
    │   ├── SyntheticPayloadFactory.cs    造一个假 x64\Release\a\ 到 MockFileSystem
    │   └── FakeGameFactory.cs            FakeUeGame / FakeGdkGame / FakeConflictGame
    └── Deployer.Core.Tests/
        ├── Deployer.Core.Tests.csproj
        ├── JsonAssert.cs                 含集合的 record 用序列化比相等（见 Task 3 Step 7）
        ├── WindowsFileVersionReaderTests.cs  FileVersionInfo 真实文件系统边界测试（FileVersionInfo 用不了 MockFileSystem）
        ├── DeployerExceptionTests.cs     DeployerException 基类契约（Message 带用户话术、ToString 追加诊断）
        ├── IniPatcherTests.cs
        ├── BuiltinProfilesTests.cs
        ├── LocalBuildPayloadSourceTests.cs
        ├── DllIdentitySelectorTests.cs
        ├── FingerprintCalculatorTests.cs
        ├── PayloadWalkerTests.cs
        ├── DeploymentPlannerTests.cs
        ├── DeploymentExecutorTests.cs
        ├── RollbackExecutorTests.cs
        ├── JsonInstallLedgerTests.cs
        ├── EndToEndTests.cs              plan → apply → rollback 逐字节一致
        └── CliTests.cs                   CLI 命令与磁盘集成验证
```

**分解原则**：一个文件一个职责；`Domain/` 下全是无行为的 record/enum（唯一例外 `DllIdentityMap`，因为身份↔文件名的映射必须只有一份）；每个模块的接口与实现分文件，大多数 Core 测试使用 MockFileSystem；CLI 集成测试使用隔离临时目录，文件版本读取也覆盖真实文件系统边界。

---

## Task 1: 解决方案骨架与工具链

**Files:**
- Create: `tools/OptiScalerDeployer/Directory.Build.props`
- Create: `tools/OptiScalerDeployer/Directory.Packages.props`
- Create: `tools/OptiScalerDeployer/.gitignore`
- Create: `tools/OptiScalerDeployer/OptiScalerDeployer.sln`（由 `dotnet new sln` 生成）
- Create: `tools/OptiScalerDeployer/src/Deployer.Core/Deployer.Core.csproj`
- Create: `tools/OptiScalerDeployer/src/Deployer.Cli/Deployer.Cli.csproj`
- Create: `tools/OptiScalerDeployer/tests/Deployer.Core.Tests/Deployer.Core.Tests.csproj`
- Create: `tools/OptiScalerDeployer/tests/Deployer.TestData/Deployer.TestData.csproj`

- [x] **Step 1: 安装 .NET 8 SDK**

本机现状已核实：`C:\Program Files\dotnet` 只有 host 与 shared，`dotnet --list-sdks` 返回 `No .NET SDKs were found`。必须先装 SDK。

Run:
```powershell
winget install --id Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements
```

装完**必须重开一个终端**（PATH 不会热更新），然后验证：

Run: `dotnet --list-sdks`
Expected: 输出一行形如 `8.0.xxx [C:\Program Files\dotnet\sdk]`

若 winget 装不动，改用官方安装脚本：
```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1"
& "$env:TEMP\dotnet-install.ps1" -Channel 8.0
```
此时 SDK 落在 `$env:LOCALAPPDATA\Microsoft\dotnet`，后续所有 `dotnet` 命令改用该绝对路径。

- [x] **Step 2: 建目录树**

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler"
New-Item -ItemType Directory -Force -Path "tools\OptiScalerDeployer\src","tools\OptiScalerDeployer\tests" | Out-Null
```
Expected: 无输出，`Test-Path "tools\OptiScalerDeployer\src"` 为 `True`

- [x] **Step 3: 写 `Directory.Build.props`**

`tools/OptiScalerDeployer/Directory.Build.props`：
```xml
<Project>
  <PropertyGroup>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>8.0</AnalysisLevel>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

`AnalysisLevel` 钉死在 `8.0`：它默认解析为 `latest`，意味着分析器规则集会随本机装了哪个 SDK 而漂移，叠上 `TreatWarningsAsErrors=true` 就会制造「代码一行没动、构建突然红」的故障；钉版本让规则集可复现。**不要**再加 `AnalysisMode`（`Recommended` 会打开一批额外规则，在本工程里等于一批新的硬错误），保持隐式默认。`CA1416`（Windows 平台兼容性）**不做任何压制**：本产品确实是 Windows 专用工具、`Core` 却定 `net8.0`（而非 `net8.0-windows`）以便测试宿主轻量，但原计划据此认为 `WindowsFileVersionReader`（Task 3）用到的 `System.Diagnostics.FileVersionInfo` 会触发 CA1416，已在 SDK 8.0.425 上实测证伪——.NET 8 的 `FileVersionInfo` 并未标注 `[SupportedOSPlatform("windows")]`，因此不产生 CA1416，无需任何 suppression 即可干净编译；而一条没有事实依据的全局 `NoWarn` 一旦写下，后面的代码会长在它上面，就再也摘不掉了。若将来真的引入 Windows 专有 API（Plan D 的注册表 / `WindowsApps` 访问），正确机制是在**那个**程序集上加 `[assembly: SupportedOSPlatform("windows")]`，而不是全局 `NoWarn`；同样**不要**用 `#pragma warning disable` 逐处压制——那是把全局决策散落成局部噪音。

- [x] **Step 4: 写 `.gitignore`**

`tools/OptiScalerDeployer/.gitignore`：
```
bin/
obj/
*.user
TestResults/
```

- [x] **Step 5: 建四个项目**

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet new classlib -o src\Deployer.Core -n Deployer.Core -f net8.0
dotnet new console  -o src\Deployer.Cli  -n Deployer.Cli  -f net8.0
dotnet new classlib -o tests\Deployer.TestData -n Deployer.TestData -f net8.0
dotnet new xunit    -o tests\Deployer.Core.Tests -n Deployer.Core.Tests -f net8.0
Remove-Item src\Deployer.Core\Class1.cs, tests\Deployer.TestData\Class1.cs -ErrorAction SilentlyContinue
```
Expected: 四次 `The template ... was created successfully.`

`dotnet new` 生成的 csproj 已含 `TargetFramework`，但**不含** Step 3 里那些公共开关之外的东西——需要手工覆盖成下面的内容。

- [x] **Step 6: 写 `Directory.Packages.props`，并覆盖四个 csproj**

**6a. `tools/OptiScalerDeployer/Directory.Packages.props`（集中包版本管理，CPM）：**
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="System.IO.Abstractions" Version="21.0.29" />
    <PackageVersion Include="System.IO.Abstractions.TestingHelpers" Version="21.0.29" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

这个文件守的是一条**类型同一性**不变式：`System.IO.Abstractions` 与 `System.IO.Abstractions.TestingHelpers` 必须在所有项目里解析到完全相同的版本，否则 `MockFileSystem`（来自 TestingHelpers）与 `IFileSystem`（来自 Abstractions）会落在两个不同的程序集版本上而类型不兼容。危险在于失败模式：NuGet 对版本*冲突*只做静默向上统一（只有降级才报 NU1605），所以版本不一致既不报错也不警告，只在**运行期**炸成一个难懂的类型身份异常。把版本硬写在四个 csproj 里、仅靠人肉纪律保持一致是不可接受的，CPM 让这条不变式由构建机械保证。

开启 CPM 后，csproj 里的 `<PackageReference>` **不能再带 `Version` 属性**（带了就是 NU1008 错误），所以下面四个 csproj 一律只写 `Include`。

**不要**加 `<CentralPackageTransitivePinningEnabled>`：`TestableIO.*` 那些传递依赖的版本本来就由 `System.IO.Abstractions` 自己精确锁定，开它收益几乎为零，反而带来 NU1605 降级错误的风险。

**6b. 四个 csproj：**

`src/Deployer.Core/Deployer.Core.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>Deployer.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.IO.Abstractions" />
  </ItemGroup>
</Project>
```

`tests/Deployer.TestData/Deployer.TestData.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>Deployer.TestData</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.IO.Abstractions" />
    <PackageReference Include="System.IO.Abstractions.TestingHelpers" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Deployer.Core\Deployer.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/Deployer.Core.Tests/Deployer.Core.Tests.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>Deployer.Core.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="System.IO.Abstractions" />
    <PackageReference Include="System.IO.Abstractions.TestingHelpers" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Deployer.Core\Deployer.Core.csproj" />
    <ProjectReference Include="..\Deployer.TestData\Deployer.TestData.csproj" />
  </ItemGroup>
</Project>
```

`src/Deployer.Cli/Deployer.Cli.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>Deployer.Cli</RootNamespace>
    <AssemblyName>opti-deploy</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.IO.Abstractions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Deployer.Core\Deployer.Core.csproj" />
  </ItemGroup>
</Project>
```

包版本已核实为 .NET 8 兼容的当前稳定版。若 `dotnet restore` 报某个版本不存在，用 `dotnet package search System.IO.Abstractions --take 1` 查实际最新版，然后**只改 `Directory.Packages.props` 这一个地方**——CPM 之下四个 csproj 根本不含版本号，既不需要、也不允许再去「四处同步替换」（真在 csproj 里写 `Version` 会直接报 NU1008）。注意 `System.IO.Abstractions` 与 `System.IO.Abstractions.TestingHelpers` 必须一起改成同一个版本，理由见 6a。

- [x] **Step 7: 建 sln 并挂载**

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet new sln -n OptiScalerDeployer
dotnet sln add src\Deployer.Core\Deployer.Core.csproj
dotnet sln add src\Deployer.Cli\Deployer.Cli.csproj
dotnet sln add tests\Deployer.TestData\Deployer.TestData.csproj
dotnet sln add tests\Deployer.Core.Tests\Deployer.Core.Tests.csproj
```
Expected: 四次 `已将项目"..."添加到解决方案中。`（或英文 `added to the solution`）

**不要**把这个 sln 加进 `OptiScaler.sln`，也不要动 `.github/workflows/build.yml`。nightly CI 直接依赖 `msbuild OptiScaler.sln` 与 `x64\Release\a\*.*`，混入 C# 项目会干扰那条流水线（spec §3.1）。

- [x] **Step 8: 验证构建与测试空跑**

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet build OptiScalerDeployer.sln -c Debug
dotnet test OptiScalerDeployer.sln --no-build
```
Expected: build `0 个警告 / 0 个错误`（`0 Warning(s) 0 Error(s)`）；test 通过 1 个由 `dotnet new xunit` 生成的 `UnitTest1.Test1`。

- [x] **Step 9: 删掉模板测试**

删除 `tests/Deployer.Core.Tests/UnitTest1.cs`。

Run: `dotnet test OptiScalerDeployer.sln`
Expected: **退出码 0**（PowerShell 里 `$LASTEXITCODE` 为 0）——这是唯一的通过判据。测试程序集此刻零个测试，VSTest 17.11.1 只会打印一行 `...\Deployer.Core.Tests.dll 中没有可用测试。请确保已注册测试发现器和执行器且平台和框架版本设置合理，然后重试。`，并且**不会**打印 `已通过! - 失败: 0，通过: 0` 那样的汇总行。看不到「已通过」不等于失败，别去「修」它。

- [x] **Step 10: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "chore(deployer): scaffold OptiScalerDeployer solution (Plan A task 1)"
```

---

## Task 2: 领域类型（record / enum）

这一批是纯数据，没有行为，因此**不写单测**（测 record 的相等性是测编译器）。但它们是后续所有任务的地基，必须一次定全，避免后面回头改签名。

**Files:**
- Create: `src/Deployer.Core/Domain/Enums.cs`
- Create: `src/Deployer.Core/Domain/DllIdentityMap.cs`
- Create: `src/Deployer.Core/Domain/GameTarget.cs`
- Create: `src/Deployer.Core/Domain/InspectionSummary.cs`
- Create: `src/Deployer.Core/Domain/UpscalerProfile.cs`
- Create: `src/Deployer.Core/Domain/PayloadBundle.cs`
- Create: `src/Deployer.Core/Domain/IniEdit.cs`
- Create: `src/Deployer.Core/Domain/DeploymentPlan.cs`
- Create: `src/Deployer.Core/Domain/DeploymentManifest.cs`
- Create: `src/Deployer.Core/Domain/DeploymentProgress.cs`
- Create: `src/Deployer.Core/Domain/InstallRecord.cs`
- Test: `tests/Deployer.Core.Tests/DllIdentityMapTests.cs`（唯一有行为的一个）

- [x] **Step 1: 写 `Domain/Enums.cs`**

```csharp
namespace Deployer.Core.Domain;

/// <summary>游戏来源平台。决定身份 DLL 的候选优先级。</summary>
public enum GamePlatform { Xbox, Steam, Epic, Manual }

/// <summary>部署目录的可写性探测结果。</summary>
public enum DirectoryWritability { Writable, NeedsElevation, Blocked, Unknown }

/// <summary>OptiScaler 可以伪装的 DLL 身份。顺序无意义，优先级在 IdentityContext 的候选表里。</summary>
public enum DllIdentity { Dxgi, Winmm, Version, Dbghelp, D3d12, Wininet, Winhttp, Asi }

public enum IdentityStatus { Resolved, RequiresUserDecision, NoCandidateAvailable }

/// <summary>为什么要把某个原件收进影子备份。</summary>
public enum BackupReason { OverwriteGameFile, OverwritePreviousOpti, IniModified }

public enum ElevationRequirement { NotRequired, Required, Declined, Blocked }

public enum WarningSeverity { Info, Caution, Blocking }

public enum PayloadOrigin { LocalBuild, GitHubRelease, ManualDirectory }

/// <summary>游戏使用的图形 API。一个游戏可能同时用多个（UE 常见 DX12 + Vulkan）。</summary>
public enum GraphicsApi { Dx11, Dx12, Vulkan }

/// <summary>游戏内已存在的超分 SDK。决定 OptiScaler 该开哪些 Input。</summary>
public enum UpscalerSdk { Dlss, Streamline, XeSS, Fsr2, Fsr3, FfxApi }

public enum InstallState { Installed, Healthy, RolledBack, NeedsReinstall, Failed }
```

- [x] **Step 2: 写 `Domain/DllIdentityMap.cs`**

身份↔文件名的映射必须只有一份真相，否则 planner 与 rollback 会对不上号。

```csharp
namespace Deployer.Core.Domain;

public static class DllIdentityMap
{
    private static readonly Dictionary<DllIdentity, string> FileNames = new()
    {
        [DllIdentity.Dxgi]     = "dxgi.dll",
        [DllIdentity.Winmm]    = "winmm.dll",
        [DllIdentity.Version]  = "version.dll",
        [DllIdentity.Dbghelp]  = "dbghelp.dll",
        [DllIdentity.D3d12]    = "d3d12.dll",
        [DllIdentity.Wininet]  = "wininet.dll",
        [DllIdentity.Winhttp]  = "winhttp.dll",
        [DllIdentity.Asi]      = "OptiScaler.asi",
    };

    public static string FileName(this DllIdentity identity) => FileNames[identity];
}
```

- [x] **Step 3: 写 `Domain/GameTarget.cs`**

```csharp
namespace Deployer.Core.Domain;

/// <summary>
/// 决策后的部署目标。与扫描阶段的 DiscoveredGame（计划 B）区分：
/// GameTarget 有唯一的 DeployDirectory 与唯一的 ExecutableName。
/// </summary>
public sealed record GameTarget(
    string Title,
    GamePlatform Platform,
    string? PackageFamilyName,
    string? AppId,
    string InstallRoot,
    string DeployDirectory,
    string ExecutableName,
    string Fingerprint);
```

- [x] **Step 4: 写 `Domain/InspectionSummary.cs`**

本计划只定义形状并由 CLI 手工填充；真正的检测器在计划 B。

```csharp
namespace Deployer.Core.Domain;

/// <summary>
/// 游戏体检结果中 planner 需要的部分。由计划 B 的 IGameInspector 填充；
/// 计划 A 的 CLI 与测试手工构造。
/// </summary>
public sealed record InspectionSummary
{
    public required IReadOnlyList<GraphicsApi> GraphicsApis { get; init; }
    public required IReadOnlyList<UpscalerSdk> DetectedSdks { get; init; }
    public required bool HasEngineFolder { get; init; }
    public required bool IsVulkanOnly { get; init; }

    /// <summary>
    /// 游戏没有任何超分 SDK —— 装了 OptiScaler 也不会生效。
    /// planner 据此产出 Blocking 级警告。
    /// </summary>
    public bool HasAnyUpscalerSdk => DetectedSdks.Count > 0;

    public static InspectionSummary Empty { get; } = new()
    {
        GraphicsApis = Array.Empty<GraphicsApi>(),
        DetectedSdks = Array.Empty<UpscalerSdk>(),
        HasEngineFolder = false,
        IsVulkanOnly = false,
    };
}
```

- [x] **Step 5: 写 `Domain/UpscalerProfile.cs`**

```csharp
namespace Deployer.Core.Domain;

/// <summary>
/// 一组 INI 覆盖 + 元数据。Profile 不含游戏特定项
/// （TargetProcessName / LogToFile 由 planner 追加，不属于 Profile）。
/// </summary>
public sealed record UpscalerProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required bool IsBuiltin { get; init; }
    public required IReadOnlyList<IniEdit> Edits { get; init; }
}
```

- [x] **Step 6: 写 `Domain/PayloadBundle.cs`**

```csharp
namespace Deployer.Core.Domain;

/// <summary>一份可用的 OptiScaler 产物目录。RootDirectory 即 x64\Release\a\ 或解压后的缓存目录。</summary>
public sealed record PayloadBundle(string RootDirectory, string Version, PayloadOrigin Origin);

/// <summary>写进 plan 与 manifest 的 payload 快照。</summary>
public sealed record PayloadInfo(PayloadOrigin Origin, string Version, string RootDirectory)
{
    public static PayloadInfo From(PayloadBundle bundle) =>
        new(bundle.Origin, bundle.Version, bundle.RootDirectory);
}
```

- [x] **Step 7: 写 `Domain/IniEdit.cs`**

```csharp
namespace Deployer.Core.Domain;

/// <summary>
/// 一条 INI 覆盖。OldValue 在 planner 阶段为 null（此时还没读文件），
/// 由 IniPatcher 落地时回填实际旧值，供 UI 预演 diff。
/// </summary>
public sealed record IniEdit(string Section, string Key, string? OldValue, string NewValue)
{
    public IniEdit WithOldValue(string? oldValue) => this with { OldValue = oldValue };
}

/// <summary>一次文件写入。DestinationPath 为绝对路径。</summary>
public sealed record FileWrite(string SourcePath, string DestinationPath, long Length, string Sha256);

/// <summary>
/// 一条影子备份。Sha256 在 planner 阶段为 null（planner 不哈希游戏自带文件——太慢，
/// 也不是它的职责），由 BackupStore.Backup 在真正拷贝的那一刻回填。与 IniEdit.OldValue
/// 是同一个模式：计划期不可知的值，落地期补上。
/// </summary>
/// <remarks>
/// 为什么**可空**而不是 required：本 record 会进 manifest.json 长期落盘，受
/// DeploymentManifest 的前向兼容约定管辖；且哈希在计划阶段确实不可知。
///
/// 为什么值得记：回滚时它是唯一的**独立**参照物。备份件在存放期间可能被磁盘错误、
/// 杀软或云占位符水合失败改动过；没有哈希就只能盲拷回游戏目录并宣称回滚成功——
/// 那正是本工具承诺绝不发生的事。注意这**不是**「校验它等于校验自己」：哈希是备份
/// 当时记下的，与回滚那一刻的磁盘状态分属两个时间点，相互独立。
/// </remarks>
public sealed record BackupEntry(string OriginalPath, string BackupPath, BackupReason Reason, string? Sha256 = null)
{
    public BackupEntry WithSha256(string? sha256) => this with { Sha256 = sha256 };
}

public sealed record PlanWarning(WarningSeverity Severity, string Message);
```

- [x] **Step 8: 写 `Domain/DeploymentPlan.cs`**

```csharp
namespace Deployer.Core.Domain;

public sealed record DeploymentPlan
{
    public required Guid PlanId { get; init; }
    public required GameTarget Game { get; init; }
    public required PayloadInfo Payload { get; init; }
    public required IdentityDecision Identity { get; init; }
    public required UpscalerProfile Profile { get; init; }
    public required DirectoryWritability Writability { get; init; }
    public required ElevationRequirement Elevation { get; init; }

    /// <summary>有序。身份 DLL 必须是最后一项（spec §5.4）。</summary>
    public required IReadOnlyList<FileWrite> Writes { get; init; }

    public required IReadOnlyList<BackupEntry> Backups { get; init; }
    public required IReadOnlyList<IniEdit> IniEdits { get; init; }
    public required IReadOnlyList<PlanWarning> Warnings { get; init; }

    public bool HasBlockingWarning =>
        Warnings.Any(w => w.Severity == WarningSeverity.Blocking);
}

public sealed record IdentityDecision(
    DllIdentity Chosen,
    string FileName,
    IdentityStatus Status,
    IReadOnlyList<DllIdentity> SkippedDueToConflict,
    string? UserPromptMessage);
```

- [x] **Step 9: 写 `Domain/DeploymentManifest.cs`**

回滚的唯一依据。写进影子备份目录。

```csharp
namespace Deployer.Core.Domain;

/// <summary>
/// 影子备份目录内的 manifest.json。RollbackExecutor 只认这个文件，
/// 绝不扫描目录猜测该删什么。
/// </summary>
/// <remarks>
/// 本类型跨版本长寿命持久化：安装时写盘，可能数月后被更高版本的工具读回，
/// 而「任何时候都能回滚」正是本工具的核心承诺。System.Text.Json 在反序列化
/// 缺失 <c>required</c> 成员的 JSON 时会抛异常——一旦某次升级给本类型加了
/// <c>required</c> 字段，磁盘上旧版本写下的 manifest 就会读不回来，用户将
/// 无法回滚当初装过的游戏。因此 v1 发布之后，对本类型的任何新增字段
/// **必须是可空类型或带默认值**，禁止使用 <c>required</c>；
/// 若确实需要破坏性变更，先引入 SchemaVersion 并写兼容分支。
/// </remarks>
public sealed record DeploymentManifest
{
    public required Guid PlanId { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required string Fingerprint { get; init; }
    public required string GameTitle { get; init; }
    public required string DeployDirectory { get; init; }
    public required PayloadInfo Payload { get; init; }
    public required IdentityDecision Identity { get; init; }
    public required UpscalerProfile Profile { get; init; }
    public required IReadOnlyList<BackupEntry> Backups { get; init; }

    /// <summary>本次新增/覆盖的全部目标路径。无对应 BackupEntry 的即为「纯新增」，回滚时直接删。</summary>
    public required IReadOnlyList<string> WrittenPaths { get; init; }
}
```

- [x] **Step 10: 写 `Domain/DeploymentProgress.cs`**

```csharp
namespace Deployer.Core.Domain;

/// <summary>
/// 执行期落盘的进度。每完成一步就重写一次，
/// 进程被强杀后 RollbackExecutor 据此接管（spec §5.5）。
/// </summary>
public sealed record DeploymentProgress
{
    public required Guid PlanId { get; init; }
    public required string ManifestPath { get; init; }
    public required IReadOnlyList<string> CompletedSteps { get; init; }

    public static DeploymentProgress Empty(Guid planId, string manifestPath) =>
        new() { PlanId = planId, ManifestPath = manifestPath, CompletedSteps = Array.Empty<string>() };
}

public sealed record RollbackResult(
    bool Success,
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<string> DeletedPaths,
    string? ErrorMessage);
```

- [x] **Step 11: 写 `Domain/InstallRecord.cs`**

```csharp
namespace Deployer.Core.Domain;

/// <summary>台账条目。ledger.json 只存这个索引，明细在各 PlanId 的 manifest.json 里。</summary>
/// <remarks>
/// 与 <see cref="DeploymentManifest"/> 同属跨版本长寿命持久化类型，适用同一条约定：
/// v1 发布之后新增字段必须可空或带默认值，禁止 <c>required</c>，
/// 否则旧 ledger.json 在新版工具下会反序列化失败。
/// </remarks>
public sealed record InstallRecord
{
    public required string Fingerprint { get; init; }
    public required string GameTitle { get; init; }
    public required string DeployDirectory { get; init; }
    public required Guid LatestPlanId { get; init; }
    public required string BackupRoot { get; init; }
    public required InstallState State { get; init; }
    public required string PayloadVersion { get; init; }
    public required string IdentityFileName { get; init; }
    public required DateTime InstalledUtc { get; init; }
}
```

- [x] **Step 12: 写 `DllIdentityMap` 的测试**

`tests/Deployer.Core.Tests/DllIdentityMapTests.cs`：
```csharp
using Deployer.Core.Domain;
using Xunit;

namespace Deployer.Core.Tests;

public class DllIdentityMapTests
{
    [Theory]
    [InlineData(DllIdentity.Dxgi, "dxgi.dll")]
    [InlineData(DllIdentity.Winmm, "winmm.dll")]
    [InlineData(DllIdentity.Version, "version.dll")]
    [InlineData(DllIdentity.Dbghelp, "dbghelp.dll")]
    [InlineData(DllIdentity.D3d12, "d3d12.dll")]
    [InlineData(DllIdentity.Wininet, "wininet.dll")]
    [InlineData(DllIdentity.Winhttp, "winhttp.dll")]
    [InlineData(DllIdentity.Asi, "OptiScaler.asi")]
    public void FileName_matches_optiscaler_convention(DllIdentity identity, string expected)
    {
        Assert.Equal(expected, identity.FileName());
    }

    [Fact]
    public void FileName_covers_every_enum_member()
    {
        foreach (var identity in Enum.GetValues<DllIdentity>())
        {
            Assert.False(string.IsNullOrWhiteSpace(identity.FileName()), $"{identity} 缺少文件名映射");
        }
    }
}
```

- [x] **Step 13: 跑测试**

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet test OptiScalerDeployer.sln
```
Expected: `失败: 0，通过: 9`

- [x] **Step 14: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add core domain records and enums"
```

---

## Task 3: 基础设施（异常、JSON、哈希、版本读取）

**Files:**
- Create: `src/Deployer.Core/Errors/DeployerException.cs`
- Create: `src/Deployer.Core/Serialization/DeployerJson.cs`
- Create: `src/Deployer.Core/Util/Hashing.cs`
- Create: `src/Deployer.Core/Abstractions/IFileVersionReader.cs`
- Create: `src/Deployer.Core/Abstractions/WindowsFileVersionReader.cs`
- Test: `tests/Deployer.Core.Tests/HashingTests.cs`
- Test: `tests/Deployer.Core.Tests/JsonAssert.cs`（测试辅助，非测试）
- Test: `tests/Deployer.Core.Tests/DeployerJsonTests.cs`
- Test: `tests/Deployer.Core.Tests/WindowsFileVersionReaderTests.cs`
- Test: `tests/Deployer.Core.Tests/DeployerExceptionTests.cs`

- [x] **Step 1: 写 `Errors/DeployerException.cs`**

每个异常带 `UserMessage`（给用户看的中文短句）与 `Diagnostic`（给日志的技术细节）—— spec §10 要求 UI 能渲染「出了什么问题 / 建议怎么做 / 详细日志」三段式。

```csharp
namespace Deployer.Core.Errors;

/// <summary>所有 Deployer 异常的基类。</summary>
public abstract class DeployerException : Exception
{
    protected DeployerException(string userMessage, string diagnostic, Exception? inner = null)
        : base(userMessage, inner)
    {
        UserMessage = userMessage;
        Diagnostic = diagnostic;
    }

    /// <summary>给用户看的一句中文话，不含路径细节以外的技术黑话。</summary>
    public string UserMessage { get; }

    /// <summary>给日志看的技术细节。</summary>
    public string Diagnostic { get; }

    /// <summary>
    /// <see cref="Exception.Message"/> 只带用户话术，所以这里把 <see cref="Diagnostic"/>
    /// 追加回来，保证日志与崩溃转储里技术细节不丢。
    /// </summary>
    public override string ToString() =>
        $"{base.ToString()}{Environment.NewLine}诊断：{Diagnostic}";
}

public sealed class PayloadNotFoundException : DeployerException
{
    public PayloadNotFoundException(string diagnostic)
        : base("找不到 OptiScaler 产物。请先用 VS2022 构建一次 OptiScaler.sln，或在设置里手动指定产物目录。",
               diagnostic) { }
}

/// <remarks>
/// 可写性在计划 A 里是**值**不是异常：<c>DirectoryWritability</c> 枚举 → <c>ElevationRequirement</c>
/// → <c>PlanWarning</c>，探测结果一路带到 UI。本类型只保留给执行阶段**真的**写失败
/// （已提权仍被拒、目录被独占等）。
/// </remarks>
public sealed class GameNotWritableException : DeployerException
{
    public GameNotWritableException(string path, string diagnostic)
        : base($"游戏目录不可写：{path}", diagnostic) => FilePath = path;

    public string FilePath { get; }
}

/// <remarks>
/// 计划 A/B **不会**抛本异常。「全部候选身份都冲突」是以值的形式流转的：
/// <c>DllIdentitySelector.Select</c> 返回 <c>IdentityStatus.RequiresUserDecision</c> 加一段
/// <c>UserPromptMessage</c>，planner 把它转成 Caution 级 <c>PlanWarning</c>，UI 再给用户三个选项。
/// 只有计划 C 需要表达「三个选项用户全拒了」时才考虑本类型，且要先确认不与上述值路径重复。
/// </remarks>
public sealed class IdentityConflictException : DeployerException
{
    public IdentityConflictException(string deployDirectory, string diagnostic)
        : base($"所有候选 DLL 身份都与 {deployDirectory} 里的游戏自带文件冲突，需要你决定如何处理。",
               diagnostic) { }
}

public sealed class ChecksumMismatchException : DeployerException
{
    public ChecksumMismatchException(string path, string expected, string actual)
        : base($"文件校验失败：{path}。可能是磁盘空间不足或杀毒软件拦截。",
               $"expected SHA256 {expected}, actual {actual}, path {path}")
    {
        FilePath = path;
        Expected = expected;
        Actual = actual;
    }

    public string FilePath { get; }
    public string Expected { get; }
    public string Actual { get; }
}

/// <remarks>
/// 计划 D 专用。**用户取消 UAC 时绝不能抛本异常** —— spec §10 的失败点表明写「UAC 被拒
/// **不算错误**」，那条路径要把结果记成 <c>ElevationRequirement.Declined</c>，并说明
/// 「该游戏在受保护的 WindowsApps 里」；抛异常会让 executor 报 <c>Success=false</c>，正是 spec 禁止的。
/// 本类型只留给提权**通道本身**失败的情形：runas 起不来、Worker 崩溃等。
/// </remarks>
public sealed class ElevationDeclinedException : DeployerException
{
    public ElevationDeclinedException(string diagnostic)
        : base("你取消了管理员授权。该游戏位于受系统保护的 WindowsApps 目录，不提权无法部署。",
               diagnostic) { }
}

/// <remarks>
/// 计划 D 专用，由 <c>IHealthCheckRunner</c> 抛出。<see cref="LogExcerpt"/> 带着
/// <c>OptiScaler.log</c> 的摘要，UI 把它显示在「一键回滚」按钮旁边（spec §10）。
/// </remarks>
public sealed class HealthCheckFailedException : DeployerException
{
    public HealthCheckFailedException(string logExcerpt)
        : base("OptiScaler 启动后报告了错误。日志摘要已保留，可一键回滚。", logExcerpt)
        => LogExcerpt = logExcerpt;

    public string LogExcerpt { get; }
}
```

构造函数参数**不能**用在属性初始化器里（`public string FilePath { get; } = path;` 会报 4 个 CS0103：参数在属性初始化器中不在作用域内）。要么用表达式构造体 `=> FilePath = path;`，要么用构造函数体 `{ FilePath = path; }`——上面两个类各用了一种，与同文件里 `GameNotWritableException` 的风格一致。

属性叫 `FilePath` 而不是 `Path`：`ImplicitUsings=enable` 把 `System.IO` 拉了进来，类内裸写 `Path` 会先绑定到**属性**，方法体里的 `Path.Combine(...)` 就编译不过了（Task 13 的 `DefaultPath` 也是为此才显式写 `System.IO.Path.Combine`）。这两个属性目前零消费者，是能免费改名的唯一窗口。

基类把 `userMessage` 交给 `base(...)`，于是 `Exception.Message` 带的是**人话**，泛型错误处理（`catch (Exception ex) => ... ex.Message`）默认就是对的：Task 15 的 CLI 顶层那句 `return Fail(ex.Message, ex)` 因此会把 spec §10 要求的两条出路（「用 VS2022 构建一次 OptiScaler.sln」/「手动指定产物目录」）摆到用户面前，而不是甩一串裸诊断。`Diagnostic` 仍然给日志用，并由 `ToString()` 追加回去；Task 15 的 `Fail` 会把它打到 stderr——CLI 就是日志，技术细节没有别的落点，丢了就再没人能复现现场。Task 12 executor 里的 `ex is DeployerException ? ex.Message : $"部署失败：{ex.Message}"` 不再需要 `de.UserMessage`（两者恒等），但三元式本身要保留：对外来异常还得加「部署失败：」这个前缀。

- [x] **Step 2: 写 `Serialization/DeployerJson.cs`**

plan / manifest / progress / ledger 四种文件都要跨进程、跨会话读写，序列化选项必须只有一份。

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deployer.Core.Serialization;

public static class DeployerJson
{
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // 这些文件就是要给人打开来读的，默认 encoder 会把所有中文转成 \uXXXX；
        // 输出只落 .json 文件、从不内联进 HTML，所以放宽转义不带来安全损失。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 冻结成只读：Options 是五种落盘文件共用的单例，{ get; } 只防重新赋值，对象本身仍可改，
    // 一句杂散的 WriteIndented = false 就会静默重排所有文件（而且 JsonAssert 两边一起变，测试照样绿）。
    // MakeReadOnly() 返回 void，串不进初始化表达式，只能放在静态构造里；必须传
    // populateMissingResolver: true，否则无参重载会因未指定 TypeInfoResolver 而抛异常。
    // 附带好处：只读 Options 能走 JsonSerializer 的缓存快路径。
    static DeployerJson() => Options.MakeReadOnly(populateMissingResolver: true);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"无法反序列化 {typeof(T).Name}：内容为 null。");
}
```

`JsonStringEnumConverter` 是必须的：plan 与 manifest 会被人打开来看，`"Chosen": 1` 毫无诊断价值，`"Chosen": "winmm"` 一眼就懂。

`Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 是**可读性**修复，不是正确性修复。默认的 `JavaScriptEncoder.Default` 会把所有非 ASCII 转成 `\uXXXX`，而这几份文件里的人话内容几乎全是中文：`IdentityDecision.UserPromptMessage`（一整段解释 OptiScaler 的代理转发到 System32 而不是游戏目录内那份 DLL）、`UpscalerProfile.DisplayName`/`Description`、`PlanWarning.Message`、`GameTitle`，分别落在 `manifest.json`、`plan.json`、`profiles.json`、`ledger.json` 里。上一段刚用 `WriteIndented` + 字符串枚举论证过「这些文件是给人打开来读的」，转义正好把这个理由打掉。`\u5149` 与 `光` 反序列化完全等价，所以这不是 bug；放宽也安全，因为输出只落 `.json` 文件、从不内联进 HTML。今天 CI 看不见它，只是因为还没有测试把中文字符串推过一遍 JSON（Step 7 补上了）。

`MakeReadOnly` 把这个单例冻住：`{ get; }` 只防重新赋值，对象本身仍是可变的，一句杂散的 `DeployerJson.Options.WriteIndented = false;` 就会静默重排五种落盘文件的格式——而 `JsonAssert.Equivalent` 两边用的是同一份 `Options`，两边一起变，测试照样绿。.NET 8 的两个坑记在这里：`MakeReadOnly()` 返回 `void`，串不进属性初始化表达式；无参重载还要求先指定 `TypeInfoResolver`，否则直接抛 `InvalidOperationException`，所以要传 `populateMissingResolver: true`。

- [x] **Step 3: 写 `Util/Hashing.cs`**

```csharp
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Deployer.Core.Util;

public static class Hashing
{
    private const int HexLength = 64;

    /// <summary>返回小写十六进制 SHA256。经 IFileSystem 读，因此 MockFileSystem 上照样能算。</summary>
    public static string Sha256OfFile(IFileSystem fs, string path)
    {
        using var stream = fs.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256OfBytes(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256OfString(string text) =>
        Sha256OfBytes(Encoding.UTF8.GetBytes(text));

    /// <summary>取 SHA256 前 <paramref name="length"/> 位十六进制小写，用于短指纹。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 不在 [1, 64] 区间内。</exception>
    public static string ShortHash(string text, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, HexLength);
        return Sha256OfString(text)[..length];
    }
}
```

`Sha256OfFile` 接 `IFileSystem` 而不是直接用 `System.IO.File`：否则在 `MockFileSystem` 上会去真磁盘找文件、直接抛 `FileNotFoundException`，所有依赖哈希的测试都跑不了。多传一个参数比注入 `Func<string, string>` 函数指针更好读，也不会遗漏。

- [x] **Step 4: 写 `Abstractions/IFileVersionReader.cs` 与 `WindowsFileVersionReader.cs`**

`FileVersionInfo` 无法作用于 `MockFileSystem`，因此必须抽接口，测试注入假实现。

```csharp
namespace Deployer.Core.Abstractions;

/// <summary>读取 PE 版本资源。身份冲突判定依赖 OriginalFilename。</summary>
public interface IFileVersionReader
{
    /// <summary>返回版本资源里的 OriginalFilename；无版本资源或读取失败返回 null。</summary>
    string? GetOriginalFilename(string path);

    /// <summary>返回 FileVersion（形如 "0.9.1.0"）；无版本资源返回 null。</summary>
    string? GetFileVersion(string path);
}
```

```csharp
using System.Diagnostics;

namespace Deployer.Core.Abstractions;

public sealed class WindowsFileVersionReader : IFileVersionReader
{
    public string? GetOriginalFilename(string path) => Read(path)?.OriginalFilename;

    public string? GetFileVersion(string path) => Read(path)?.FileVersion;

    private static FileVersionInfo? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            // FileVersionInfo 对无版本资源的文件返回全 null 字段的对象，而不是抛异常
            return string.IsNullOrEmpty(info.OriginalFilename) && string.IsNullOrEmpty(info.FileVersion)
                ? null
                : info;
        }
        catch (Exception)
        {
            // 32/64 位不符、文件被占用、非 PE 文件 —— 一律视为「读不出版本信息」
            return null;
        }
    }
}
```

`catch (Exception)` 在此处是有意的：这个方法的契约就是「读不出来返回 null」，调用方（身份选择器）会把 null 当作「这是游戏自带文件」处理，即走**保守路径**。吞异常在这里是安全的默认，不是偷懒。

- [x] **Step 5: 写 `HashingTests.cs`**

```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class HashingTests
{
    [Fact]
    public void Sha256OfString_is_lowercase_hex_64_chars()
    {
        var hash = Hashing.Sha256OfString("OptiScaler");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    [Fact]
    public void Sha256OfString_is_stable_across_calls()
    {
        Assert.Equal(Hashing.Sha256OfString("abc"), Hashing.Sha256OfString("abc"));
    }

    [Fact]
    public void ShortHash_returns_requested_prefix_length()
    {
        var short8 = Hashing.ShortHash("xbox|c:\\xboxgames\\game|game.exe", 8);

        Assert.Equal(8, short8.Length);
        Assert.Equal(Hashing.Sha256OfString("xbox|c:\\xboxgames\\game|game.exe")[..8], short8);
    }

    [Fact]
    public void Sha256OfFile_matches_bytes_hash()
    {
        var fs = new MockFileSystem();
        const string path = @"C:\tmp\hash-me.bin";
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        fs.AddFile(path, new MockFileData(bytes));

        Assert.Equal(Hashing.Sha256OfBytes(bytes), Hashing.Sha256OfFile(fs, path));
    }

    // FIPS 180-2 的已知答案，一句断言同时钉住「算法 + UTF-8 编码 + 小写十六进制」三件事。
    // 之所以不能漂：GameTarget.Fingerprint 由 Sha256OfString 推出，并作为影子备份的目录名
    // 与台账索引键落盘；换掉编码会让所有既有备份静默失联 —— 用户看到「未安装」，
    // 而备份其实躺在另一个指纹底下。
    [Fact]
    public void Sha256OfString_matches_the_fips_known_answer()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                     Hashing.Sha256OfString("abc"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void ShortHash_rejects_length_outside_one_to_sixty_four(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Hashing.ShortHash("abc", length));
    }

    [Theory]
    [InlineData(1, "b")]
    [InlineData(64, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void ShortHash_accepts_the_boundary_lengths(int length, string expected)
    {
        Assert.Equal(expected, Hashing.ShortHash("abc", length));
    }
}
```

- [x] **Step 6: 跑测试，确认失败**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: **FAIL** —— `HashingTests` 编译不过（`Hashing` 尚未存在）。若 Step 3 已写完则会直接通过；此时改为确认 `通过: 19`。

- [x] **Step 7: 写 `JsonAssert.cs` 与 `DeployerJsonTests.cs`**

先写辅助类。它解决一个会反复出现的坑：**含 `IReadOnlyList<T>` 成员的 record 不能用 `Assert.Equal` 直接比。**

`tests/Deployer.Core.Tests/JsonAssert.cs`：
```csharp
using Deployer.Core.Serialization;
using Xunit;

namespace Deployer.Core.Tests;

/// <summary>
/// 含 <see cref="System.Collections.Generic.IReadOnlyList{T}"/> 成员的 record 不能用
/// <c>Assert.Equal</c> 直接比较，改比序列化结果。
/// </summary>
/// <remarks>
/// 原因有两层：
/// ① record 合成的 <c>Equals</c> 对每个字段走 <c>EqualityComparer&lt;TField&gt;.Default</c>，
///    而 <c>IReadOnlyList&lt;T&gt;</c> 不实现 <c>IEquatable&lt;IReadOnlyList&lt;T&gt;&gt;</c>，
///    退化为引用相等——数组与 <c>List&lt;T&gt;</c> 是两个实例，永远不等。
/// ② xUnit 的 <c>AssertEqualityComparer&lt;T&gt;</c> 会**先**检查 <c>IEquatable&lt;T&gt;</c>（record 都实现），
///    所以直接委派给 record 自己的 <c>Equals</c>，根本走不到 xUnit 的集合结构化比较分支。
/// 失败时 xUnit 会把所有标量字段展成相等、只有集合的运行时类型不同，极易误判为「元素不等」。
///
/// 改成比序列化结果反而更贴合这些断言真正要守的性质：plan 与 manifest 本来就要
/// 以 JSON 形态跳进程（主进程 plan → 提权 Worker）与落盘（manifest.json）。
///
/// 注意失效方向：将来若有人对这类 record 直接写 <c>Assert.Equal(a, b)</c>，测试会**变红**，
/// 而不是静默通过。这是故意选的：相比手写 <c>Equals</c>/<c>GetHashCode</c> 样板（漏同步一个字段
/// 就是静默假绿），响亮的失败安全得多。红的修法就是改用本辅助类。
/// 不含集合成员的 record（如 <see cref="Deployer.Core.Domain.GameTarget"/>）record 相等本来就对，
/// 继续用 <c>Assert.Equal</c> 即可。
/// </remarks>
public static class JsonAssert
{
    public static void Equivalent<T>(T expected, T actual) =>
        Assert.Equal(DeployerJson.Serialize(expected), DeployerJson.Serialize(actual));
}
```

再写测试。它守的是「plan 能被序列化后原样读回来」——计划 D 的提权 Worker 完全依赖这一点。

`tests/Deployer.Core.Tests/DeployerJsonTests.cs`：

```csharp
using Deployer.Core.Domain;
using Deployer.Core.Serialization;
using Xunit;

namespace Deployer.Core.Tests;

public class DeployerJsonTests
{
    [Fact]
    public void Enums_serialize_as_readable_strings()
    {
        var json = DeployerJson.Serialize(DllIdentity.Winmm);

        Assert.Equal("\"winmm\"", json);
    }

    [Fact]
    public void GameTarget_roundtrips()
    {
        var target = new GameTarget(
            "Halo Infinite", GamePlatform.Xbox, "Microsoft.25438_8wekyb3d8bbwe!App", "App",
            @"C:\XboxGames\Halo Infinite\content", @"C:\XboxGames\Halo Infinite\content\content\Binaries\Win64",
            "HaloInfinite-WinGDK-Shipping.exe", "a1b2c3d4");

        var restored = DeployerJson.Deserialize<GameTarget>(DeployerJson.Serialize(target));

        Assert.Equal(target, restored);
    }

    [Fact]
    public void Manifest_roundtrips()
    {
        var manifest = new DeploymentManifest
        {
            PlanId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            CreatedUtc = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            Fingerprint = "a1b2c3d4",
            GameTitle = "Halo Infinite",
            DeployDirectory = @"C:\XboxGames\Halo Infinite\content",
            Payload = new PayloadInfo(PayloadOrigin.LocalBuild, "0.9.1.0", @"D:\repo\x64\Release\a"),
            Identity = new IdentityDecision(
                DllIdentity.Winmm, "winmm.dll", IdentityStatus.Resolved,
                new[] { DllIdentity.Dxgi }, null),
            Profile = new UpscalerProfile
            {
                Id = "amd-rdna4", DisplayName = "AMD RDNA4", Description = "RX 9070 XT",
                IsBuiltin = true,
                Edits = new[] { new IniEdit("Upscalers", "Dx12Upscaler", null, "ffx") },
            },
            Backups = new[] { new BackupEntry(@"C:\game\winmm.dll", @"C:\backup\winmm.dll", BackupReason.OverwriteGameFile) },
            WrittenPaths = new[] { @"C:\game\winmm.dll", @"C:\game\OptiScaler.ini" },
        };

        var restored = DeployerJson.Deserialize<DeploymentManifest>(DeployerJson.Serialize(manifest));

        JsonAssert.Equivalent(manifest, restored);
    }

    [Fact]
    public void Chinese_text_is_written_readable_not_escaped()
    {
        var json = DeployerJson.Serialize(new PlanWarning(WarningSeverity.Caution, "身份 DLL 全部冲突"));

        // 原文出现，就已经独立且精确地证明了中文没被转义成 \uXXXX。
        // 别再补「不含某转义前缀」那类断言：含 Windows 路径的载荷会把反斜杠转义成
        // 两个字符，紧跟 u 的路径段会伪造出同样子串 → 假失败。
        Assert.Contains("身份 DLL 全部冲突", json);
    }

    // 这些名字是 manifest.json 与 ledger.json 的**落盘契约**：改个成员名或改拼写，
    // 旧 manifest 就反序列化不出来；BackupStore.ReadJson 会带着内层异常抛 InvalidDataException，
    // RollbackExecutor 接着报「无法读取备份清单……原因：……」—— 一次纯重构就这样永久地
    // 毁掉用户的回滚能力。响是响了，但那份备份已经回不来了。
    // 真要改，必须同时给 SchemaVersion 加兼容分支，并有意识地更新本测试。
    [Theory]
    [InlineData(typeof(PayloadOrigin), "localBuild", "gitHubRelease", "manualDirectory")]
    [InlineData(typeof(DllIdentity), "dxgi", "winmm", "version", "dbghelp", "d3d12", "wininet", "winhttp", "asi")]
    [InlineData(typeof(IdentityStatus), "resolved", "requiresUserDecision", "noCandidateAvailable")]
    [InlineData(typeof(BackupReason), "overwriteGameFile", "overwritePreviousOpti", "iniModified")]
    [InlineData(typeof(InstallState), "installed", "healthy", "rolledBack", "needsReinstall", "failed")]
    public void Persisted_enum_wire_names_are_pinned(Type enumType, params string[] expectedWireNames)
    {
        var actualWireNames = Enum.GetValues(enumType)
            .Cast<object>()
            .Select(value => DeployerJson.Serialize(value).Trim('"'))
            .ToArray();

        Assert.Equal(expectedWireNames, actualWireNames);
    }
}
```

注意这里用的是 `JsonAssert.Equivalent` 而不是 `Assert.Equal`：`DeploymentManifest` 含四个集合成员（`Identity.SkippedDueToConflict`、`Profile.Edits`、`Backups`、`WrittenPaths`），直接用 `Assert.Equal` 会因引用相等而失败。上一项 `GameTarget_roundtrips` 用 `Assert.Equal` 是对的，因为 `GameTarget` 全是 string 与 enum，没有集合成员。

两个新测试分别钉住 Step 2 的两项保证：`Chinese_text_is_written_readable_not_escaped` 是 `UnsafeRelaxedJsonEscaping` 的唯一看守人（没有它，谁把 `Encoder` 删了测试也照样绿）；`Persisted_enum_wire_names_are_pinned` 钉住落盘枚举名。后者的预期值是**实测**出来的，不是拍脑袋：`JsonNamingPolicy.CamelCase` 对开头连续大写的降位方式有它自己的规矩（`GitHubRelease` → `gitHubRelease`，而不是 `githubRelease`）。将来对不上时，先确认 `DeployerJson` 真实产出的是什么，再去改**预期值**；绝不得把断言改宽。

`DeployerJson.Serialize(value)` 在 `value` 静态类型为 `object` 时仍按**运行时类型**序列化，所以箱起来的枚举能正常走到 `JsonStringEnumConverter`（已实测，与 `JsonSerializer.Serialize(value, enumType, Options)` 逐字节一致）。若哪天不再成立，改用后者那个显式运行时类型的调用，**不要**为测试往 `DeployerJson` 里加新方法。

- [x] **Step 7b: 写 `WindowsFileVersionReaderTests.cs`**

`FileVersionInfo` 走的是 Win32 版本资源 API，作用不到 `MockFileSystem` 上——这正是 Step 4 抽 `IFileVersionReader` 的理由，也是本文件成为需要真实文件系统的边界测试的原因。只测否定路径：仓库里没有 PE 二进制夹具，正向路径无从造起；而否定路径是确定性的，也恰好就是 Task 7 保守分支依赖的契约（「读不出来 → 返回 null，绝不抛」→ 该槽位被当作游戏自带文件，宁可收进影子备份也不静默覆盖）。

```csharp
using Deployer.Core.Abstractions;
using Xunit;

namespace Deployer.Core.Tests;

// 本文件是整套测试里唯一碰真文件系统的，这是正确且无法回避的：FileVersionInfo 走的是
// Win32 版本资源 API，作用不到 MockFileSystem 上 —— 这正是 IFileVersionReader 存在的理由。
// 只测否定路径（仓库里没有 PE 二进制夹具，正向路径无从造起）。否定路径是确定性的，
// 也恰好就是任务 7 保守分支依赖的契约：「读不出来就返回 null、绝不抛」→ 该槽位被当作
// 游戏自带文件，宁可收进影子备份，也不静默覆盖。
public class WindowsFileVersionReaderTests
{
    private readonly WindowsFileVersionReader reader = new();

    [Fact]
    public void Missing_file_returns_null_instead_of_throwing()
    {
        // 目录本身也不存在，因此 File.Exists 必为 false
        var missing = Path.Combine(Path.GetTempPath(), "opti-deployer-tests-no-such-dir", "no-such-file.dll");

        Assert.Null(reader.GetOriginalFilename(missing));
        Assert.Null(reader.GetFileVersion(missing));
    }

    [Fact]
    public void Non_PE_file_returns_null_instead_of_throwing()
    {
        var notAPe = Path.Combine(Path.GetTempPath(), $"opti-deployer-tests-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(notAPe, "这不是 PE 文件，只是一段普通文本。");

            Assert.Null(reader.GetOriginalFilename(notAPe));
            Assert.Null(reader.GetFileVersion(notAPe));
        }
        finally
        {
            // 断言失败也要清干净，别在 temp 里留垃圾
            File.Delete(notAPe);
        }
    }
}
```

`finally` 里的 `File.Delete` 是必须的：断言一失败就会抛出，没 `finally` 就会在用户的 temp 目录里留下孤儿文件。临时文件名带 `Guid.NewGuid()`，因此并行跑测试也不会互撞。

- [x] **Step 7c: 写 `DeployerExceptionTests.cs`**

Step 1 把 `userMessage` 交给 `base(...)`、并让 `ToString()` 把 `Diagnostic` 追加回来，这两条是**基类契约**，被全部 6 个具体异常继承。但直到本 Step 之前它们**零测试覆盖**：把 `: base(userMessage, inner)` 改回 `: base(diagnostic, inner)`，前面 30 个测试全绿（已由两位复核者独立实证）。这不是纯理论风险——Task 15 的 CLI 顶层就是 `return Fail(ex.Message)`，`Message` 一旦退化成裸诊断串，spec §10 承诺的两条出路（「用 VS2022 构建一次 OptiScaler.sln」/「在设置里手动指定产物目录」）就再也到不了用户眼前。

基类契约在基类测一次杠杆最高，所以拿构造最简单的 `PayloadNotFoundException` 当被测对象：它的 `UserMessage` 恰好就是 spec §10 那两条出路。

```csharp
using Deployer.Core.Errors;
using Xunit;

namespace Deployer.Core.Tests;

/// <remarks>
/// 本文件钉住 <c>DeployerException</c> **基类**的两条契约，而不是某个具体异常的语义：
/// ① <c>base(userMessage, inner)</c> 让 <c>Exception.Message</c> 承载用户话术；
/// ② <c>ToString()</c> 把 <c>Diagnostic</c> 追加回来，日志与崩溃转储里技术细节不丢。
///
/// 为什么值得单独一个文件：这两条被全部 6 个具体异常继承，在基类测一次杠杆最高。
/// 而在补上本测试之前它们的覆盖面是**零**——把 <c>base(userMessage, inner)</c> 改回
/// <c>base(diagnostic, inner)</c>，原有 30 个测试全绿（已由两位复核者独立实证）。
/// 这不是理论风险：Task 15 的 CLI 顶层就是 <c>Fail(ex.Message)</c>，一旦 <c>Message</c>
/// 退化成裸诊断串，用户就看不到 spec §10 承诺的那两条出路。
///
/// **护栏**：把基类的 <c>: base(userMessage, inner)</c> 改成 <c>: base(diagnostic, inner)</c>，
/// 或把 <c>ToString()</c> 简化成 <c>base.ToString()</c>，本测试会**变红**。
/// 觉得它多余想删之前，请先亲手做一次上述变异。
/// </remarks>
public class DeployerExceptionTests
{
    // 一眼可辨的假诊断串：必须与用户话术毫不重叠，才能证明两者确实分流了
    private const string FakeDiagnostic = "payloadRoot=D:\\repo\\x64\\Release\\a not found";

    [Fact]
    public void Message_carries_the_user_guidance_not_the_diagnostic()
    {
        var ex = new PayloadNotFoundException(FakeDiagnostic);

        // 这一条正好杀死 base(diagnostic, inner) 那个变异体
        Assert.Equal(ex.UserMessage, ex.Message);

        // 用户话术还得是**可操作**的指引：告诉用户怎么修，而不是甩一串技术黑话
        Assert.Contains("OptiScaler.sln", ex.Message);
        Assert.DoesNotContain(FakeDiagnostic, ex.Message);
    }

    [Fact]
    public void ToString_appends_the_diagnostic()
    {
        var ex = new PayloadNotFoundException(FakeDiagnostic);

        Assert.Contains(ex.Diagnostic, ex.ToString());
    }

    [Fact]
    public void InnerException_is_null()
    {
        // 现有 6 个具体异常都不传 inner；基类的 inner 参数留给 Task 10/11 的包装场景
        var ex = new PayloadNotFoundException(FakeDiagnostic);

        Assert.Null(ex.InnerException);
    }
}
```

三个 `[Fact]` 各钉一件事，分开写是为了变异时能一眼看出是哪条契约碎了：`Assert.Equal(ex.UserMessage, ex.Message)` 杀死上述变异体；`Assert.Contains(ex.Diagnostic, ex.ToString())` 钉住诊断被追加回来；`Assert.Contains("OptiScaler.sln", ...)` 加 `Assert.DoesNotContain(FakeDiagnostic, ...)` 钉住「用户话术是可操作指引、不是裸诊断」。假诊断串故意带 Windows 路径形状，与用户话术零重叠。

基类的 `inner` 参数目前**没有任何具体异常传值**（6 个都不传）。不要为了测它去改产品代码硬造一个带 inner 的异常；只断言 `Assert.Null(ex.InnerException)` 把现状钉住，那个参数是留给 Task 10/11 包装场景的。

- [x] **Step 8: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 33`

若 `Manifest_roundtrips` 失败，先看 xUnit 的差异输出：如果所有标量字段都相等、只有集合的**运行时类型**不同（`T[]` vs `List<T>`），那不是元素不等，而是你把它写成了 `Assert.Equal`——record 对 `IReadOnlyList<T>` 字段是引用相等（详见 Step 7 的 `JsonAssert` 注释），改用 `JsonAssert.Equivalent` 即可。只有在元素**值**真的不同时，才去查 `IniEdit` / `BackupEntry` 是不是漏了 `record` 关键字（被写成了 `class`）。

- [x] **Step 9: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add exceptions, json options, hashing and version-info abstraction"
```

---

## Task 4: IniPatcher —— 保留注释的部分改写

Spec §8.2 的核心约束：**不重新序列化**。`OptiScaler.ini` 1571 行，一半价值在注释里；用 SimpleIni / IniParser 会把注释全丢，后续排查极痛苦。

已核实的文件事实（决定实现细节；下面每个数字都用脚本对真文件实测过）：
- 行尾是 **CRLF**（`crlf=1571 bareLf=0`，零混合行尾）、UTF-8 **无 BOM**
- **864 / 1571 = 55% 是注释行**。这就是「不重新序列化」从「比较好」变成「硬约束」的原因
- **零行内注释**：所有 `;` 注释都独占一行。因此「替换 `=` 右侧」不会误删注释。**这条事实只对当前这份仓库根的 `OptiScaler.ini` 成立**：被 patch 的 ini 来自 `Payload.RootDirectory`，而 `PayloadOrigin` 枚举含 `ManualDirectory`（用户可以指定任意本地目录作为 payload），换一份 ini 不保证仍然零行内注释——参见下面「行尾注释会被一并替换掉」那条已知限制
- 键的写法**绝大多数**是 `Key=value`（等号两侧无空格），但有 3 个例外：`[NvngxFG]` 段的 `DispatchFlags` / `ShowDebug` / `DisableHudless` 写作 `Key = auto`（实测 L721 / L726 / L731）。所以「只换 `=` 右侧、左半部分原样保留」这条路径在真文件里**确实会走到**，不是纯理论构造
- **同一 section 内零重复键**（实测）。所以「命中即 `pending.Remove`」不会漏改；若将来 OptiScaler 上游引入了重复键，本实现只改**第一次出现**的那个，而 OptiScaler 自己的 SimpleIni 可能读到最后一个——那种不一致会表现为「改了但没生效」，需重新评估
- `Enabled=auto` 这个键名在 **5 个不同的 section** 里各出现一次：`[FrameGen]` L25、`[DLSS]` L664、`[OutputScaling]` L1154、`[CAS]` L1172、`[Magnifier]` L1234（全文 37 个 section，section 名零重复）—— **section 作用域匹配是硬性要求，不是优化**：不分作用域就会一次改掉五个不相干的功能开关

**Files:**
- Create: `src/Deployer.Core/Ini/IIniPatcher.cs`
- Create: `src/Deployer.Core/Ini/IniPatcher.cs`
- Test: `tests/Deployer.Core.Tests/IniPatcherTests.cs`

- [x] **Step 1: 写 `Ini/IIniPatcher.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Ini;

public sealed record IniPatchResult(
    string Content,
    IReadOnlyList<IniEdit> AppliedEdits,
    IReadOnlyList<IniEdit> AppendedEdits,
    string DetectedNewLine);

public interface IIniPatcher
{
    /// <summary>
    /// 对 <paramref name="originalContent"/> 应用 edits，返回改写后的全文。
    /// 保证：所有以 ; 或 # 开头的行原样保留；输出行数 >= 输入行数。
    /// </summary>
    IniPatchResult Patch(string originalContent, IReadOnlyList<IniEdit> edits);
}
```

`AppliedEdits`（命中原有键、就地替换）与 `AppendedEdits`（原文件没这个键、追加到 section 末尾）分开返回，因为 UI 预演要把两者用不同措辞展示：前者是「改动」，后者是「新增」。两者的 `OldValue` 都已回填（追加项的 `OldValue` 为 `null`）。

- [x] **Step 2: 写 `Ini/IniPatcher.cs` 的失败测试**

`tests/Deployer.Core.Tests/IniPatcherTests.cs`：
```csharp
using Deployer.Core.Domain;
using Deployer.Core.Ini;
using Xunit;

namespace Deployer.Core.Tests;

public class IniPatcherTests
{
    private const string Sample =
        "; OptiScaler config\r\n" +
        "[Upscalers]\r\n" +
        "; fsr21, fsr22, ffx, dlss\r\n" +
        "Dx12Upscaler=auto\r\n" +
        "\r\n" +
        "[FrameGen]\r\n" +
        "Enabled=auto\r\n" +
        "\r\n" +
        "[Log]\r\n" +
        "LogToFile=auto\r\n" +
        "LogLevel=auto\r\n";

    private static IniEdit Edit(string section, string key, string value) =>
        new(section, key, null, value);

    /// <summary>以 ; 或 # 开头的行（允许前导空白）。</summary>
    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(';') || trimmed.StartsWith('#');
    }

    /// <summary>断言 <paramref name="input"/> 的每一行都按原顺序出现在 <paramref name="output"/> 里。</summary>
    private static void AssertIsSupersequence(string[] input, string[] output)
    {
        var i = 0;
        foreach (var line in output)
        {
            if (i < input.Length && input[i] == line)
            {
                i++;
            }
        }

        Assert.Equal(input.Length, i);
    }

    [Fact]
    public void Replaces_value_in_place_and_preserves_comment_lines()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Contains("Dx12Upscaler=ffx", result.Content);
        Assert.Contains("; fsr21, fsr22, ffx, dlss", result.Content);
        Assert.Contains("; OptiScaler config", result.Content);
        Assert.DoesNotContain("Dx12Upscaler=auto", result.Content);
        Assert.Single(result.AppliedEdits);
        Assert.Equal("auto", result.AppliedEdits[0].OldValue);
        Assert.Empty(result.AppendedEdits);
    }

    [Fact]
    public void Output_preserves_every_input_line_in_order()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("Log", "LogToFile", "true"),
            Edit("Spoofing", "Dxgi", "false"),   // section 不存在
            Edit("Upscalers", "BrandNewKey", "1"), // key 不存在
        });

        var outputLines = result.Content.Split(result.DetectedNewLine);

        // 只断言「输出行数 >= 输入行数」是恒真式：实现从不删行，那个断言唯一能捕获的退化
        // 是「有人开始删行」，而它捕不到「行放错了位置」（行数完全正确）。超序列断言同时
        // 覆盖删行、改行、重排三类退化——输入的每一行都必须按原顺序出现在输出里。
        // 注意必须在 split 之后的行数组上比，比原文会被行尾归一化误报。
        //
        // 上面两条就地命中的 edit 是**契约允许**的改行（「只替换 = 右侧，左半部分原样保留」），
        // 所以超序列要比的是「已应用就地替换后的输入行」。直接拿原始 Sample 的行去比是恒假的：
        // Dx12Upscaler=auto 本来就不应该出现在输出里。这里把预期改动显式写成映射，
        // 于是断言仍然要求：左侧原样保留、其余每一行逐字原样且顺序不变。
        //
        // 与 All_comment_lines_survive_verbatim 是**互补**的、不可互相替代：超序列断言天然容忍重复，
        // 「把每条注释行复制一份」这类变异它**抓不到**（要靠后者的有序带重数比对）；反过来后者只看注释行，
        // 删掉一个非注释行它不红，那要靠这条超序列断言。别以为其中一条冗余而删掉它。
        var survivingInputLines = Sample
            .Split("\r\n")
            .Select(l => l switch
            {
                "Dx12Upscaler=auto" => "Dx12Upscaler=ffx",
                "LogToFile=auto" => "LogToFile=true",
                _ => l,
            })
            .ToArray();

        AssertIsSupersequence(survivingInputLines, outputLines);
    }

    [Fact]
    public void All_comment_lines_survive_verbatim()
    {
        var patcher = new IniPatcher();
        var originalComments = Sample.Split("\r\n").Where(IsComment).ToArray();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("FrameGen", "Enabled", "true"),
            Edit("Log", "LogLevel", "2"),
        });

        var outputComments = result.Content
            .Split(result.DetectedNewLine)
            .Where(l => IsComment(l) && l != "; added by OptiScaler Deployer")
            .ToArray();

        // 有序 + 带重数的比对。旧写法 Assert.Contains(comment, outputLines) 是**集合包含**：
        // 真 OptiScaler.ini 里 `; ----` 横幅重复了几十次（L1/L3/L19/L21 完全相同），
        // 删掉其中三条只要还剩一条相同的就照样绿。排掉 AddedByDeployer 是因为它是新增的注释。
        // 与 Output_preserves_every_input_line_in_order 是**互补**的：本条只看注释行、且要求有序带重数，
        // 是唯一能抓到「把每条注释行复制一份」这类变异的一条（超序列断言天然容忍重复，抓不到它）；
        // 反过来删掉一个非注释行只有那条超序列断言能抓到。别以为其中一条冗余而删掉它。
        Assert.Equal(originalComments, outputComments);
    }

    [Fact]
    public void Same_key_name_in_different_sections_is_scoped_correctly()
    {
        const string ini =
            "[FrameGen]\r\nEnabled=auto\r\n\r\n[OptiFG]\r\nEnabled=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("FrameGen", "Enabled", "true") });

        Assert.Equal("[FrameGen]\r\nEnabled=true\r\n\r\n[OptiFG]\r\nEnabled=auto\r\n", result.Content);
    }

    [Fact]
    public void Missing_key_is_appended_to_end_of_matching_section()
    {
        var patcher = new IniPatcher();

        // OldValue 故意传一个非 null 的脏值：追加项的 OldValue 必须由 patcher 清成 null，
        // 否则拿上一轮 patch 过的 edits 再 patch 一次时，UI 会渲染出一个根本不存在的
        // 「旧值 → 新值」diff。用 Edit() helper（它传 null）测不出这件事——那测的是 helper。
        var stale = new IniEdit("Log", "SingleFile", "stale-from-a-previous-run", "true");

        var result = patcher.Patch(Sample, new[] { stale });

        // 精确全文断言：[Log] 是 Sample 的最后一个 section，追加必须落在 LogLevel 之后、
        // 也就是文件末尾。只断言「在 [Log] 之后」区分不出「落在段末」与「落在下一个 section 里」。
        Assert.Equal(Sample + "SingleFile=true\r\n", result.Content);
        Assert.Single(result.AppendedEdits);
        Assert.Null(result.AppendedEdits[0].OldValue);
    }

    [Fact]
    public void Missing_section_is_created_at_end_of_file_with_source_comment()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[] { Edit("Spoofing", "Dxgi", "false") });

        Assert.EndsWith("[Spoofing]\r\nDxgi=false\r\n", result.Content);
        Assert.Contains("; added by OptiScaler Deployer", result.Content);
        Assert.Single(result.AppendedEdits);
    }

    [Fact]
    public void Several_keys_appended_to_one_section_keep_their_original_order()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Zzz", "3"),
            Edit("Upscalers", "Aaa", "1"),
            Edit("Upscalers", "Mmm", "2"),
        });

        var body = result.Content.Split(result.DetectedNewLine);
        var iAaa = Array.IndexOf(body, "Aaa=1");
        var iMmm = Array.IndexOf(body, "Mmm=2");
        var iZzz = Array.IndexOf(body, "Zzz=3");

        Assert.True(iZzz >= 0 && iZzz < iAaa && iAaa < iMmm,
            $"追加顺序应为 Zzz、Aaa、Mmm（实际下标 {iZzz}, {iAaa}, {iMmm}）");
    }

    [Fact]
    public void Appended_key_lands_inside_its_section_not_after_the_blank_separator()
    {
        const string ini = "[Upscalers]\r\nDx12Upscaler=auto\r\n\r\n[Log]\r\nLogToFile=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "NewKey", "1") });

        Assert.Equal("[Upscalers]\r\nDx12Upscaler=auto\r\nNewKey=1\r\n\r\n[Log]\r\nLogToFile=auto\r\n",
            result.Content);
    }

    [Fact]
    public void Appends_into_two_different_existing_sections_both_land_at_their_own_section_end()
    {
        // 这是 Critical 缺陷的回归看守：插入使**后半部分所有 section** 的锚点过期，
        // 只更新当前 section 会让后一个 section 的新键落到它的 section 头之前，
        // 也就是写进上一个 section。既有的追加测试全都只碰一个 section，盖不到。
        const string ini = "[A]\r\nA1=1\r\n\r\n[B]\r\nB1=1\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[]
        {
            Edit("A", "X1", "1"),
            Edit("A", "X2", "2"),
            Edit("B", "Y", "9"),
        });

        Assert.Equal("[A]\r\nA1=1\r\nX1=1\r\nX2=2\r\n\r\n[B]\r\nB1=1\r\nY=9\r\n", result.Content);
    }

    [Fact]
    public void Appends_shift_anchors_of_empty_sections_too()
    {
        // 钉住前移循环的比较符边界：`>= anchorLine + 1` 写成 `> anchorLine + 1` 的话，
        // 当**别的** section 的锚点值恰好等于 anchorLine + 1 时那个锚点会被漏掉前移。
        // 这种撞值只在两个 section 头**紧邻**时发生（中间的 [B] 是个没有任何键行的空段，
        // 它的锚点就是它自己那一行头）。上面那条两 section 看守的 fixture 里每段都有键行，
        // 锚点值不可能撞上这个边界，所以盖不到——而漏掉前移的后果与刚修掉的那个 Critical
        // 完全同型：Y=9 会落进 [A] 段，静默。
        const string ini = "[A]\r\nA1=1\r\n[B]\r\n[C]\r\nJ=1\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[]
        {
            Edit("A", "X1", "1"),
            Edit("A", "X2", "2"),
            Edit("B", "Y", "9"),
        });

        Assert.Equal("[A]\r\nA1=1\r\nX1=1\r\nX2=2\r\n[B]\r\nY=9\r\n[C]\r\nJ=1\r\n", result.Content);
    }

    [Fact]
    public void Insert_into_earlier_section_after_creating_a_new_one_keeps_both_anchors_correct()
    {
        // 覆盖「先新建 section、再往前面既有 section 插入」这条路径：新建走 lines.Add 并写下
        // 锚点，随后向 [Upscalers] 插入会让**所有** >= 插入点的锚点（包括刚写下的 Spoofing）
        // 整体前移；第三条 edit 再往 [Spoofing] 追加时必须落在 Dxgi=false 之后。
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Spoofing", "Dxgi", "false"),      // section 不存在 -> 新建，走 lines.Add
            Edit("Upscalers", "BrandNewKey", "1"),  // 往前面的既有 section 插入 -> 触发整体前移
            Edit("Spoofing", "Other", "2"),         // 再往刚新建的 section 追加
        });

        Assert.Equal(
            "; OptiScaler config\r\n" +
            "[Upscalers]\r\n" +
            "; fsr21, fsr22, ffx, dlss\r\n" +
            "Dx12Upscaler=auto\r\n" +
            "BrandNewKey=1\r\n" +
            "\r\n" +
            "[FrameGen]\r\n" +
            "Enabled=auto\r\n" +
            "\r\n" +
            "[Log]\r\n" +
            "LogToFile=auto\r\n" +
            "LogLevel=auto\r\n" +
            "\r\n" +
            "; added by OptiScaler Deployer\r\n" +
            "[Spoofing]\r\n" +
            "Dxgi=false\r\n" +
            "Other=2\r\n",
            result.Content);
    }

    [Fact]
    public void Matching_is_case_insensitive_for_section_and_key_but_preserves_original_spelling()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[] { Edit("upscalers", "dx12upscaler", "ffx") });

        Assert.Contains("Dx12Upscaler=ffx", result.Content);
        Assert.DoesNotContain("dx12upscaler=", result.Content);
    }

    [Fact]
    public void Key_with_spaces_around_equals_is_matched_and_left_side_preserved()
    {
        const string ini = "[Upscalers]\r\nDx12Upscaler = auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Equal("[Upscalers]\r\nDx12Upscaler =ffx\r\n", result.Content);
    }

    [Fact]
    public void Lf_only_file_is_detected_and_preserved()
    {
        const string ini = "[Upscalers]\nDx12Upscaler=auto\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Equal("\n", result.DetectedNewLine);
        Assert.Equal("[Upscalers]\nDx12Upscaler=ffx\n", result.Content);
    }

    [Fact]
    public void Empty_edit_list_returns_content_unchanged()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, Array.Empty<IniEdit>());

        Assert.Equal(Sample, result.Content);
        Assert.Empty(result.AppliedEdits);
        Assert.Empty(result.AppendedEdits);
    }

    [Fact]
    public void Duplicate_edits_for_same_key_last_one_wins()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("Upscalers", "Dx12Upscaler", "dlss"),
        });

        Assert.Contains("Dx12Upscaler=dlss", result.Content);
        Assert.Single(result.AppliedEdits);
    }

    [Fact]
    public void Blank_section_is_rejected_instead_of_appending_an_empty_section_header()
    {
        var patcher = new IniPatcher();

        // 不拦的话，Section 为空的 edit 会命中前言区或走进「新建 section」分支，
        // 往用户的 OptiScaler.ini 末尾追加一个 [] 段——那是往游戏文件里写垃圾。
        var ex = Assert.Throws<ArgumentException>(() => patcher.Patch(Sample, new[] { Edit("", "Foo", "1") }));

        // 钉住消息里含 Section，这样消息退化成裸堆栈时会红
        Assert.Contains("Section", ex.Message);
    }

    [Fact]
    public void Blank_key_is_rejected()
    {
        var patcher = new IniPatcher();

        // 用空白而非空串，确保守卫用的是 IsNullOrWhiteSpace 而不是 IsNullOrEmpty；
        // Key 为空会命中任何以 = 开头的畸形行。
        var ex = Assert.Throws<ArgumentException>(() => patcher.Patch(Sample, new[] { Edit("Upscalers", "   ", "1") }));

        Assert.Contains("Key", ex.Message);
    }

    [Fact]
    public void Section_and_key_with_surrounding_whitespace_are_trimmed_instead_of_creating_a_duplicate_section()
    {
        // " Log " 匹配不上文件里的 "Log"，旧行为会往文件末尾追加一个字面的 [ Log ] 段，
        // 而 INI 解析器读它时 trim 内侧空格得到 Log —— 游戏文件里于是出现两个同名 section。
        const string ini = "[Log]\r\nLogToFile=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit(" Log ", " LogToFile ", "true") });

        Assert.Equal("[Log]\r\nLogToFile=true\r\n", result.Content);
        Assert.Single(result.AppliedEdits);
        Assert.Empty(result.AppendedEdits);
    }

    [Fact]
    public void Mixed_line_endings_still_get_patched_instead_of_silently_skipping()
    {
        // 第 2 行以 bare LF 结尾。改之前这条 edit 会静默不生效：
        // Foo=1\nDx12Upscaler=auto 被当成一行，匹配到的是 Foo。输出把那个 bare LF
        // 归一化成 CRLF 是有意的（对「最小干预」的有意识让步，换取「不会静默漏改」）。
        const string ini = "[Upscalers]\r\nFoo=1\nDx12Upscaler=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Single(result.AppliedEdits);
        Assert.Equal("auto", result.AppliedEdits[0].OldValue);
        Assert.Equal("[Upscalers]\r\nFoo=1\r\nDx12Upscaler=ffx\r\n", result.Content);
    }

    [Fact]
    public void Appended_keys_keep_edit_order_even_when_other_edits_matched_in_place()
    {
        // 钉住的是**可观察契约**：追加顺序 = 这些 edit 在入参里首次出现的顺序，
        // 与「同一批里有多少条是就地命中」无关。就地命中会触发 pending.Remove，
        // 而实现刻意不依赖 Dictionary 删除后的枚举序（理由写在 IniPatcher.cs 里
        // pending / orderedKeys 上方的注释）。
        // 既有的 Several_keys_appended… 是纯追加场景，从不 Remove，盖不到这条。
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Log", "Zzz", "3"),
            Edit("Upscalers", "Dx12Upscaler", "ffx"),  // 就地命中，触发 Remove
            Edit("Log", "Aaa", "1"),
            Edit("Log", "LogLevel", "2"),              // 又一处就地命中
            Edit("Log", "Mmm", "2"),
        });

        Assert.Equal(2, result.AppliedEdits.Count);
        Assert.Equal(3, result.AppendedEdits.Count);

        var body = result.Content.Split(result.DetectedNewLine);
        var iZzz = Array.IndexOf(body, "Zzz=3");
        var iAaa = Array.IndexOf(body, "Aaa=1");
        var iMmm = Array.IndexOf(body, "Mmm=2");

        Assert.True(iZzz >= 0 && iZzz < iAaa && iAaa < iMmm,
            $"追加顺序应为 Zzz、Aaa、Mmm（实际下标 {iZzz}, {iAaa}, {iMmm}）");
    }

    [Fact]
    public void Section_header_with_trailing_inline_comment_is_still_recognised()
    {
        // `[B] ; note` 是合法 INI 写法。不认它的话 currentSection 会停留在 A，
        // 于是针对 A/K 的 edit 会改到物理上位于 B 段的那个 K——改错 section，静默。
        const string ini = "[A]\r\nX=1\r\n[B] ; note\r\nK=2\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "K", "9") });

        // A 段没有 K，所以应当**追加**到 A 段末尾，B 段的 K=2 一个字符都不能动
        Assert.Equal("[A]\r\nX=1\r\nK=9\r\n[B] ; note\r\nK=2\r\n", result.Content);
        Assert.Single(result.AppendedEdits);
        Assert.Empty(result.AppliedEdits);
    }

    [Fact]
    public void Section_header_with_inner_padding_is_trimmed_before_matching()
    {
        // 文件侧的 `[ Log ]`：TryReadSectionHeader 必须 trim 内侧空白，否则 currentSection
        // 会是 " Log "，与 edit 的 "Log" 匹配不上（OrdinalIgnoreCase 救不了空格差异），
        // 于是往文件末尾追加出**第二个** [Log] 段——与「先 Trim 归一化」那条防的是同一个
        // 失效模式，只是入口从 edit 侧换成了文件侧。
        // 既有的 Section_and_key_with_surrounding_whitespace… 只覆盖 edit 侧带空格。
        // 真 ini 实测 paddedHeaders=0，但被 patch 的 ini 可能来自 ManualDirectory。
        const string ini = "[ Log ]\r\nLogToFile=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Log", "LogToFile", "true") });

        Assert.Equal("[ Log ]\r\nLogToFile=true\r\n", result.Content);
        Assert.Single(result.AppliedEdits);
        Assert.Empty(result.AppendedEdits);
    }

    [Fact]
    public void Line_starting_with_bracket_but_without_closing_bracket_is_skipped_entirely()
    {
        // `[Foo=bar` 不是 section 头。旧行为会把它当成键名 `[Foo` 解析，
        // 那是在往游戏文件里制造一个谁也不认识的键。
        // 第二条 edit 的 Key 故意取成 `[Broken`——正好等于那条畸形行 `=` 的左侧：
        // 只有这样才能观察到「整行跳过」与「掉进键解析」的区别。对任何正常 Key
        // 而言两者结果相同（畸形行匹配不上就被静默忽略），所以只靠一条普通 edit 钉不住这个分支。
        const string ini = "[A]\r\nK=1\r\n[Broken=bar\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "K", "9"), Edit("A", "[Broken", "9") });

        // `[Broken=bar` 一个字符都不能动；`[Broken` 在 A 段不是键，所以走追加，落在段末
        Assert.Equal("[A]\r\nK=9\r\n[Broken=bar\r\n[Broken=9\r\n", result.Content);
        Assert.Single(result.AppliedEdits);
        Assert.Single(result.AppendedEdits);
    }

    [Fact]
    public void Hash_comment_lines_are_never_parsed_as_keys()
    {
        // Sample 常量里没有 # 注释行，所以 All_comment_lines_survive_verbatim 单靠它盖不到 # 分支。
        // 这里用一个 Key 字面上等于 # 注释行 `=` 左侧的 edit 来钉住：# 行必须整行当注释跳过，
        // 绝不参与键解析。少了 `|| trimmed.StartsWith('#')` 的话实现会去改写这条注释，
        // 而 IIniPatcher 承诺「所有以 ; 或 # 开头的行原样保留」。
        const string ini = "[A]\r\n#Legacy=1\r\nK=2\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "#Legacy", "9") });

        // 注释行一个字符都不能动；#Legacy 在 A 段不是键，所以走追加，落在段末
        Assert.Equal("[A]\r\n#Legacy=1\r\nK=2\r\n#Legacy=9\r\n", result.Content);
        Assert.Empty(result.AppliedEdits);
        Assert.Single(result.AppendedEdits);
    }

    [Fact]
    public void Input_without_trailing_newline_gains_one_when_edits_are_applied()
    {
        // 已知且**有意**的行为：edits 非空时 Join 会给不以换行结尾的输入补一个尾换行。
        // 钉住它是为了防止后来者把这当 bug「修」掉——空 edits 走早返回分支，不补。
        const string ini = "[A]\r\nK=1";       // 注意：不以换行结尾
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "K", "2") });

        Assert.Equal("[A]\r\nK=2\r\n", result.Content);
    }
}
```

「左半部分原样保留」这条（`Dx12Upscaler =ffx`）看着别扭，但它正是**最小干预**的体现：我们只动 `=` 右边，绝不重排用户的空格。这**不是**纯理论构造：真 `OptiScaler.ini` 里 `[NvngxFG]` 段的 L721 `DispatchFlags = auto`、L726 `ShowDebug = auto`、L731 `DisableHudless = auto` 就是等号两侧带空格的（实测）。它们目前不在任何 profile 的 edits 里，但用户手写 profile JSON 时完全可能碰到；而测试钉住这条主要是为了防实现悄悄开始重新格式化整行。

- [x] **Step 3: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter IniPatcherTests`
Expected: 编译错误 `IniPatcher 不存在`

- [x] **Step 4: 写 `Ini/IniPatcher.cs`**

```csharp
using System.Text;
using Deployer.Core.Domain;

namespace Deployer.Core.Ini;

/// <remarks>
/// 后来者最容易搞错的三件事，都写在实现之前：
/// 1. **不重新序列化**是设计文档 §8.2 的硬约束，不是风格偏好。真 OptiScaler.ini
///    共 1571 行，其中 864 行（55%）是注释；任何现成 INI 库（SimpleIni / IniParser）
///    在解析-回写时都会把注释整片丢掉。所以这里坚持逐行文本改写，绝不 round-trip。
/// 2. **section 作用域匹配是硬性要求**。同一个键名 Enabled=auto 在真文件里分属 5 个
///    不同 section（[FrameGen] L25、[DLSS] L664、[OutputScaling] L1154、[CAS] L1172、
///    [Magnifier] L1234）。不按 section 限定，一条 edit 就会一次改掉五个毫不相干的功能开关。
/// 3. **只替换 = 右侧**：左半部分（键名连同用户原有的空格）原样保留，这是最小干预的直接体现。
/// </remarks>
public sealed class IniPatcher : IIniPatcher
{
    private const string AddedByDeployer = "; added by OptiScaler Deployer";

    public IniPatchResult Patch(string originalContent, IReadOnlyList<IniEdit> edits)
    {
        // 用户手写 profile JSON 时很容易在 Section / Key 前后多打空格（" Log "）。不归一化的话
        // 它匹配不上文件里的 "Log"，会掉进「新建 section」分支往文件末尾追加一个字面的
        // `[ Log ]` 段——而 INI 解析器读它时会 trim 内侧空格得到 Log，于是游戏文件里出现两个
        // 同名 section、值互相冲突。这里选择 Trim 而不是拒绝：意图是明确的、错误是可恢复的，
        // 不该让整次部署失败。归一化后的 edits 也会原样出现在返回的 AppliedEdits / AppendedEdits 里。
        var normalizedEdits = new List<IniEdit>(edits.Count);
        foreach (var edit in edits)
        {
            normalizedEdits.Add(edit with
            {
                Section = edit.Section?.Trim() ?? string.Empty,
                Key = edit.Key?.Trim() ?? string.Empty,
            });
        }

        // 空 Section / 空 Key 是**硬错误**，不是「匹配不上就算了」：Section 为空会命中
        // 第一个 section 头之前的前言区，Key 为空会命中任何以 = 开头的畸形行；两者都
        // 会让「新建 section」分支往用户的 OptiScaler.ini 末尾追加一个 [] 段——那是往
        // 游戏文件里写垃圾。守卫放在这一层而不是调用方：IniPatcher 是真正落笔的地方，
        // 而 edits 可能来自用户自己写的 profile JSON。
        foreach (var edit in normalizedEdits)
        {
            if (string.IsNullOrWhiteSpace(edit.Section) || string.IsNullOrWhiteSpace(edit.Key))
            {
                throw new ArgumentException(
                    $"IniEdit 的 Section 与 Key 都不能为空白（实际：Section=\"{edit.Section}\"，Key=\"{edit.Key}\"）。",
                    nameof(edits));
            }
        }

        var newLine = DetectNewLine(originalContent);
        var lines = SplitLines(originalContent);

        if (normalizedEdits.Count == 0)
        {
            return new IniPatchResult(originalContent, Array.Empty<IniEdit>(), Array.Empty<IniEdit>(), newLine);
        }

        // 后出现的 edit 覆盖先出现的（"last one wins"），但顺序按**首次出现**的位置保留。
        // 不能直接靠 pending.Values 的枚举顺序：Dictionary 一旦 Remove 过，插入序就不再由
        // 规范保证（.NET 8 实测幸存键的相对顺序不变，但那是实现细节、不是契约）。
        var pending = new Dictionary<(string Section, string Key), IniEdit>(KeyComparer.Instance);
        var orderedKeys = new List<(string Section, string Key)>();
        foreach (var edit in normalizedEdits)
        {
            var key = (edit.Section, edit.Key);
            if (!pending.ContainsKey(key))
            {
                orderedKeys.Add(key);
            }

            pending[key] = edit;
        }

        var applied = new List<IniEdit>();
        var sectionAppendAnchor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;   // 注释行：原样保留，绝不参与解析
            }

            if (trimmed.StartsWith('['))
            {
                // 以 [ 开头的行一律不当作键解析：要么是 section 头，要么整行跳过。
                if (TryReadSectionHeader(trimmed, out var sectionName))
                {
                    if (currentSection.Length > 0)
                    {
                        sectionAppendAnchor[currentSection] = LastContentLineIndex(lines, i - 1);
                    }

                    currentSection = sectionName;
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;   // 空行或畸形行：原样保留
            }

            var key = line[..eq].Trim();
            if (!pending.TryGetValue((currentSection, key), out var edit))
            {
                continue;
            }

            lines[i] = line[..eq] + "=" + edit.NewValue;   // 只替换 = 右侧，左半部分（含原有空格）原样保留
            applied.Add(edit.WithOldValue(line[(eq + 1)..].Trim()));
            pending.Remove((currentSection, key));
        }

        if (currentSection.Length > 0)
        {
            sectionAppendAnchor[currentSection] = LastContentLineIndex(lines, lines.Count - 1);
        }

        var appended = new List<IniEdit>();
        var createdSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in orderedKeys)
        {
            if (!pending.TryGetValue(key, out var edit))
            {
                continue;   // 已在上面就地替换过
            }

            if (sectionAppendAnchor.TryGetValue(edit.Section, out var anchorLine) && anchorLine >= 0)
            {
                lines.Insert(anchorLine + 1, edit.Key + "=" + edit.NewValue);

                // 插入使插入点之后的所有行整体下移一行，因此**每一个**指向后半部分的锚点都过期了。
                // 只更新当前 section 会让后面 section 的锚点偏差，偏差累积到超过该 section 的长度时，
                // 新键会落到 section 头之前——也就是写进上一个 section，直接击穿 section 作用域这条
                // 不变量。section 总数最多几十个，整体扫一遍的代价可以忽略。
                foreach (var pair in sectionAppendAnchor.ToArray())
                {
                    if (pair.Value >= anchorLine + 1)
                    {
                        sectionAppendAnchor[pair.Key] = pair.Value + 1;
                    }
                }

                // 当前 section 的锚点推到刚插入的那一行，同 section 的下一条接着排在它后面
                sectionAppendAnchor[edit.Section] = anchorLine + 1;
            }
            else
            {
                if (createdSections.Add(edit.Section))
                {
                    if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                    {
                        lines.Add(string.Empty);
                    }

                    lines.Add(AddedByDeployer);
                    lines.Add("[" + edit.Section + "]");
                }

                lines.Add(edit.Key + "=" + edit.NewValue);
                sectionAppendAnchor[edit.Section] = lines.Count - 1;
            }

            appended.Add(edit.WithOldValue(null));
        }

        return new IniPatchResult(Join(lines, newLine), applied, appended, newLine);
    }

    /// <summary>
    /// 识别 section 头：以 [ 开头、扫到**第一个** ] 为止，] 之后的内容（行内注释）一律忽略，
    /// 内侧空白 trim 掉。没有 ] 就返回 false，整行由调用方跳过。
    /// </summary>
    /// <remarks>
    /// 为什么 `]` 后面允许有内容：`[Log] ; 只写日志` 是完全合法的 INI 写法，通行解析器都认。
    /// 要求 `]` 必须在行尾会让这一行既不是 section 头、又没有 `=`，于是被当成空行跳过——
    /// currentSection 保持不变，后面所有键被归到**上一个** section 名下。失效方向是
    /// 「改到别的 section 的键」与「往文件末尾追加一个重复 section」，两者都是静默的。
    /// 为什么没有 `]` 时整行跳过而不是当键解析：`[Foo=bar` 会被解析成键名 `[Foo`，
    /// 那是在往游戏文件里制造一个谁也不认识的键。
    /// </remarks>
    private static bool TryReadSectionHeader(string trimmedLine, out string sectionName)
    {
        sectionName = string.Empty;

        var close = trimmedLine.IndexOf(']');
        if (close < 0)
        {
            return false;
        }

        sectionName = trimmedLine[1..close].Trim();
        return true;
    }

    /// <summary>
    /// 从 <paramref name="from"/> 往前找最后一个非空白行，作为该 section 的追加锚点。
    /// </summary>
    /// <remarks>
    /// 注意**注释行算内容**：向既有 section 追加时，新键会落在该段末尾的注释块**之后**，而不是紧贴
    /// 最后一个真实键。在真 OptiScaler.ini 上这表现为：37 个 section 头**全部**是「`; ----` 横幅 /
    /// `[Header]` / 横幅」三明治（实测 immediatelyAboveHeader=37、immediatelyBelowHeader=37），所以
    /// 某个头**上方**那条横幅是**那个头的开头装饰**，不是上一个 section 的收尾（决定性反证：L1 的横幅
    /// 在文件第一行，上方根本没有任何 section）。于是新键会落在「下一个 section 的开头横幅之后、它的
    /// `[Header]` 之前」——section 作用域不破（新键仍在下一个头之前，文件仍合法、解析器读得对），但
    /// 人读起来像是属于下一个 section：这是已知的**观感退化**，不是正确性缺陷。
    /// 不改成「锚到最后一个真实键行」，是因为它对末尾 section 更糟：`[Hotfix]`（最后一段）的最后一个
    /// 真实键 `OutputResourceBarrier=auto`（L1551）之后是一个空行（L1552）加 19 行文档注释块
    /// （L1553..L1571，以一条 Microsoft 文档 URL 收尾），跳过末尾注释会把新键楔进那个键与解释
    /// 它的注释之间。两种规则都不是普遍更优，都是在猜「末尾注释块属于上面还是下面」，而这份文件里
    /// 两种情况都真实存在，故保持现规则并记进已知限制。
    /// 返回 `-1` 的唯一情形是 `from` 之前全是空白行；调用处那个 `>= 0` 判断在当前所有路径下恒为真
    /// （三个写入点都保证了非负），是一段死防御，留着防将来新增一个可能写入 `-1` 的路径。
    /// </remarks>
    private static int LastContentLineIndex(List<string> lines, int from)
    {
        while (from >= 0 && lines[from].Trim().Length == 0)
        {
            from--;
        }

        return from;
    }

    private static string DetectNewLine(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// 按 \n 切，再去掉每行末尾的 \r。这样混合行尾的文件也能被正确逐行解析。
    /// </summary>
    /// <remarks>
    /// 若按检测到的 newLine 切，夹在 CRLF 文件里的 bare LF 会把相邻两行并成一行，
    /// 那一行里的键会**静默**匹配不上——Join 能原样还原所以文件不会被写坏，
    /// 但配置没生效，这是最难排查的一类失败。
    /// 代价是输出会把零星 bare LF 归一化成主导行尾，这算是对「最小干预」的一次
    /// 有意识让步：换取的是「不会静默漏改」。
    /// 另一个已知的既有行为：输入不以换行结尾时，输出会补一个换行。真 OptiScaler.ini
    /// 以换行结尾，且 edits 为空时 Patch 直接原样返回，所以实际碰不到。
    /// </remarks>
    private static List<string> SplitLines(string content)
    {
        var parts = content.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        // 文件以换行结尾时 Split 会多出一个空尾项；去掉它，Join 时统一补回
        if (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    private static string Join(List<string> lines, string newLine)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append(newLine);
        }

        return sb.ToString();
    }

    private sealed class KeyComparer : IEqualityComparer<(string Section, string Key)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((string Section, string Key) x, (string Section, string Key) y) =>
            string.Equals(x.Section, y.Section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Section, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Section),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }
}
```

**为什么用 `orderedKeys` 而不直接遍历 `pending.Values`**：`Dictionary` 在不删除时保持插入序，但本实现在命中时会 `Remove`，之后顺序就不再可靠。用一个显式的 `List` 记住首次出现顺序，比依赖未文档化的容器行为稳。

**关于「`orderedKeys` 杀不死变异」**：把 `foreach (var key in orderedKeys)` 改成 `foreach (var key in pending.Keys.ToList())`，全部测试依旧绿——这**不是覆盖缺口，是等价变异体**。.NET 8 的 `Dictionary.Remove` 只把槽位标记为空闲并挂进 freeList，**不压缩** `_entries` 数组；`KeyCollection` 枚举器按下标 `0.._count` 线性扫并跳过已释放的槽，所以删除之后**幸存键的相对顺序不变**。本实现又只在最初那一个 `foreach` 里往 `pending` 加键（之后再无 Add），freeList 复用无从发生，两者逐输入行为相同。`orderedKeys` 仍然要留：它把「顺序 = 首次出现顺序」这个意图写成**结构**，而不是寄望于运行时实现细节；哪天有人改成边扫边 Add、或者换个容器，寄望就会失效。`Appended_keys_keep_edit_order_even_when_other_edits_matched_in_place` 钉住的是可观察契约（混合命中与追加时的顺序），它杀不死上面那个等价变异体，但契约真被破坏时会变红。**别因为「变异没被杀死」就去删 `orderedKeys` 或那条测试。**

**为什么正序插入而不用 `Reverse()`**：每插入一行就把 `sectionAppendAnchor[section]` 推到新行的位置，下一条自然排在它后面——正序遍历直接得到正确结果，逆序反而会颠倒。

**插入之后必须把插入点之后所有 section 的锚点整体前移一行**：`sectionAppendAnchor` 里的下标是扫描阶段对着**未修改**的 `lines` 记下来的，而追加阶段用 `lines.Insert(anchorLine + 1, …)` 改的是同一个列表。插入使插入点之后的每一行都下移一行，所以**每一个** `>= anchorLine + 1` 的锚点都过期了，不只是当前 section 的那一个。只更新当前 section 的话，后面所有 section 的锚点各偏差 1，偏差还会随追加次数累积：偏差 ≥ 1 时新键不再落在 section 末尾，还可能插进一个注释块与它所解释的键之间把两者拆散（在一份 55% 是注释的文件里这是用户可见的退化）；偏差累积到超过该 section 的长度时，新键会落到 section 头**之前**，也就是写进了上一个 section——直接击穿类头 `<remarks>` 第 2 条声称的 section 作用域硬性要求。实测：输入 `[Upscalers]\r\nDx12Upscaler=auto\r\n\r\n[FrameGen]\r\nEnabled=auto\r\n` + edits `(Upscalers,New1,1) (Upscalers,New2,2) (FrameGen,New3,3)`，`New3=3` 会跑到 `[FrameGen]` 头之前、也就是写进了 `[Upscalers]`。实现上先 `sectionAppendAnchor.ToArray()` 拿快照再遍历。**注意 net8.0 实测纠正一处旧假断言**：`Dictionary` 的枚举器确实校验 `_version`，但索引器 setter 走 `TryInsert(..., updateVersion: false)`——**更新已有键不递增 `_version`、不会抛**（探针 `foreach (var kv in dd) { dd[kv.Key] = kv.Value + 1; }` 实测 `n=3 threw=none`），只有**插入新键**才递增并抛 `InvalidOperationException`（探针 `dd[kv.Key + "z"] = 9` 实测 `n=1 threw=InvalidOperationException`）。本前移循环里 `pair.Key` 必然是已有键（只在既有锚点表上前移），所以 `.ToArray()` 快照当前**不是必需**的；保留它是为了防止将来有人在循环里写出「可能新增键」的形式，那时就会真的抛。**别因为「去掉快照的变异没被杀死」就去删它**（与 `orderedKeys` 的处置同理：防御性正确、开销可忽略）。section 总数最多几十个，整体扫一遍的代价可以忽略。「新建 section」分支走的是 `lines.Add(...)`，往列表末尾追加不会让任何既有锚点失效，所以**不需要**这个处理。回归看守是 `Appends_into_two_different_existing_sections_both_land_at_their_own_section_end`（既有的追加测试全都只碰一个 section，盖不到）。

**`LastContentLineIndex` 存在的理由**：若直接用「下一个 section 头的前一行」作为插入点，而那一行是分隔用的空行，新键就会落在空行之后、与 section 本体隔开。功能上仍正确（还在同一个 section 里），但人读起来像是另一个区块。注意它只跳**纯空白行**，`; ----` 这样的注释横幅算内容、会成为锚点，所以新键落在横幅**之后**。**这里曾有一处假断言，现按实测纠正**：真 `OptiScaler.ini` 的 37 个 section 头**全部**是「横幅 / `[Header]` / 横幅」三明治（实测 `immediatelyAboveHeader=37`、`immediatelyBelowHeader=37`），所以某个头**上方**那条横幅是**那个头的开头装饰**，不是上一个 section 的收尾——决定性反证：L1 的横幅位于文件第一行，上方根本没有任何 section。后果是：向既有 section 追加时，新键会落在「下一个 section 的开头横幅之后、它的 `[Header]` 之前」。section 作用域不破（新键仍在下一个头之前，INI 解析器读得对、文件仍合法），但人读起来像是属于下一个 section——这是已知的**观感退化**，不是正确性缺陷。它在全是空白行时返回 `-1`，而调用处那个 `anchorLine >= 0` 判断在当前所有路径下恒为真（三个写入点都保证了非负），是一段死防御，留着只为防将来新增一个可能写入 `-1` 的路径。

**为什么不改成「锚到最后一个真实键行」（跳过末尾注释与空白）**：再复核曾建议此修法，控制器实测后**驳回**，裁定为终审、行为一律不动。理由：（1）对 section 1..36（后面都跟着一个被横幅夹住的头）这个改法确实更好，但对最后一个 section `[Hotfix]`（L1483）它会把新键楔进「L1551 `OutputResourceBarrier=auto` 与紧随其后的空行（L1552）加 19 行解释它的文档注释块（L1553..L1571，以一条 Microsoft 文档 URL 收尾）」之间——正是本计划自己判定为「用户可见退化、不可接受」的形状。（2）`zeroKeySections=0`，所以这个改法需要的 `floor`（section 头下界）参数在出厂 payload 上**根本不会被触发**，它只为 `PayloadOrigin.ManualDirectory` 指向的任意 ini 存在，却要冒在刚修好 Critical 的锚点逻辑里引入正确性回归的风险，去换一个纯观感问题。（3）Task 9 的 `BuildIniEdits` 还没写，此刻并不知道实际会往哪些 section 追加；信息不足时把启发式钉死是错的。**两种规则都不是普遍更优**，都是在猜「末尾注释块属于上面还是下面」，而这份文件里两种情况都真实存在（37/37 个头被夹住 vs `[Hotfix]` 的 19 行尾部文档块）。故保持现规则、把取舍写进已知限制，把「锚点启发式」推迟到**计划 B**处置（届时真 ini 回归测试与 Task 9 的实际 edit 集合都已知）。

**section 头怎么解析**：判定抽在私有方法 `TryReadSectionHeader` 里，规则与通行 INI 解析器对齐——以 `[` 开头、扫到**第一个** `]` 为止，`]` 之后的内容（行内注释）一律忽略，内侧空白 trim 掉；没有 `]` 就返回 `false`，整行由调用方跳过。

为什么 `]` 之后允许有内容：`[Log] ; 只写日志` 是完全合法的 INI 写法。旧判定 `trimmed.StartsWith('[') && trimmed.TrimEnd().EndsWith(']')` 要求 `]` 必须在行尾，于是这一行既不是 section 头、又没有 `=`，被当成空行跳过——`currentSection` 保持不变，后面所有键被归到**上一个** section 名下。两个后果都是静默的：其一，针对 `A` 段的 edit 会改到物理上位于 `B` 段的那个同名键（实测 `[A]\r\nX=1\r\n[B] ; note\r\nK=2\r\n` + edit `(A,K,"9")` 会把 `B` 段的 `K=2` 改成 `K=9`）；其二，针对 `B` 段的 edit 匹配不上，于是往文件末尾追加出**第二个** `[B]` 段。可达性：仓库根的真 `OptiScaler.ini` 37 个 section 头全部干净，但被 patch 的 ini 来自 `Payload.RootDirectory`，而 `PayloadOrigin` 含 `ManualDirectory`——用户可以指定任意本地目录作为 payload。

为什么没有 `]` 时整行跳过而不是当键解析：`[Foo=bar` 会被 `IndexOf('=')` 解析成键名 `[Foo`，那是在往游戏文件里制造一个谁也不认识的键。这是一处**有意的行为改变**（旧实现会当键解析）。其余分支与旧实现逐一核对过等价：`[ Section ]` → trim 后 `Section`；`[]` → 空 section 名；`[Sec[tion]` → 取第一个 `]`，得 `Sec[tion`（旧的 `[1..^1]` 对同一输入也得 `Sec[tion`）。

**为什么空 Section / 空 Key 是硬错误而不是「匹配不上就跳过」**：`currentSection` 初值是 `string.Empty`，一条 Section 为空白的 edit 会静默命中第一个 section 头之前的前言区；若没命中，就落进「新建 section」分支，往用户的 `OptiScaler.ini` 末尾追加一个 `[]` 段——那是往游戏文件里写垃圾，直接违背「绝不损坏游戏文件」这条第一承诺。Key 为空白同理，会命中任何以 `=` 开头的畸形行。失效方向是**静默写入垃圾**，所以必须抛 `ArgumentException` 而不是当作未命中。守卫放在 `IniPatcher` 而非调用方：它是真正落笔改游戏文件的那一层，而 edits 可能来自用户自己写的 profile JSON（Task 5 的 `JsonProfileStore` 从磁盘读），放在这最后一道关口才能同时覆盖 builtins、用户 JSON 与任何未来的调用方；Task 9 的 `BuildIniEdits` 因此不再重复校验。

**为什么先 Trim 归一化 Section / Key，而空白仍然抛**：守卫循环之前先把每条 edit 的 `Section` / `Key` 各自 `Trim()` 一遍，之后所有逻辑（守卫、`pending`、`orderedKeys`）都用归一化后的列表，返回的 `AppliedEdits` / `AppendedEdits` 里也是归一化后的值。不归一化的话 `" Log "` 能通过 `IsNullOrWhiteSpace` 守卫，却匹配不上扫描期从文件读出的 `"Log"`（`OrdinalIgnoreCase` 救不了空格差异），于是掉进「新建 section」分支往文件末尾追加一个字面的 `[ Log ]` 段——而 INI 解析器读 `[ Log ]` 时会 trim 内侧空格得到 `Log`，游戏文件里于是出现**两个同名 section**、值互相冲突。这与上面那段守卫点名要防的事（往 ini 末尾追加一个 `[]` 段）是同一个失效模式，只是入口从「空白」换成了「前后带空白」；只拦空白等于守卫只挡了一半，「同时覆盖 builtins、用户 JSON 与任何未来的调用方」那句也就不成立。

为什么这里选 Trim 而空白仍然抛 `ArgumentException`：两者防的是不同的东西。前后多打空格是**意图明确、错误可恢复**的——用户想改的就是 `Log` 段，Trim 之后的行为正是他想要的，不该让整次部署失败；而 Section / Key 全空白是**意图不可恢复**的，无从猜测用户想改哪个段哪个键，只能报错让他改 profile。注意 `Blank_key_is_rejected` 传的 `"   "` 经 Trim 后变成 `""`，仍会被守卫拒绝，那条测试继续通过。

**为什么 `SplitLines` 改成按 `\n` 切再剥掉行尾 `\r`**：若按检测到的主导 `newLine` 切，一个以 CRLF 为主、却夹了几行 bare LF 的文件，那几行会被并进相邻的同一个「行」字符串里。例如 `[Upscalers]\r\nFoo=1\nDx12Upscaler=auto\r\n` 会被切成 `["[Upscalers]", "Foo=1\nDx12Upscaler=auto"]`，于是针对 `Dx12Upscaler` 的 edit 实际匹配到的是 `Foo`。失效方向是**静默漏改**：`Join` 能原样还原所以文件不会被写坏，但「配置改了却没生效」是最难排查的一类失败。代价是输出会把零星 bare LF 归一化成主导行尾——这是对「最小干预」的一次有意识让步，换取的是「不会静默漏改」。仓库根目录的真 `OptiScaler.ini` 实测 `crlf=1571 bareLf=0`，但**这不能推广到实际被 patch 的那份文件**：被 patch 的 ini 来自 `Payload.RootDirectory`，而 `PayloadOrigin` 枚举含 `ManualDirectory`——用户可以指定任意本地目录作为 payload，那份 ini 可能来自任何地方（被用户手改过、从别的机器拷来、被某个编辑器重存过）；而且不同 OptiScaler 版本的 ini 行尾也不保证一致。加固本身仍然正确，只是威胁模型要说准，不能窄化成「用户拿 Unix 编辑器手改过」这一条路径；纯 CRLF 与纯 LF 两种文件在改动前后行为完全一致（加固改动前的既有测试全部保持绿）。

**这条 `ArgumentException` 在下游怎么呈现**：它从 `IniPatcher.Patch` 抛出，穿过纯函数 planner、executor，最终由 Task 15 CLI 顶层那句 `return Fail(ex.Message, ex)` 打到 stderr——消息里带着 `Section=""`/`Key=""` 的实际值，正好是用户手改 profile JSON 时定位错误所需的线索，所以**不需要**再包一层 `DeployerException`（包了只会把这条线索换成一句更泛的话术）。计划 C 的 WPF UI 需遵守同一条：把这个异常的消息原文呈现给用户，而不是显示成「未知错误」。

**`SplitLines` 会补一个尾换行**：若输入不以换行结尾，`Join` 后输出会多一个（例如 `"[A]\nK=1"` → `"[A]\nK=1\n"`）。这对 INI 无害（OptiScaler 的 SimpleIni 不关心），但计划 B 写「真 INI 回归测试」时必须明确断言口径：要么断言 `Patch(真文件)` 的输出以换行结尾，要么先确认真文件本来就以换行结尾（实测 1571 行 CRLF，是）。别把这条当成 bug 去「修」。这条已知行为已由 `Input_without_trailing_newline_gains_one_when_edits_are_applied` 钉住（空 edits 走早返回分支，不补）。

**匹配到的键行，其 `=` 右侧的行尾注释会被一并替换掉**：`K=1 ; keep me` 被改成 `K=9`，`; keep me` 消失。这在契约上不算违约（`IIniPatcher` 承诺的是「所有**以** `;` 或 `#` **开头**的行原样保留」，行尾注释不在承诺范围内），且真 payload ini 实测零行内注释，故当前不可达。**不做保留是有意的**：通行 INI 解析器很可能把值后面的 `;` 视为值的一部分，拼回去会让 OptiScaler 读到 `"9 ; keep me"` 而解析失败——那比丢一条注释严重得多，失效方向更糟。（`external/simpleini` 是未初始化的子模块，本地无源码可查证，所以这里不凭猜测改行为。）

**追加键的落点是观感退化（不是正确性缺陷）**：向既有 section 追加新键时，因为 `LastContentLineIndex` 把末尾注释块算作内容、且真 `OptiScaler.ini` 的 37 个 section 头全部是「横幅 / `[Header]` / 横幅」三明治，新键会落在**下一个 section 的开头横幅与其 `[Header]` 之间**。section 作用域不破（新键仍物理位于下一个头之前，INI 解析器把它归给上一个 section、文件仍合法、OptiScaler 读得对），只是人读起来像是属于下一个 section。这在真 payload 上向既有 section 追加时是**常态**而非边角情形：36/36 个段间间隔（37 个头两两相邻）**全部**以横幅结尾，所以每一次跨段追加都会命中。处置：**行为不动**（理由见上文「为什么不改成锚到最后一个真实键行」的终审裁定），把这类**观感降级**记为 **Task 9 `Warnings` 通道**的典型候选条目——由 planner 报给 UI 让用户知情，而不是在 `IniPatcher` 这一层单方面加通道（`IniPatchResult` 的 `Warnings` 已裁定推迟到 Task 9）。

- [x] **Step 5: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter IniPatcherTests`
Expected: `失败: 0，通过: 26`

- [x] **Step 6: 用真 INI 做一次人工核对**

这一步不是单测，是防止「测试全绿但真文件被改坏」。

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler"
$orig = Get-Content "OptiScaler.ini"
$origComments = $orig | Where-Object { $_.TrimStart().StartsWith(';') -or $_.TrimStart().StartsWith('#') }
"原文件行数: $($orig.Count)"
"原文件注释行数: $($origComments.Count)"
"原文件 section 数: $(($orig | Where-Object { $_ -match '^\s*\[' }).Count)"
```
Expected: 记下三个数字（`1571` / `864` / `37`）。计划 B 之后会有针对真 INI 的回归测试；此处仅建立基线。三个数字已由控制器独立实测确认。

**计划 B 真 INI 回归测试的三条硬信息**（上一轮实施者在真 ini 上实测得到，届时必须落实）：
1. **必须探测至少 2 个 section**：`[Upscalers]` 作为文件第一个 section，在锚点前移缺陷下**不会**漂移（它前面没有别的 section 插入去触发前移），单探它抓不到跨段错位。
2. **必须加 `tailNonBlank` 断言**：「追加键之后到下一个 section 头之间只能是空白行或同批 probe」。上一轮实测：只有「行数对、注释数对、超序列对、键仍在正确 section 内、`OldValue` 为 null」这 5 条断言时，在锚点前移缺陷下**全部通过**（真文件 section 够长，10 条追加最大漂移仅 8 行）；加了 `tailNonBlank` 才炸出 8 处 ANCHOR DRIFT。
3. **应额外覆盖一个最短的 section，并用真 ini 验证注释重数断言**：`All_comment_lines_survive_verbatim` 的「带重数」强度在 `Sample` 上尚未被真正激发（Sample 里的注释行互不相同，没有任何一条重复）；真 ini 里最常见的那条 `; ----` 横幅精确重复 **75** 次（形似横幅的行共 78 条，另有 3 条长度略不同的变体各出现 1 次），届时才会兑现。

- [x] **Step 7: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add comment-preserving IniPatcher"
```

---

## Task 5: Profile —— 后端策略

Spec §8.1。已确认的决策 #8：DX11/Vulkan 走 `ffx_12`（DX12 interop，能吃到 FSR4，代价约 10% 性能）为默认，同时提供一个无损失的原生档，UI 上可切。

**Files:**
- Create: `src/Deployer.Core/Profiles/BuiltinProfiles.cs`
- Create: `src/Deployer.Core/Profiles/IProfileStore.cs`
- Create: `src/Deployer.Core/Profiles/JsonProfileStore.cs`
- Test: `tests/Deployer.Core.Tests/BuiltinProfilesTests.cs`
- Test: `tests/Deployer.Core.Tests/JsonProfileStoreTests.cs`

- [x] **Step 1: 写 `BuiltinProfiles` 的失败测试**

`tests/Deployer.Core.Tests/BuiltinProfilesTests.cs`：
```csharp
using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using Xunit;

namespace Deployer.Core.Tests;

public class BuiltinProfilesTests
{
    private static string? Value(UpscalerProfile profile, string section, string key) =>
        profile.Edits.FirstOrDefault(e =>
            string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))?.NewValue;

    [Fact]
    public void AmdRdna4_pins_fsr4_indices_instead_of_auto()
    {
        var profile = BuiltinProfiles.AmdRdna4;

        Assert.Equal("ffx", Value(profile, "Upscalers", "Dx12Upscaler"));
        Assert.Equal("ffx_12", Value(profile, "Upscalers", "Dx11Upscaler"));
        Assert.Equal("ffx_12", Value(profile, "Upscalers", "VulkanUpscaler"));
        Assert.Equal("0", Value(profile, "FSR", "UpscalerIndex"));
        Assert.Equal("0", Value(profile, "FSR", "FGIndex"));
        Assert.Equal("0", Value(profile, "FSR", "Fsr4ForceModel"));
    }

    [Fact]
    public void AmdRdna4_disables_spoofing_because_amd_needs_no_nvidia_mask()
    {
        var profile = BuiltinProfiles.AmdRdna4;

        Assert.Equal("false", Value(profile, "Spoofing", "Dxgi"));
        Assert.Equal("false", Value(profile, "Spoofing", "Vulkan"));
    }

    [Fact]
    public void No_builtin_profile_enables_framegen_by_default()
    {
        foreach (var profile in BuiltinProfiles.All)
        {
            var enabled = Value(profile, "FrameGen", "Enabled");
            Assert.True(enabled is null or "false",
                $"{profile.Id} 默认开启了帧生成（实际值 {enabled}）");
        }
    }

    [Fact]
    public void AmdRdna4Native_avoids_interop_and_therefore_caps_at_fsr31()
    {
        var profile = BuiltinProfiles.AmdRdna4Native;

        Assert.Equal("ffx", Value(profile, "Upscalers", "Dx12Upscaler"));
        Assert.Equal("fsr31", Value(profile, "Upscalers", "Dx11Upscaler"));
        Assert.Equal("fsr22", Value(profile, "Upscalers", "VulkanUpscaler"));
    }

    [Fact]
    public void NvidiaRtx_uses_dlss_output()
    {
        var profile = BuiltinProfiles.NvidiaRtx;

        Assert.Equal("dlss", Value(profile, "Upscalers", "Dx12Upscaler"));
        Assert.Equal("dlss", Value(profile, "Upscalers", "Dx11Upscaler"));
        Assert.Equal("dlssg", Value(profile, "FrameGen", "FGOutput"));
    }

    [Fact]
    public void FrameGenAddOn_sets_hudfix_and_upscaler_input()
    {
        var edits = BuiltinProfiles.FrameGenAddOn(useStreamlineInput: false);

        var lookup = edits.ToDictionary(e => (e.Section, e.Key));
        Assert.Equal("true", lookup[("FrameGen", "Enabled")].NewValue);
        Assert.Equal("upscaler", lookup[("FrameGen", "FGInput")].NewValue);
        Assert.Equal("fsrfg", lookup[("FrameGen", "FGOutput")].NewValue);
        Assert.Equal("0", lookup[("FSR", "FGIndex")].NewValue);
        Assert.Equal("true", lookup[("OptiFG", "HUDFix")].NewValue);
    }

    [Fact]
    public void FrameGenAddOn_uses_dlssg_input_when_game_has_streamline()
    {
        var edits = BuiltinProfiles.FrameGenAddOn(useStreamlineInput: true);

        Assert.Equal("dlssg",
            edits.Single(e => e.Section == "FrameGen" && e.Key == "FGInput").NewValue);
        // dlssg 输入原生支持 Hudless（真 ini L28），不需要 HUDFix；而 HUDFix 自带崩溃风险
        // （L317 "Might cause crashes, especially with Async"），所以这条路径上不应出现它。
        Assert.DoesNotContain(edits, e => e.Section == "OptiFG" && e.Key == "HUDFix");
    }

    [Fact]
    public void All_builtin_ids_are_unique()
    {
        var ids = BuiltinProfiles.All.Select(p => p.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_builtins_are_marked_builtin()
    {
        Assert.All(BuiltinProfiles.All, p => Assert.True(p.IsBuiltin));
    }
}
```

- [x] **Step 2: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter BuiltinProfilesTests`
Expected: 编译错误 `BuiltinProfiles 不存在`

- [x] **Step 3: 写 `Profiles/BuiltinProfiles.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Profiles;

/// <summary>
/// 内置后端策略。核心原则是最小干预（spec §8.2）：只钉死必须确定的键，
/// 其余一律留给 OptiScaler 的 auto 与 per-game quirks。
/// </summary>
public static class BuiltinProfiles
{
    public const string AmdRdna4Id = "amd-rdna4";
    public const string AmdRdna4NativeId = "amd-rdna4-native";
    public const string NvidiaRtxId = "nvidia-rtx";

    /// <summary>RX 9070 XT 主力档。DX11/Vulkan 经 DX12 interop 以吃到 FSR4，代价约 10% 性能。</summary>
    public static UpscalerProfile AmdRdna4 { get; } = new()
    {
        Id = AmdRdna4Id,
        DisplayName = "AMD RDNA4（FSR4，画质优先）",
        Description = "RX 9070 XT 等 RDNA4 显卡。DX11/Vulkan 游戏走 DX12 interop 以启用 FSR4，约有 10% 性能损失。",
        IsBuiltin = true,
        Edits = new[]
        {
            new IniEdit("Upscalers", "Dx12Upscaler", null, "ffx"),
            new IniEdit("Upscalers", "Dx11Upscaler", null, "ffx_12"),
            new IniEdit("Upscalers", "VulkanUpscaler", null, "ffx_12"),
            new IniEdit("FSR", "UpscalerIndex", null, "0"),
            new IniEdit("FSR", "FGIndex", null, "0"),
            new IniEdit("FSR", "Fsr4ForceModel", null, "0"),
            new IniEdit("Spoofing", "Dxgi", null, "false"),
            new IniEdit("Spoofing", "Vulkan", null, "false"),
            new IniEdit("FrameGen", "Enabled", null, "false"),
            new IniEdit("FrameGen", "FGOutput", null, "fsrfg"),
        },
    };

    /// <summary>RDNA4 性能档。DX11/Vulkan 用原生后端，无 interop 损失，但画质上限是 FSR3.1。</summary>
    public static UpscalerProfile AmdRdna4Native { get; } = new()
    {
        Id = AmdRdna4NativeId,
        DisplayName = "AMD RDNA4（原生，性能优先）",
        Description = "DX11 用 FSR3.1、Vulkan 用 FSR2.2 原生后端，无 interop 性能损失；DX12 仍是 FSR4。",
        IsBuiltin = true,
        Edits = new[]
        {
            new IniEdit("Upscalers", "Dx12Upscaler", null, "ffx"),
            new IniEdit("Upscalers", "Dx11Upscaler", null, "fsr31"),
            new IniEdit("Upscalers", "VulkanUpscaler", null, "fsr22"),
            new IniEdit("FSR", "UpscalerIndex", null, "0"),
            new IniEdit("FSR", "FGIndex", null, "0"),
            new IniEdit("FSR", "Fsr4ForceModel", null, "0"),
            new IniEdit("Spoofing", "Dxgi", null, "false"),
            new IniEdit("Spoofing", "Vulkan", null, "false"),
            new IniEdit("FrameGen", "Enabled", null, "false"),
            new IniEdit("FrameGen", "FGOutput", null, "fsrfg"),
        },
    };

    /// <summary>开发机（NVIDIA RTX）自测档。</summary>
    public static UpscalerProfile NvidiaRtx { get; } = new()
    {
        Id = NvidiaRtxId,
        DisplayName = "NVIDIA RTX（DLSS）",
        Description = "输出走 DLSS。帧生成需要 OptiScaler\\streamline\\ 与 nvngx_dlssg.dll，二者不随仓库分发。",
        IsBuiltin = true,
        Edits = new[]
        {
            new IniEdit("Upscalers", "Dx12Upscaler", null, "dlss"),
            new IniEdit("Upscalers", "Dx11Upscaler", null, "dlss"),
            new IniEdit("Upscalers", "VulkanUpscaler", null, "dlss"),
            new IniEdit("Spoofing", "Dxgi", null, "false"),
            new IniEdit("Spoofing", "Vulkan", null, "false"),
            new IniEdit("FrameGen", "Enabled", null, "false"),
            new IniEdit("FrameGen", "FGOutput", null, "dlssg"),
        },
    };

    public static IReadOnlyList<UpscalerProfile> All { get; } = new[] { AmdRdna4, AmdRdna4Native, NvidiaRtx };

    /// <summary>
    /// 帧生成附加档。用户勾选后才叠加到基础 Profile 之上，一律不默认开启。
    /// HUDFix 只在 FGInput=upscaler 时开：那条路径下它是必需的（防 UI 鬼影），
    /// 而 dlssg 输入原生支持 Hudless，开它只增加崩溃风险。依据见方法体注释。
    /// </summary>
    public static IReadOnlyList<IniEdit> FrameGenAddOn(bool useStreamlineInput)
    {
        var edits = new List<IniEdit>
        {
            new("FrameGen", "Enabled", null, "true"),
            new("FrameGen", "FGInput", null, useStreamlineInput ? "dlssg" : "upscaler"),
            new("FrameGen", "FGOutput", null, "fsrfg"),
            new("FSR", "FGIndex", null, "0"),
        };

        // HUDFix 只在 FGInput=upscaler 时才是必需的，而它自带崩溃风险，所以按输入分支决定加不加。
        // 依据是真 OptiScaler.ini 的上游注释（实测行号）：L31 说 upscaler 输入
        // "To prevent UI glitching, Hudfix is required"；L28 说 dlssg 输入
        // "Supports Hudless out of the box"（不需要它）；L317 警告 HUDFix
        // "Might cause crashes, especially with Async"。
        // 在不需要它的路径上开它 = 纯增崩溃风险、零收益，与本项目「部署后健康检查回滚」的
        // 安全模型直接冲突（触发的是回滚，用户看到的是「装了又自己卸了」）。
        if (!useStreamlineInput)
        {
            edits.Add(new IniEdit("OptiFG", "HUDFix", null, "true"));
        }

        return edits;
    }
}
```

**本 Task 引用的全部 12 个 (section, key) 与它们的值已逐个对着上游注释核过（控制器在实施前实测）**。键名与行号：`Upscalers.Dx11Upscaler`(L6) / `Dx12Upscaler`(L11) / `VulkanUpscaler`(L15)、`FSR.UpscalerIndex`(L572) / `FGIndex`(L577) / `Fsr4ForceModel`(L621)、`Spoofing.Dxgi`(L956) / `Vulkan`(L974)、`FrameGen.Enabled`(L25) / `FGInput`(L34) / `FGOutput`(L41)、`OptiFG.HUDFix`(L319)。值域全部合法，依据是每个键上方的上游注释：`Dx11Upscaler` 取 `fsr22, fsr31, xess, xess_12, fsr21_12, fsr22_12, ffx_12, dlss`(L5)、`Dx12Upscaler` 取 `xess, fsr21, fsr22, ffx, dlss`(L9)、`VulkanUpscaler` 取 `fsr21, fsr22, ffx, xess, fsr21_12, ffx_12, dlss`(L14)、`FGInput` 取 `nofg, dlssg, nvngxfg, fsrfg, upscaler, fsrfg30`(L33)、`FGOutput` 取 `nofg, fsrfg, xefg, dlssg`(L40)，余下均为 `true or false` 或整数枚举。关于旧版本这里写的「该 section 有 22 个 HUDFix 相关键」：实测对不上任何口径——`[OptiFG]`（头在 L307）共 **27** 个键，其中键名含 `hudfix`（不分大小写）的有 **16** 个，全文件含 `hudfix` 的行（含注释）是 **23** 行。`HUDFix` 本身精确存于 **L319**。

**为什么 `UpscalerIndex=0` / `FGIndex=0` / `Fsr4ForceModel=0` / `Spoofing.Vulkan=false` 在 RDNA4 上与 `auto` 等效却仍要钉死**：实测上游注释 L571「Default (auto) is 0 - **FSR4 for RDNA4**, FSR3 for the rest」、L576 同、L620「From 0 to 2 - Default (auto) is 0」、L973「Default (auto) is false」——在 9070 XT 上这四个键写 `auto` 得到的就是同样的值。钉死它们的理由**不是**「auto 会选错」，而是**归一化**：被 patch 的 ini 可能来自 `PayloadOrigin.ManualDirectory` 指向的任意目录、也可能被用户手改过，而 `IniPatcher` 是**改写既有键**——profile 不写它就保持用户留下的值。L618 明确警告过这个坑：「Don't fool yourself when trying to force FP8 on actually unsupported GPUs, driver has a FSR 3 fallback」，即手改留下的 `Fsr4ForceModel=1` 会静默降级而不报错。**别按「最小干预」把这几个键当冗余删掉**：最小干预约束的是「不去动与本档无关的键」，不是「不去纠正与本档直接相关的键」。

**为什么 `Spoofing.Dxgi=false` 是有意的行为改变**：L955 实测「Default (auto) is **true for AMD/Intel**, false for Nvidia」，所以在 9070 XT 上 `auto` 等于 `true`（会伪装成 N 卡），而本档写 `false` 与 auto **不等效**。理由是走 FSR4 不需要伪装 N 卡，关掉能少一堆兼容性问题（design.md 已记）。

**已知问题（记给 Task 9 planner 与计划 C 的 UI）**：`FrameGenAddOn` 写 `FrameGen.FGOutput=fsrfg`，而 `NvidiaRtx` 基础档自己写的是 `dlssg`。两者叠加时 `IniPatcher` 的 last-one-wins（实测 `IniPatcher.cs` L60-74：`pending[key] = edit` 用索引器赋值，重复 (Section, Key) **不抛**、后出现的覆盖先出现的、顺序按首次出现保留）会让 AddOn 的 `fsrfg` 胜出。这在 N 卡上技术上可行（FSR FG 的 `amd_fidelityfx_*.dll` 不限显卡），但与基础档的声明矛盾。**现在不参数化 `FGOutput`**：`FrameGenAddOn` 当前没有任何调用方（Task 9 的 planner 才是第一个），此刻并不知道 UI 会不会允许在 `NvidiaRtx` 上勾选帧生成；信息不足时不把签名钉死。计划 C 若确定 N 卡档不提供该勾选，这条自然不可达；若提供，届时给 `FrameGenAddOn` 加一个 `fgOutput` 参数。

若上游改了键名，`IniPatcher` 的行为是**追加**而非报错，游戏里表现为「该项不生效」而不是崩溃——这正是 spec §13 列的上游键名变动缓解措施。上游改版后重新核对用这个（每个键的值域注释就在它自己上方几行，行号见上表）：

```powershell
cd "d:\AI\project\hlyOptiScaler"
$e=New-Object System.Text.UTF8Encoding($false); $L=[System.IO.File]::ReadAllText((Resolve-Path 'OptiScaler.ini'),$e) -split "
"; 315..319 | ForEach-Object { "L$_`: " + $L[$_-1] }
```

- [x] **Step 4: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter BuiltinProfilesTests`
Expected: `失败: 0，通过: 9`

- [x] **Step 5: 写 `Profiles/IProfileStore.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Profiles;

/// <summary>Profile 的持久化。内置档永远存在且不可删；用户档存 profiles.json。</summary>
public interface IProfileStore
{
    /// <summary>内置档 + 用户档。用户档 Id 与内置档冲突时，用户档胜出（允许覆盖内置行为）。</summary>
    IReadOnlyList<UpscalerProfile> All();

    /// <summary>按 Id 查找，找不到返回 null（调用方决定怎么报错）。</summary>
    UpscalerProfile? Find(string id);

    void Save(UpscalerProfile profile);

    /// <summary>删除用户档。内置档返回 false。</summary>
    bool Delete(string id);
}
```

- [x] **Step 6: 写 `JsonProfileStore` 的失败测试**

`tests/Deployer.Core.Tests/JsonProfileStoreTests.cs`：
```csharp
using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace Deployer.Core.Tests;

public class JsonProfileStoreTests
{
    private const string StorePath = @"C:\users\tester\AppData\Roaming\OptiScalerDeployer\profiles.json";

    private static UpscalerProfile Custom(string id) => new()
    {
        Id = id,
        DisplayName = "我的档",
        Description = "desc",
        IsBuiltin = false,
        Edits = new[] { new IniEdit("Upscalers", "Dx12Upscaler", null, "xess") },
    };

    [Fact]
    public void All_returns_builtins_when_store_file_missing()
    {
        var store = new JsonProfileStore(new MockFileSystem(), StorePath);

        Assert.Equal(BuiltinProfiles.All.Count, store.All().Count);
    }

    [Fact]
    public void Save_then_Find_roundtrips()
    {
        var fs = new MockFileSystem();
        new JsonProfileStore(fs, StorePath).Save(Custom("mine"));

        var reloaded = new JsonProfileStore(fs, StorePath).Find("mine");

        Assert.NotNull(reloaded);
        Assert.Equal("我的档", reloaded!.DisplayName);
        Assert.Equal("xess", reloaded.Edits[0].NewValue);
    }

    [Fact]
    public void User_profile_with_builtin_id_overrides_builtin()
    {
        var fs = new MockFileSystem();
        new JsonProfileStore(fs, StorePath).Save(Custom(BuiltinProfiles.AmdRdna4Id));

        var found = new JsonProfileStore(fs, StorePath).Find(BuiltinProfiles.AmdRdna4Id);

        Assert.NotNull(found);
        Assert.False(found!.IsBuiltin);
        Assert.Equal("xess", found.Edits[0].NewValue);
    }

    [Fact]
    public void Delete_removes_user_profile_but_refuses_builtin()
    {
        var fs = new MockFileSystem();
        var store = new JsonProfileStore(fs, StorePath);
        store.Save(Custom("mine"));

        Assert.True(store.Delete("mine"));
        Assert.Null(new JsonProfileStore(fs, StorePath).Find("mine"));
        Assert.False(new JsonProfileStore(fs, StorePath).Delete(BuiltinProfiles.NvidiaRtxId));
    }

    [Fact]
    public void Corrupt_store_file_falls_back_to_builtins_instead_of_throwing()
    {
        var fs = new MockFileSystem();
        fs.AddFile(StorePath, new MockFileData("{ 这不是 JSON"));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.Equal(BuiltinProfiles.All.Count, store.All().Count);
    }
}
```

「损坏的 profiles.json 不能让程序起不来」是硬要求：这个文件在 `%APPDATA%`，用户可能手改过，也可能被同步工具截断。

- [x] **Step 7: 写 `Profiles/JsonProfileStore.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Serialization;

namespace Deployer.Core.Profiles;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly IFileSystem _fs;
    private readonly string _path;
    private List<UpscalerProfile> _userProfiles;

    public JsonProfileStore(IFileSystem fs, string path)
    {
        _fs = fs;
        _path = path;
        _userProfiles = Load();
    }

    /// <summary>默认位置：%APPDATA%\OptiScalerDeployer\profiles.json</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OptiScalerDeployer", "profiles.json");

    public IReadOnlyList<UpscalerProfile> All()
    {
        var userIds = new HashSet<string>(_userProfiles.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var result = new List<UpscalerProfile>(_userProfiles);
        result.AddRange(BuiltinProfiles.All.Where(p => !userIds.Contains(p.Id)));
        return result;
    }

    public UpscalerProfile? Find(string id) =>
        All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Save(UpscalerProfile profile)
    {
        _userProfiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        _userProfiles.Add(profile with { IsBuiltin = false });
        Persist();
    }

    public bool Delete(string id)
    {
        var removed = _userProfiles.RemoveAll(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Persist();
        }

        return removed > 0;
    }

    private List<UpscalerProfile> Load()
    {
        try
        {
            if (!_fs.File.Exists(_path))
            {
                return new List<UpscalerProfile>();
            }

            var json = _fs.File.ReadAllText(_path);
            return DeployerJson.Deserialize<List<UpscalerProfile>>(json)
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToList();
        }
        catch (Exception)
        {
            // 损坏的用户配置不能阻止程序启动，退回内置档
            return new List<UpscalerProfile>();
        }
    }

    private void Persist()
    {
        var dir = _fs.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        _fs.File.WriteAllText(_path, DeployerJson.Serialize(_userProfiles));
    }
}
```

- [x] **Step 8: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 73`

（73 = Task 4 结束时的实测基线 **59** + `BuiltinProfilesTests` **9** + `JsonProfileStoreTests` **5**。上面 Task 3 那个 Step 写的「通过: 33」同样是偏小的推算值，实测是 59——这类累加值只供对照，闸门始终是 `失败: 0`，见本计划末尾的说明。）

- [x] **Step 9: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add builtin upscaler profiles and json profile store"
```

---

## Task 6: Payload 来源 + 测试夹具工厂

Spec §9。本计划只实现 `LocalBuildPayloadSource`（GitHub Releases 属计划 D，需要 SharpCompress 与网络）。

**进度记录（2026-10-09）**：Task 1–6 已实现。Task 5 的 Profile store 包含输入验证、损坏配置恢复提示和数据保护测试；Task 6 提供合成夹具、来源链、本地构建来源和手动目录来源。早期阶段性验证曾为 109 项；最终集成验证见 Task 16。Task 6 Step 4 红阶段与 Step 11 commit 未执行。

**Files:**
- Create: `tests/Deployer.TestData/SyntheticPayloadFactory.cs`
- Create: `src/Deployer.Core/Payload/IPayloadSource.cs`
- Create: `src/Deployer.Core/Payload/LocalBuildPayloadSource.cs`
- Create: `src/Deployer.Core/Payload/PayloadSourceChain.cs`
- Create: `src/Deployer.Core/Payload/ManualDirectoryPayloadSource.cs`
- Test: `tests/Deployer.Core.Tests/LocalBuildPayloadSourceTests.cs`
- Test: `tests/Deployer.Core.Tests/PayloadSourceChainTests.cs`

- [x] **Step 1: 写 `tests/Deployer.TestData/SyntheticPayloadFactory.cs`**

`x64\Release\a\` 在本机尚不存在（已核实 `Test-Path` 为 `False`），所以测试必须能自己造一份。这个工厂同时是「payload 布局」的可执行文档。

```csharp
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.TestData;

/// <summary>
/// 在 IFileSystem（通常是 MockFileSystem）上造一份代表性假 payload，采用 OptiScaler.vcxproj
/// PostBuildEvent 的输出布局。
/// </summary>
public static class SyntheticPayloadFactory
{
    public const string OptiScalerDllName = "OptiScaler.dll";
    public const string IniFileName = "OptiScaler.ini";
    public const string DefaultVersion = "0.9.1.0";

    /// <summary>
    /// 相对 payload 根目录的全部文件。含三个必须被跳过的安装脚本。
    /// </summary>
    public static IReadOnlyList<string> RelativeFiles { get; } = new[]
    {
        OptiScalerDllName,
        IniFileName,
        @"OptiScaler\amd_fidelityfx_loader_dx12.dll",
        @"OptiScaler\amd_fidelityfx_upscaler_dx12.dll",
        @"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll",
        @"OptiScaler\amd_fidelityfx_vk.dll",
        @"OptiScaler\amdxc64.dll",
        @"OptiScaler\libxess.dll",
        @"OptiScaler\Licenses\FidelityFX_v1_LICENSE.md",
        @"OptiScaler\D3D12_OptiScaler\d3d12core.dll",
        @"Licenses\FidelityFX_v2_LICENSE.md",
        @"Licenses\DirectX_LICENSE.txt",
        @"Licenses\XeSS_LICENSE.txt",
        "setup_windows.bat",
        "setup_linux.sh",
        "!! EXTRACT ALL FILES TO GAME FOLDER !!",
    };

    /// <summary>部署时必须跳过的文件（本工具即其替代品）。</summary>
    public static IReadOnlyList<string> SkippedFileNames { get; } = new[]
    {
        "setup_windows.bat", "setup_linux.sh", "!! EXTRACT ALL FILES TO GAME FOLDER !!",
    };

    /// <summary>一段结构仿真、含注释的 OptiScaler.ini，够 IniPatcher 与 planner 用。</summary>
    public static string SampleIniContent { get; } = string.Join("\r\n", new[]
    {
        "; OptiScaler configuration",
        "[Upscalers]",
        "; fsr21, fsr22, ffx (FSR 2.3; 3.1; 4.x), dlss",
        "Dx11Upscaler=auto",
        "Dx12Upscaler=auto",
        "VulkanUpscaler=auto",
        "",
        "[FrameGen]",
        "; nofg, dlssg, nvngxfg, fsrfg, upscaler, fsrfg30",
        "Enabled=auto",
        "FGInput=auto",
        "FGOutput=auto",
        "",
        "[FSR]",
        "; 0 = FSR 4.0.2 | 1 = FSR 3.1.5 | 2 = FSR 2.3.4",
        "UpscalerIndex=auto",
        "FGIndex=auto",
        "Fsr4ForceModel=auto",
        "",
        "[OptiFG]",
        "HUDFix=auto",
        "",
        "[Spoofing]",
        "; 0x10de = Nvidia | 0x8086 = Intel | 0x1002 = AMD",
        "Dxgi=auto",
        "Vulkan=auto",
        "",
        "[Log]",
        "; Default (auto) is false",
        "LogToFile=auto",
        "LogLevel=auto",
        "SingleFile=auto",
        "",
        "[ProcessFilter]",
        "; Default (auto) is disabled (injects into all processes)",
        "TargetProcessName=auto",
        "ProcessExclusionList=auto",
        "",
    });

    public static string Create(MockFileSystem fs, string payloadRoot, string version = DefaultVersion)
    {
        foreach (var relative in RelativeFiles)
        {
            var full = fs.Path.Combine(payloadRoot, relative);
            var content = relative == IniFileName
                ? SampleIniContent
                : $"SYNTHETIC::{relative}::v{version}";
            fs.AddFile(full, new MockFileData(Encoding.UTF8.GetBytes(content)));
        }

        return payloadRoot;
    }

    /// <summary>让 FakeFileVersionReader 能把合成 OptiScaler.dll 报出指定版本。</summary>
    public static FakeFileVersionReader CreateVersionReader(string payloadRoot, IFileSystem fs, string version = DefaultVersion)
    {
        var reader = new FakeFileVersionReader();
        reader.SetFileVersion(fs.Path.Combine(payloadRoot, OptiScalerDllName), version);
        return reader;
    }
}

/// <summary>测试替身。真实实现读 PE 资源，无法作用于 MockFileSystem。</summary>
public sealed class FakeFileVersionReader : IFileVersionReader
{
    private readonly Dictionary<string, string?> _original = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _version = new(StringComparer.OrdinalIgnoreCase);

    public void SetOriginalFilename(string path, string? value) => _original[path] = value;

    public void SetFileVersion(string path, string? value) => _version[path] = value;

    public string? GetOriginalFilename(string path) =>
        _original.TryGetValue(path, out var v) ? v : null;

    public string? GetFileVersion(string path) =>
        _version.TryGetValue(path, out var v) ? v : null;
}
```

`FakeFileVersionReader` 放在 TestData 而不是 Tests 项目里，因为计划 B 的 `UpscalerSdkDetector` 测试也要用它。

- [x] **Step 2: 写 `Payload/IPayloadSource.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Payload;

public interface IPayloadSource
{
    /// <summary>给用户看的一句来源说明，失败时汇总进异常消息。</summary>
    string Description { get; }

    /// <summary>可用则返回 bundle，不可用返回 null（不抛异常——链路要继续试下一个）。</summary>
    Task<PayloadBundle?> TryGetAsync(CancellationToken ct);
}
```

- [x] **Step 3: 写 `LocalBuildPayloadSource` 的失败测试**

`tests/Deployer.Core.Tests/LocalBuildPayloadSourceTests.cs`：
```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;
using Deployer.Core.Payload;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class LocalBuildPayloadSourceTests
{
    private const string RepoRoot = @"D:\repo\hlyOptiScaler";
    private static readonly string PayloadRoot = $@"{RepoRoot}\x64\Release\a";

    private static (LocalBuildPayloadSource source, MockFileSystem fs) Build(bool withPayload)
    {
        var fs = new MockFileSystem();
        if (withPayload)
        {
            SyntheticPayloadFactory.Create(fs, PayloadRoot);
        }

        var versions = withPayload
            ? SyntheticPayloadFactory.CreateVersionReader(PayloadRoot, fs)
            : new FakeFileVersionReader();

        return (new LocalBuildPayloadSource(RepoRoot, fs, versions), fs);
    }

    [Fact]
    public async Task Returns_bundle_when_local_build_exists()
    {
        var (source, _) = Build(withPayload: true);

        var bundle = await source.TryGetAsync(CancellationToken.None);

        Assert.NotNull(bundle);
        Assert.Equal(PayloadRoot, bundle!.RootDirectory);
        Assert.Equal(PayloadOrigin.LocalBuild, bundle.Origin);
        Assert.Equal(SyntheticPayloadFactory.DefaultVersion, bundle.Version);
    }

    [Fact]
    public async Task Returns_null_when_release_folder_missing()
    {
        var (source, _) = Build(withPayload: false);

        Assert.Null(await source.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_when_optiscaler_dll_missing()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, PayloadRoot);
        fs.File.Delete($@"{PayloadRoot}\OptiScaler.dll");
        var source = new LocalBuildPayloadSource(RepoRoot, fs, new FakeFileVersionReader());

        Assert.Null(await source.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_when_ini_missing()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, PayloadRoot);
        fs.File.Delete($@"{PayloadRoot}\OptiScaler.ini");
        var source = new LocalBuildPayloadSource(RepoRoot, fs, new FakeFileVersionReader());

        Assert.Null(await source.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Falls_back_to_unknown_version_when_dll_has_no_version_resource()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, PayloadRoot);
        var source = new LocalBuildPayloadSource(RepoRoot, fs, new FakeFileVersionReader());

        var bundle = await source.TryGetAsync(CancellationToken.None);

        Assert.Equal("unknown", bundle!.Version);
    }

    [Fact]
    public void Description_mentions_the_expected_path()
    {
        var (source, _) = Build(withPayload: false);

        Assert.Contains("x64", source.Description, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter LocalBuildPayloadSourceTests`
Expected: 编译错误 `LocalBuildPayloadSource 不存在`

- [x] **Step 5: 写 `Payload/LocalBuildPayloadSource.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.Payload;

/// <summary>
/// 读 <c>&lt;repoRoot&gt;\x64\Release\a\</c> —— 即 OptiScaler.vcxproj PostBuildEvent 的输出目录。
/// 本地构建优先的意义：能验证自己对 OptiScaler C++ 源码的修改（spec §9）。
/// </summary>
public sealed class LocalBuildPayloadSource : IPayloadSource
{
    private const string RelativePayloadPath = @"x64\Release\a";

    private readonly string _repoRoot;
    private readonly IFileSystem _fs;
    private readonly IFileVersionReader _versions;

    public LocalBuildPayloadSource(string repoRoot, IFileSystem fs, IFileVersionReader versions)
    {
        _repoRoot = repoRoot;
        _fs = fs;
        _versions = versions;
    }

    public string PayloadRoot => _fs.Path.GetFullPath(_fs.Path.Combine(_repoRoot, RelativePayloadPath));

    public string Description => $"本地构建 {PayloadRoot}";

    public Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var root = PayloadRoot;
        var dll = _fs.Path.Combine(root, "OptiScaler.dll");
        var ini = _fs.Path.Combine(root, "OptiScaler.ini");

        if (!_fs.Directory.Exists(root) || !_fs.File.Exists(dll) || !_fs.File.Exists(ini))
        {
            return Task.FromResult<PayloadBundle?>(null);
        }

        var version = _versions.GetFileVersion(dll) ?? "unknown";
        return Task.FromResult<PayloadBundle?>(new PayloadBundle(root, version, PayloadOrigin.LocalBuild));
    }
}
```

- [x] **Step 6: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter LocalBuildPayloadSourceTests`
Expected: `失败: 0，通过: 6`

- [x] **Step 7: 写 `Payload/ManualDirectoryPayloadSource.cs`**

用户手动指定任意目录（spec §10「Payload 缺失」的第二条出路）。它比 LocalBuild 宽松：只校验两个必需文件，不假设目录名。

```csharp
using System.IO.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.Payload;

public sealed class ManualDirectoryPayloadSource : IPayloadSource
{
    private readonly string _directory;
    private readonly IFileSystem _fs;
    private readonly IFileVersionReader _versions;

    public ManualDirectoryPayloadSource(string directory, IFileSystem fs, IFileVersionReader versions)
    {
        _directory = directory;
        _fs = fs;
        _versions = versions;
    }

    public string Description => $"手动指定目录 {_directory}";

    public Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dll = _fs.Path.Combine(_directory, "OptiScaler.dll");
        var ini = _fs.Path.Combine(_directory, "OptiScaler.ini");

        if (!_fs.File.Exists(dll) || !_fs.File.Exists(ini))
        {
            return Task.FromResult<PayloadBundle?>(null);
        }

        var version = _versions.GetFileVersion(dll) ?? "unknown";
        return Task.FromResult<PayloadBundle?>(
            new PayloadBundle(_directory, version, PayloadOrigin.ManualDirectory));
    }
}
```

- [x] **Step 8: 写 `Payload/PayloadSourceChain.cs`**

```csharp
using Deployer.Core.Domain;
using Deployer.Core.Errors;

namespace Deployer.Core.Payload;

/// <summary>按顺序尝试各来源，第一个可用的胜出；全不可用则抛 PayloadNotFoundException。</summary>
public sealed class PayloadSourceChain : IPayloadSource
{
    private readonly IReadOnlyList<IPayloadSource> _sources;

    public PayloadSourceChain(IReadOnlyList<IPayloadSource> sources)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("payload 来源链不能为空", nameof(sources));
        }

        _sources = sources;
    }

    public string Description => string.Join(" → ", _sources.Select(s => s.Description));

    public async Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
    {
        foreach (var source in _sources)
        {
            var bundle = await source.TryGetAsync(ct).ConfigureAwait(false);
            if (bundle is not null)
            {
                return bundle;
            }
        }

        return null;
    }

    /// <summary>TryGetAsync 的强约束版：拿不到就抛，带全部来源的诊断信息。</summary>
    public async Task<PayloadBundle> GetOrThrowAsync(CancellationToken ct)
    {
        var bundle = await TryGetAsync(ct).ConfigureAwait(false);
        if (bundle is not null)
        {
            return bundle;
        }

        throw new PayloadNotFoundException(
            "已尝试的来源：" + string.Join(" | ", _sources.Select(s => s.Description)));
    }
}
```

`TryGetAsync` 与 `GetOrThrowAsync` 并存是有意的：planner 用后者（拿不到 payload 就没法继续），UI 的「设置页探测」用前者（要显示每个来源的可用状态而不是弹异常）。

- [x] **Step 9: 写 `PayloadSourceChainTests.cs`**

```csharp
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Payload;
using Xunit;

namespace Deployer.Core.Tests;

public class PayloadSourceChainTests
{
    private sealed class StubSource : IPayloadSource
    {
        public StubSource(string description, PayloadBundle? result) =>
            (Description, Result) = (description, result);

        public string Description { get; }
        public PayloadBundle? Result { get; }
        public int Calls { get; private set; }

        public Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private static PayloadBundle Bundle(string root) => new(root, "1.0", PayloadOrigin.LocalBuild);

    [Fact]
    public async Task First_available_source_wins_and_later_ones_are_not_called()
    {
        var first = new StubSource("A", null);
        var second = new StubSource("B", Bundle(@"C:\b"));
        var third = new StubSource("C", Bundle(@"C:\c"));
        var chain = new PayloadSourceChain(new IPayloadSource[] { first, second, third });

        var bundle = await chain.GetOrThrowAsync(CancellationToken.None);

        Assert.Equal(@"C:\b", bundle.RootDirectory);
        Assert.Equal(0, third.Calls);
    }

    [Fact]
    public async Task GetOrThrow_lists_every_source_in_the_diagnostic()
    {
        var chain = new PayloadSourceChain(new IPayloadSource[]
        {
            new StubSource("本地构建 D:\\repo\\x64\\Release\\a", null),
            new StubSource("手动指定目录 D:\\downloads\\opti", null),
        });

        var ex = await Assert.ThrowsAsync<PayloadNotFoundException>(
            () => chain.GetOrThrowAsync(CancellationToken.None));

        Assert.Contains("本地构建", ex.Diagnostic);
        Assert.Contains("手动指定", ex.Diagnostic);
        Assert.Contains("VS2022", ex.UserMessage);
    }

    [Fact]
    public async Task TryGetAsync_returns_null_instead_of_throwing()
    {
        var chain = new PayloadSourceChain(new IPayloadSource[] { new StubSource("A", null) });

        Assert.Null(await chain.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public void Empty_chain_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new PayloadSourceChain(Array.Empty<IPayloadSource>()));
    }

    [Fact]
    public void Description_joins_sources_with_arrow()
    {
        var chain = new PayloadSourceChain(new IPayloadSource[]
        {
            new StubSource("A", null), new StubSource("B", null),
        });

        Assert.Equal("A → B", chain.Description);
    }
}
```

- [x] **Step 10: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 41`

- [ ] **Step 11: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add payload sources and synthetic payload fixture"
```

---

## Task 7: DLL 身份决策

**当前状态（2026-10-09）**：已实现并通过集成定向验证。实现/测试见 [DllIdentitySelector.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Identity/DllIdentitySelector.cs) 与 [DllIdentitySelectorTests.cs](../../../tools/OptiScalerDeployer/tests/Deployer.Core.Tests/DllIdentitySelectorTests.cs)；下方原始逐步代码、Expected 计数和未勾选红阶段仅作历史记录，当前结果以 Task 16 为准。

Spec §6。这是「不损坏游戏」的关键闸门：官方 `setup_windows.bat` 在目标 DLL 已存在时只问一句就 `del /F`，不备份（L188-206、L379-382），游戏自带的 `version.dll` / `winmm.dll` 被永久删除——这是「装完游戏起不来」的典型成因。

**对 Task 2 的一处类型修正**：`IdentityDecision` 必须多带一个 `BackupReason? ExistingFileReason`，否则 planner 无法区分「覆盖上次装的 OptiScaler」与「覆盖游戏自带文件」，只能自己重读一遍 `OriginalFilename`——那就是把判定逻辑拆成两份。修改 `src/Deployer.Core/Domain/DeploymentPlan.cs` 里的定义：

```csharp
public sealed record IdentityDecision(
    DllIdentity Chosen,
    string FileName,
    IdentityStatus Status,
    IReadOnlyList<DllIdentity> SkippedDueToConflict,
    BackupReason? ExistingFileReason,
    string? UserPromptMessage);
```

`ExistingFileReason` 为 `null` 表示该位置本来是空的（纯新增）。同时把 Task 3 Step 7 测试里构造 `IdentityDecision` 的那一行改为：

```csharp
            Identity = new IdentityDecision(
                DllIdentity.Winmm, "winmm.dll", IdentityStatus.Resolved,
                new[] { DllIdentity.Dxgi }, null, null),
```

**Files:**
- Modify: `src/Deployer.Core/Domain/DeploymentPlan.cs`（`IdentityDecision` 加一个参数）
- Modify: `tests/Deployer.Core.Tests/DeployerJsonTests.cs`（跟上构造）
- Create: `src/Deployer.Core/Identity/IDllIdentitySelector.cs`
- Create: `src/Deployer.Core/Identity/DllIdentitySelector.cs`
- Test: `tests/Deployer.Core.Tests/DllIdentitySelectorTests.cs`

- [ ] **Step 1: 改 `IdentityDecision` 并重跑已有测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: 先 FAIL（构造参数不匹配），改完两处后 `失败: 0，通过: 41`

- [ ] **Step 2: 写 `Identity/IDllIdentitySelector.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Identity;

/// <summary>身份选择的全部输入。不直接读文件系统以外的任何东西。</summary>
public sealed record IdentityContext(
    GamePlatform Platform,
    bool IsVulkanOnly,
    string DeployDirectory,
    bool AllowOverwriteGameFile,
    bool PreferAsiIdentity);

public interface IDllIdentitySelector
{
    IdentityDecision Select(IdentityContext context);
}
```

- [ ] **Step 3: 写 `DllIdentitySelectorTests.cs`（全分支）**

`tests/Deployer.Core.Tests/DllIdentitySelectorTests.cs`：
```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class DllIdentitySelectorTests
{
    private const string Dir = @"C:\game\Binaries\Win64";

    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();

    private DllIdentitySelector Selector() => new(_fs, _versions);

    private IdentityContext Context(
        GamePlatform platform = GamePlatform.Steam,
        bool vulkanOnly = false,
        bool allowOverwrite = false,
        bool preferAsi = false) =>
        new(platform, vulkanOnly, Dir, allowOverwrite, preferAsi);

    /// <summary>造一个游戏自带的 DLL（无版本资源，或 OriginalFilename 不是 OptiScaler.dll）。</summary>
    private void AddGameOwnDll(DllIdentity identity, string? originalFilename = null)
    {
        var path = _fs.Path.Combine(Dir, identity.FileName());
        _fs.AddFile(path, new MockFileData("GAME OWN BINARY"));
        if (originalFilename is not null)
        {
            _versions.SetOriginalFilename(path, originalFilename);
        }
    }

    /// <summary>造一个上次部署留下的 OptiScaler。</summary>
    private void AddPreviousOptiScaler(DllIdentity identity)
    {
        var path = _fs.Path.Combine(Dir, identity.FileName());
        _fs.AddFile(path, new MockFileData("OPTISCALER BINARY"));
        _versions.SetOriginalFilename(path, "OptiScaler.dll");
    }

    [Fact]
    public void Empty_directory_on_steam_picks_dxgi()
    {
        var decision = Selector().Select(Context(GamePlatform.Steam));

        Assert.Equal(DllIdentity.Dxgi, decision.Chosen);
        Assert.Equal("dxgi.dll", decision.FileName);
        Assert.Equal(IdentityStatus.Resolved, decision.Status);
        Assert.Null(decision.ExistingFileReason);
        Assert.Empty(decision.SkippedDueToConflict);
    }

    [Fact]
    public void Xbox_platform_prefers_winmm_over_dxgi()
    {
        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Winmm, decision.Chosen);
    }

    [Fact]
    public void Vulkan_only_game_prefers_winmm_even_on_steam()
    {
        var decision = Selector().Select(Context(GamePlatform.Steam, vulkanOnly: true));

        Assert.Equal(DllIdentity.Winmm, decision.Chosen);
    }

    [Fact]
    public void Game_own_dll_is_skipped_and_next_candidate_is_chosen()
    {
        AddGameOwnDll(DllIdentity.Winmm, "MyGameAudio.dll");

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Version, decision.Chosen);
        Assert.Equal(new[] { DllIdentity.Winmm }, decision.SkippedDueToConflict);
        Assert.Contains("winmm", decision.UserPromptMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dll_without_version_resource_is_treated_as_game_own()
    {
        AddGameOwnDll(DllIdentity.Winmm, originalFilename: null);

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Version, decision.Chosen);
    }

    [Fact]
    public void Previous_optiscaler_install_is_reused_silently()
    {
        AddPreviousOptiScaler(DllIdentity.Winmm);

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Winmm, decision.Chosen);
        Assert.Equal(IdentityStatus.Resolved, decision.Status);
        Assert.Equal(BackupReason.OverwritePreviousOpti, decision.ExistingFileReason);
        Assert.Empty(decision.SkippedDueToConflict);
    }

    [Fact]
    public void OriginalFilename_match_is_case_insensitive()
    {
        AddPreviousOptiScaler(DllIdentity.Winmm);
        _versions.SetOriginalFilename(_fs.Path.Combine(Dir, "winmm.dll"), "optiscaler.DLL");

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(BackupReason.OverwritePreviousOpti, decision.ExistingFileReason);
    }

    [Fact]
    public void All_candidates_conflicting_asks_user_and_keeps_highest_priority_as_provisional()
    {
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            AddGameOwnDll(identity, "GameFile.dll");
        }

        var decision = Selector().Select(Context(GamePlatform.Xbox, allowOverwrite: false));

        Assert.Equal(IdentityStatus.RequiresUserDecision, decision.Status);
        Assert.Equal(DllIdentity.Winmm, decision.Chosen);
        Assert.Equal(BackupReason.OverwriteGameFile, decision.ExistingFileReason);
        Assert.Equal(DllIdentitySelector.XboxPriority.Count, decision.SkippedDueToConflict.Count);
        Assert.NotNull(decision.UserPromptMessage);
    }

    [Fact]
    public void AllowOverwrite_turns_a_full_conflict_into_a_resolved_decision()
    {
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            AddGameOwnDll(identity, "GameFile.dll");
        }

        var decision = Selector().Select(Context(GamePlatform.Xbox, allowOverwrite: true));

        Assert.Equal(IdentityStatus.Resolved, decision.Status);
        Assert.Equal(DllIdentity.Winmm, decision.Chosen);
        Assert.Equal(BackupReason.OverwriteGameFile, decision.ExistingFileReason);
    }

    [Fact]
    public void PreferAsi_bypasses_the_dll_table_entirely()
    {
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            AddGameOwnDll(identity, "GameFile.dll");
        }

        var decision = Selector().Select(Context(GamePlatform.Xbox, preferAsi: true));

        Assert.Equal(DllIdentity.Asi, decision.Chosen);
        Assert.Equal("OptiScaler.asi", decision.FileName);
        Assert.Equal(IdentityStatus.Resolved, decision.Status);
        Assert.Empty(decision.SkippedDueToConflict);
    }

    [Fact]
    public void PreferAsi_still_detects_a_conflicting_existing_asi_file()
    {
        _fs.AddFile(_fs.Path.Combine(Dir, "OptiScaler.asi"), new MockFileData("SOME OTHER ASI"));
        _versions.SetOriginalFilename(_fs.Path.Combine(Dir, "OptiScaler.asi"), "OtherMod.asi");

        var decision = Selector().Select(Context(GamePlatform.Xbox, preferAsi: true));

        Assert.Equal(IdentityStatus.RequiresUserDecision, decision.Status);
        Assert.Equal(BackupReason.OverwriteGameFile, decision.ExistingFileReason);
    }

    [Theory]
    [InlineData(GamePlatform.Xbox, DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dxgi)]
    [InlineData(GamePlatform.Steam, DllIdentity.Dxgi, DllIdentity.Winmm, DllIdentity.Version)]
    public void Priority_tables_start_with_the_documented_first_choice(
        GamePlatform platform, DllIdentity first, DllIdentity second, DllIdentity alsoPresent)
    {
        var table = DllIdentitySelector.PriorityFor(platform, isVulkanOnly: false);

        Assert.Equal(first, table[0]);
        Assert.Equal(second, table[1]);
        Assert.Contains(alsoPresent, table);
    }

    [Fact]
    public void Vulkan_only_table_excludes_d3d12()
    {
        var table = DllIdentitySelector.PriorityFor(GamePlatform.Steam, isVulkanOnly: true);

        Assert.DoesNotContain(DllIdentity.D3d12, table);
        Assert.Equal(DllIdentity.Winmm, table[0]);
    }

    [Fact]
    public void Every_priority_table_has_no_duplicates()
    {
        foreach (var platform in Enum.GetValues<GamePlatform>())
        {
            foreach (var vulkan in new[] { false, true })
            {
                var table = DllIdentitySelector.PriorityFor(platform, vulkan);
                Assert.Equal(table.Count, table.Distinct().Count());
                Assert.NotEmpty(table);
            }
        }
    }
}
```

- [ ] **Step 4: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter DllIdentitySelectorTests`
Expected: 编译错误 `DllIdentitySelector 不存在`

- [ ] **Step 5: 写 `Identity/DllIdentitySelector.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.Identity;

public sealed class DllIdentitySelector : IDllIdentitySelector
{
    /// <summary>OptiScaler.dll 的 OriginalFilename。与官方 BAT 的 PowerShell 判定口径一致。</summary>
    private const string OptiScalerOriginalFilename = "OptiScaler.dll";

    /// <summary>spec §6：Xbox / GDK 优先级（源自官方 BAT：XGP/MS Store 用 winmm/version 更好）。</summary>
    public static IReadOnlyList<DllIdentity> XboxPriority { get; } = new[]
    {
        DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp,
        DllIdentity.Wininet, DllIdentity.Winhttp, DllIdentity.Dxgi, DllIdentity.D3d12,
    };

    /// <summary>spec §6：纯 Vulkan 游戏（官方 BAT：For Vulkan, use winmm.dll）。</summary>
    public static IReadOnlyList<DllIdentity> VulkanPriority { get; } = new[]
    {
        DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp, DllIdentity.Dxgi,
    };

    /// <summary>spec §6：默认 DX11/DX12（官方 BAT：默认 dxgi.dll 兼容性最好）。</summary>
    public static IReadOnlyList<DllIdentity> DefaultPriority { get; } = new[]
    {
        DllIdentity.Dxgi, DllIdentity.Winmm, DllIdentity.Version,
        DllIdentity.Dbghelp, DllIdentity.D3d12,
    };

    private readonly IFileSystem _fs;
    private readonly IFileVersionReader _versions;

    public DllIdentitySelector(IFileSystem fs, IFileVersionReader versions)
    {
        _fs = fs;
        _versions = versions;
    }

    public static IReadOnlyList<DllIdentity> PriorityFor(GamePlatform platform, bool isVulkanOnly)
    {
        if (isVulkanOnly)
        {
            return VulkanPriority;
        }

        return platform == GamePlatform.Xbox ? XboxPriority : DefaultPriority;
    }

    public IdentityDecision Select(IdentityContext context)
    {
        if (context.PreferAsiIdentity)
        {
            return EvaluateSingle(context, DllIdentity.Asi);
        }

        var table = PriorityFor(context.Platform, context.IsVulkanOnly);
        var skipped = new List<DllIdentity>();

        foreach (var candidate in table)
        {
            var verdict = Classify(context.DeployDirectory, candidate);

            switch (verdict)
            {
                case SlotVerdict.Free:
                    return Resolved(candidate, skipped, existingReason: null, prompt: null);

                case SlotVerdict.PreviousOptiScaler:
                    return Resolved(candidate, skipped, BackupReason.OverwritePreviousOpti, prompt: null);

                case SlotVerdict.GameOwnFile:
                    skipped.Add(candidate);
                    break;
            }
        }

        // 全部候选都与游戏自带文件冲突
        var fallback = table[0];
        var prompt = BuildConflictPrompt(context.DeployDirectory, skipped);

        return context.AllowOverwriteGameFile
            ? Resolved(fallback, skipped, BackupReason.OverwriteGameFile, prompt)
            : new IdentityDecision(
                fallback, fallback.FileName(), IdentityStatus.RequiresUserDecision,
                skipped, BackupReason.OverwriteGameFile, prompt);
    }

    private IdentityDecision EvaluateSingle(IdentityContext context, DllIdentity identity)
    {
        var verdict = Classify(context.DeployDirectory, identity);

        return verdict switch
        {
            SlotVerdict.Free => Resolved(identity, EmptySkipped, existingReason: null, prompt: null),
            SlotVerdict.PreviousOptiScaler =>
                Resolved(identity, EmptySkipped, BackupReason.OverwritePreviousOpti, prompt: null),
            _ => new IdentityDecision(
                identity, identity.FileName(),
                context.AllowOverwriteGameFile ? IdentityStatus.Resolved : IdentityStatus.RequiresUserDecision,
                EmptySkipped, BackupReason.OverwriteGameFile,
                $"{identity.FileName()} 已存在且不属于 OptiScaler，覆盖前会先备份到影子目录。"),
        };
    }

    private static readonly IReadOnlyList<DllIdentity> EmptySkipped = Array.Empty<DllIdentity>();

    private SlotVerdict Classify(string deployDirectory, DllIdentity candidate)
    {
        var path = _fs.Path.Combine(deployDirectory, candidate.FileName());
        if (!_fs.File.Exists(path))
        {
            return SlotVerdict.Free;
        }

        var originalFilename = _versions.GetOriginalFilename(path);
        return string.Equals(originalFilename, OptiScalerOriginalFilename, StringComparison.OrdinalIgnoreCase)
            ? SlotVerdict.PreviousOptiScaler
            : SlotVerdict.GameOwnFile;
    }

    private static IdentityDecision Resolved(
        DllIdentity chosen,
        IReadOnlyList<DllIdentity> skipped,
        BackupReason? existingReason,
        string? prompt)
    {
        var message = prompt;
        if (message is null && skipped.Count > 0)
        {
            message = $"因与游戏自带文件冲突，已跳过 {string.Join("、", skipped.Select(s => s.FileName()))}，"
                    + $"改用 {chosen.FileName()}。";
        }

        return new IdentityDecision(
            chosen, chosen.FileName(), IdentityStatus.Resolved, skipped, existingReason, message);
    }

    private static string BuildConflictPrompt(string deployDirectory, IReadOnlyList<DllIdentity> skipped)
    {
        var names = string.Join("、", skipped.Select(s => s.FileName()));
        return $"{deployDirectory} 里的 {names} 全部是游戏自带文件。OptiScaler 的 proxy 会把调用转发到 "
             + "System32 的原生 DLL，而不是游戏本地同名 DLL，因此覆盖后游戏自定义的导出符号会丢失。"
             + "选择：(a) 覆盖并备份（可从影子备份还原）；(b) 改用 OptiScaler.asi（需游戏侧已有 ASI Loader，一般没有，不推荐）；(c) 取消。";
    }

    private enum SlotVerdict { Free, PreviousOptiScaler, GameOwnFile }
}
```

`BuildConflictPrompt` 里那句「转发到 System32 而非游戏本地 DLL」不是装饰——它是用户做决定时唯一真正需要的信息，也是官方 BAT 从不告知的事（spec §1.3 最后一条）。

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter DllIdentitySelectorTests`
Expected: `失败: 0，通过: 16`

- [ ] **Step 7: 全量跑**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 57`

- [ ] **Step 8: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add DLL identity selector with conflict detection"
```

---

## Task 8: 指纹计算与 payload 遍历

**当前状态（2026-10-09）**：已实现并通过集成定向验证。当前实现/测试以源码和 Task 16 的最终验证为准；下方原始逐步片段与阶段计数仅作历史记录。

**Files:**
- Create: `src/Deployer.Core/Planning/IFingerprintCalculator.cs`
- Create: `src/Deployer.Core/Planning/FingerprintCalculator.cs`
- Create: `src/Deployer.Core/Planning/PayloadFile.cs`
- Create: `src/Deployer.Core/Planning/PayloadWalker.cs`
- Test: `tests/Deployer.Core.Tests/FingerprintCalculatorTests.cs`
- Test: `tests/Deployer.Core.Tests/PayloadWalkerTests.cs`

- [ ] **Step 1: 写 `Planning/IFingerprintCalculator.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Planning;

public interface IFingerprintCalculator
{
    /// <summary>
    /// SHA256("&lt;平台&gt;|&lt;PackageFamilyName 或 InstallRoot 绝对路径&gt;|&lt;exe 名&gt;") 的前 8 位十六进制小写。
    /// 输入统一小写，路径经 GetFullPath 规范化，保证同一游戏跳扫描轮次得到稳定值。
    /// </summary>
    string Compute(GamePlatform platform, string? packageFamilyName, string installRoot, string executableName);
}
```

- [ ] **Step 2: 写 `FingerprintCalculatorTests.cs`**

```csharp
using Deployer.Core.Domain;
using Deployer.Core.Planning;
using Xunit;

namespace Deployer.Core.Tests;

public class FingerprintCalculatorTests
{
    private readonly FingerprintCalculator _calc = new();

    [Fact]
    public void Returns_8_lowercase_hex_chars()
    {
        var fp = _calc.Compute(GamePlatform.Xbox, "Microsoft.25438_8wekyb3d8bbwe!App",
            @"C:\XboxGames\Halo Infinite\content", "HaloInfinite-WinGDK-Shipping.exe");

        Assert.Equal(8, fp.Length);
        Assert.Matches("^[0-9a-f]{8}$", fp);
    }

    [Fact]
    public void Is_stable_across_calls()
    {
        var a = _calc.Compute(GamePlatform.Steam, null, @"D:\Steam\steamapps\common\Game", "game.exe");
        var b = _calc.Compute(GamePlatform.Steam, null, @"D:\Steam\steamapps\common\Game", "game.exe");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Ignores_case_and_trailing_separator_in_path()
    {
        var a = _calc.Compute(GamePlatform.Steam, null, @"D:\Games\Foo", "foo.exe");
        var b = _calc.Compute(GamePlatform.Steam, null, @"d:\games\foo\", "FOO.EXE");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Prefers_package_family_name_over_install_root()
    {
        var byPfn = _calc.Compute(GamePlatform.Xbox, "PFN.A", @"C:\XboxGames\A\content", "a.exe");
        var byPfnDifferentRoot = _calc.Compute(GamePlatform.Xbox, "PFN.A", @"D:\Elsewhere\A", "a.exe");
        var byOtherPfn = _calc.Compute(GamePlatform.Xbox, "PFN.B", @"C:\XboxGames\A\content", "a.exe");

        Assert.Equal(byPfn, byPfnDifferentRoot);
        Assert.NotEqual(byPfn, byOtherPfn);
    }

    [Fact]
    public void Different_platform_with_same_path_yields_different_fingerprint()
    {
        var xbox = _calc.Compute(GamePlatform.Xbox, null, @"C:\Games\A", "a.exe");
        var manual = _calc.Compute(GamePlatform.Manual, null, @"C:\Games\A", "a.exe");

        Assert.NotEqual(xbox, manual);
    }

    [Fact]
    public void Different_exe_in_same_folder_yields_different_fingerprint()
    {
        var shipping = _calc.Compute(GamePlatform.Steam, null, @"C:\Games\A", "A-Win64-Shipping.exe");
        var launcher = _calc.Compute(GamePlatform.Steam, null, @"C:\Games\A", "ALauncher.exe");

        Assert.NotEqual(shipping, launcher);
    }

    [Fact]
    public void Rejects_blank_inputs()
    {
        Assert.Throws<ArgumentException>(
            () => _calc.Compute(GamePlatform.Steam, null, "  ", "a.exe"));
        Assert.Throws<ArgumentException>(
            () => _calc.Compute(GamePlatform.Steam, null, @"C:\Games\A", ""));
    }
}
```

「不同 exe 得到不同指纹」是用户「每个游戏单独注入」要求在数据层的落地点：同一 UE 游戏目录下常有 `Launcher.exe` 与 `Game-Win64-Shipping.exe`，它们是**两个独立的台账条目**，卸载一个绕不可能碰到另一个（spec §8.4）。

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter FingerprintCalculatorTests`
Expected: 编译错误

- [ ] **Step 4: 写 `Planning/FingerprintCalculator.cs`**

```csharp
using Deployer.Core.Domain;
using Deployer.Core.Util;

namespace Deployer.Core.Planning;

public sealed class FingerprintCalculator : IFingerprintCalculator
{
    public string Compute(
        GamePlatform platform, string? packageFamilyName, string installRoot, string executableName)
    {
        if (string.IsNullOrWhiteSpace(installRoot) && string.IsNullOrWhiteSpace(packageFamilyName))
        {
            throw new ArgumentException("installRoot 与 packageFamilyName 不能同时为空", nameof(installRoot));
        }

        if (string.IsNullOrWhiteSpace(executableName))
        {
            throw new ArgumentException("executableName 不能为空", nameof(executableName));
        }

        var identity = string.IsNullOrWhiteSpace(packageFamilyName)
            ? NormalizePath(installRoot)
            : packageFamilyName.Trim().ToLowerInvariant();

        var input = $"{platform.ToString().ToLowerInvariant()}|{identity}|{executableName.Trim().ToLowerInvariant()}";
        return Hashing.ShortHash(input, 8);
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.ToLowerInvariant();
    }
}
```

注意：`Path.GetFullPath` 在 `Core`（net8.0）里是跨平台的纯字符串运算，不碰磁盘，所以可以直接用而不必经 `IFileSystem`——这也是为什么测试里造 `@"D:\Steam\..."` 这种在 Linux 上不存在的路径也能跑。

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter FingerprintCalculatorTests`
Expected: `失败: 0，通过: 7`

- [ ] **Step 6: 写 `Planning/PayloadFile.cs`**

```csharp
namespace Deployer.Core.Planning;

/// <summary>payload 里的一个待部署文件。RelativePath 相对于 payload 根目录。</summary>
public sealed record PayloadFile(string RelativePath, string FullPath, long Length, string Sha256);
```

- [ ] **Step 7: 写 `PayloadWalkerTests.cs`**

```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Planning;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class PayloadWalkerTests
{
    private const string Root = @"D:\repo\x64\Release\a";

    private static (PayloadWalker walker, MockFileSystem fs) Build()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, Root);
        return (new PayloadWalker(fs), fs);
    }

    [Fact]
    public void Skips_the_three_installer_artifacts()
    {
        var (walker, _) = Build();

        var relative = walker.Walk(Root).Select(f => f.RelativePath).ToArray();

        foreach (var skipped in SyntheticPayloadFactory.SkippedFileNames)
        {
            Assert.DoesNotContain(skipped, relative);
        }
    }

    [Fact]
    public void Skips_root_optiscaler_dll_and_ini_because_they_are_handled_separately()
    {
        var (walker, _) = Build();

        var relative = walker.Walk(Root).Select(f => f.RelativePath).ToArray();

        Assert.DoesNotContain("OptiScaler.dll", relative);
        Assert.DoesNotContain("OptiScaler.ini", relative);
    }

    [Fact]
    public void Keeps_files_inside_the_optiscaler_subtree()
    {
        var (walker, _) = Build();

        var relative = walker.Walk(Root).Select(f => f.RelativePath).ToArray();

        Assert.Contains(@"OptiScaler\amdxc64.dll", relative);
        Assert.Contains(@"OptiScaler\D3D12_OptiScaler\d3d12core.dll", relative);
        Assert.Contains(@"Licenses\DirectX_LICENSE.txt", relative);
    }

    [Fact]
    public void OptiScaler_subtree_comes_before_Licenses_which_comes_before_everything_else()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, Root);
        fs.AddFile($@"{Root}\Extra\readme.txt", new MockFileData("x"));
        var walker = new PayloadWalker(fs);

        var relative = walker.Walk(Root).Select(f => f.RelativePath).ToArray();

        var firstLicenses = Array.FindIndex(relative, r => r.StartsWith("Licenses", StringComparison.OrdinalIgnoreCase));
        var lastOptiScaler = Array.FindLastIndex(relative, r => r.StartsWith("OptiScaler\\", StringComparison.OrdinalIgnoreCase));
        var extra = Array.FindIndex(relative, r => r.StartsWith("Extra", StringComparison.OrdinalIgnoreCase));

        Assert.True(lastOptiScaler < firstLicenses, $"OptiScaler\\ 必须全部排在 Licenses\\ 前（{lastOptiScaler} vs {firstLicenses}）");
        Assert.True(firstLicenses < extra, "Licenses\\ 必须排在其他目录前");
    }

    [Fact]
    public void Populates_length_and_sha256()
    {
        var (walker, fs) = Build();

        var file = walker.Walk(Root).Single(f => f.RelativePath == @"OptiScaler\amdxc64.dll");

        Assert.True(file.Length > 0);
        Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
        Assert.Equal(file.Sha256, Deployer.Core.Util.Hashing.Sha256OfFile(fs, file.FullPath));
    }

    [Fact]
    public void Deterministic_ordering_across_calls()
    {
        var (walker, _) = Build();

        var first = walker.Walk(Root).Select(f => f.RelativePath).ToArray();
        var second = walker.Walk(Root).Select(f => f.RelativePath).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Missing_root_throws_payload_not_found()
    {
        var walker = new PayloadWalker(new MockFileSystem());

        Assert.Throws<Deployer.Core.Errors.PayloadNotFoundException>(() => walker.Walk(@"D:\nope"));
    }
}
```

- [ ] **Step 8: 写 `Planning/PayloadWalker.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Errors;
using Deployer.Core.Util;

namespace Deployer.Core.Planning;

/// <summary>
/// 遍历 payload 根目录，产出**有序**的待部署文件列表。
/// 排序规则（spec §5.4）：OptiScaler\** → Licenses\** → 其余。
/// 身份 DLL 与 OptiScaler.ini 由 planner 单独追加到最后，此处一律排除。
/// </summary>
public sealed class PayloadWalker
{
    private static readonly HashSet<string> ExcludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptiScaler.dll",          // 重命名为身份 DLL，由 planner 单独处理
        "OptiScaler.ini",          // 必经 IniPatcher，不是直拷
        "setup_windows.bat",       // 本工具即其替代品
        "setup_linux.sh",
        "!! EXTRACT ALL FILES TO GAME FOLDER !!",
    };

    private readonly IFileSystem _fs;

    public PayloadWalker(IFileSystem fs) => _fs = fs;

    public IReadOnlyList<PayloadFile> Walk(string payloadRoot)
    {
        if (!_fs.Directory.Exists(payloadRoot))
        {
            throw new PayloadNotFoundException($"payload 根目录不存在：{payloadRoot}");
        }

        var rootFull = _fs.Path.GetFullPath(payloadRoot);
        var files = new List<PayloadFile>();

        foreach (var path in _fs.Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var relative = ToRelative(rootFull, path);

            if (IsExcluded(relative))
            {
                continue;
            }

            var info = _fs.FileInfo.New(path);
            files.Add(new PayloadFile(relative, path, info.Length, Hashing.Sha256OfFile(_fs, path)));
        }

        return files
            .OrderBy(f => Rank(f.RelativePath))
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int Rank(string relativePath)
    {
        var topLevel = relativePath.Split('\\', '/')[0];
        return topLevel.ToLowerInvariant() switch
        {
            "optiscaler" => 0,
            "licenses" => 1,
            _ => 2,
        };
    }

    /// <summary>只有根目录下的那几个文件被排除；OptiScaler\Licenses\ 里的文件不排除。</summary>
    private static bool IsExcluded(string relativePath) =>
        !relativePath.Contains('\\') && !relativePath.Contains('/')
            && ExcludedRootFiles.Contains(relativePath);

    private string ToRelative(string rootFull, string fullPath)
    {
        var prefix = rootFull.EndsWith(_fs.Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + _fs.Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath[prefix.Length..]
            : fullPath;
    }
}
```

- [ ] **Step 9: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 71`

- [ ] **Step 10: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add fingerprint calculator and payload walker"
```

---

## Task 9: DeploymentPlanner —— 唯一汇聚点

**当前状态（2026-10-09）**：已实现并通过集成定向验证。当前实现/测试以源码和 Task 16 的最终验证为准；下方原始逐步片段与阶段计数仅作历史记录。

Spec §5。planner 是**纯函数**：输入 `PlanRequest`，输出 `DeploymentPlan`，除了读 payload 与部署目录的存在性以外不产生任何副作用。不写文件、不改注册表、不联网。

**对 Task 2 的第二处类型修正**：`FileWrite` 需要一个可选的 `InlineContent`。原因：`OptiScaler.ini` 不是「拷过去」而是「改写后落地」，计划时它的内容已经算出来了，但源文件里存的还是 `auto` 版本。若不把改写结果放进 plan，executor 就得重新跑一遍 IniPatcher——那就是把业务逻辑泄到执行层，也破坏了「Worker 只需序列化 plan」的设计支点（spec §3.4）。修改 `src/Deployer.Core/Domain/IniEdit.cs`：

```csharp
/// <summary>
/// 一次文件写入。DestinationPath 为绝对路径。
/// InlineContent 非 null 时写该内容（用于已 patch 的 OptiScaler.ini），
/// 否则从 SourcePath 拷贝。
/// </summary>
public sealed record FileWrite(
    string SourcePath, string DestinationPath, long Length, string Sha256, string? InlineContent = null)
{
    public bool IsInline => InlineContent is not null;
}
```

**Files:**
- Modify: `src/Deployer.Core/Domain/IniEdit.cs`（`FileWrite` 加一个可选参数）
- Create: `src/Deployer.Core/Planning/IDeploymentPlanner.cs`
- Create: `src/Deployer.Core/Planning/DeploymentPlanner.cs`
- Test: `tests/Deployer.Core.Tests/DeploymentPlannerTests.cs`
- Create: `tests/Deployer.TestData/FakeGameFactory.cs`

- [ ] **Step 1: 写 `tests/Deployer.TestData/FakeGameFactory.cs`**

spec §11.2 要求的三个假游戏目录。放在 TestData 项目里，计划 A 的 planner/executor 测试与计划 B 的 scanner 测试共用。

```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;

namespace Deployer.TestData;

/// <summary>在 MockFileSystem 上造 spec §11.2 定义的三种假游戏目录。</summary>
public static class FakeGameFactory
{
    /// <summary>UE 游戏：根目录有 Engine\，exe 在 &lt;Project&gt;\Binaries\Win64\。</summary>
    public static FakeGame CreateUeGame(MockFileSystem fs, string root = @"C:\Games\FakeUeGame")
    {
        fs.AddDirectory($@"{root}\Engine\Binaries\ThirdParty");
        fs.AddDirectory($@"{root}\SwGame\Content");
        var exe = $@"{root}\SwGame\Binaries\Win64\SwGame-Win64-Shipping.exe";
        fs.AddFile(exe, new MockFileData("UE GAME EXE"));
        fs.AddFile($@"{root}\SwGame\Binaries\Win64\SwGame.exe", new MockFileData("LAUNCHER"));

        return new FakeGame("Fake UE Game", root, $@"{root}\SwGame\Binaries\Win64", exe,
            "SwGame-Win64-Shipping.exe", hasEngineFolder: true);
    }

    /// <summary>Xbox GDK 平面安装：content\&lt;Project&gt;\Binaries\Win64\*-WinGDK-Shipping.exe。</summary>
    public static FakeGame CreateGdkGame(
        MockFileSystem fs, string root = @"C:\XboxGames\FakeGdkGame\content")
    {
        fs.AddDirectory($@"{root}\Engine\Binaries");
        var exe = $@"{root}\GameMain\Binaries\Win64\GameMain-WinGDK-Shipping.exe";
        fs.AddFile(exe, new MockFileData("GDK GAME EXE"));
        fs.AddFile($@"{root}\GameMain\Binaries\Win64\MicrosoftGame.config", new MockFileData("<Config/>"));
        fs.AddFile($@"{root}\GameMain\Content\Paks\pak0.pak", new MockFileData("PAK"));

        return new FakeGame("Fake GDK Game", root, $@"{root}\GameMain\Binaries\Win64", exe,
            "GameMain-WinGDK-Shipping.exe", hasEngineFolder: true);
    }

    /// <summary>
    /// 自带 version.dll 与 winmm.dll 的游戏 —— 用来验证身份冲突时会自动换身份而不是 del /F。
    /// </summary>
    public static FakeGame CreateConflictGame(MockFileSystem fs, string root = @"C:\Games\FakeConflictGame")
    {
        var exe = $@"{root}\Game.exe";
        fs.AddFile(exe, new MockFileData("CONFLICT GAME EXE"));
        fs.AddFile($@"{root}\version.dll", new MockFileData("GAME OWN version.dll"));
        fs.AddFile($@"{root}\winmm.dll", new MockFileData("GAME OWN winmm.dll"));
        fs.AddFile($@"{root}\d3d12.dll", new MockFileData("GAME OWN d3d12.dll"));

        return new FakeGame("Fake Conflict Game", root, root, exe, "Game.exe", hasEngineFolder: false);
    }
}

/// <summary>一个假游戏的坐标。</summary>
public sealed record FakeGame(
    string Title,
    string InstallRoot,
    string DeployDirectory,
    string ExecutablePath,
    string ExecutableName,
    bool HasEngineFolder)
{
    /// <summary>带默认 SDK 的体检结果（有 DLSS，所以不会触发 Blocking 警告）。</summary>
    public InspectionSummary DefaultInspection { get; } = new()
    {
        GraphicsApis = new[] { GraphicsApi.Dx12 },
        DetectedSdks = new[] { UpscalerSdk.Dlss },
        HasEngineFolder = HasEngineFolder,
        IsVulkanOnly = false,
    };

    public InspectionSummary NoSdkInspection { get; } = new()
    {
        GraphicsApis = new[] { GraphicsApi.Dx12 },
        DetectedSdks = Array.Empty<UpscalerSdk>(),
        HasEngineFolder = HasEngineFolder,
        IsVulkanOnly = false,
    };
}
```

- [ ] **Step 2: 写 `Planning/IDeploymentPlanner.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Planning;

public sealed record PlanRequest
{
    public required GameTarget Game { get; init; }
    public required InspectionSummary Inspection { get; init; }
    public required PayloadBundle Payload { get; init; }
    public required UpscalerProfile Profile { get; init; }
    public required DirectoryWritability Writability { get; init; }

    /// <summary>影子备份根，形如 %LOCALAPPDATA%\OptiScalerDeployer\backups。注入而非内部取环境变量，保证 planner 纯。</summary>
    public required string BackupRoot { get; init; }

    public bool EnableFrameGen { get; init; }
    public bool AllowOverwriteGameFile { get; init; }
    public bool PreferAsiIdentity { get; init; }

    /// <summary>不传则随机生成。测试可传固定值以获得可重复的备份路径。</summary>
    public Guid? PlanId { get; init; }
}

public interface IDeploymentPlanner
{
    DeploymentPlan Plan(PlanRequest request);
}
```

- [ ] **Step 3: 写 `DeploymentPlannerTests.cs`**

`tests/Deployer.Core.Tests/DeploymentPlannerTests.cs`：
```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class DeploymentPlannerTests
{
    private const string RepoRoot = @"D:\repo\hlyOptiScaler";
    private const string PayloadRoot = @"D:\repo\hlyOptiScaler\x64\Release\a";
    private const string BackupRoot = @"C:\users\tester\AppData\Local\OptiScalerDeployer\backups";
    private static readonly Guid FixedPlanId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();
    private readonly FingerprintCalculator _fingerprints = new();

    private DeploymentPlanner Planner() => new(
        _fs,
        new DllIdentitySelector(_fs, _versions),
        new PayloadWalker(_fs),
        new IniPatcher());

    private FakeGame PrepareUeGame()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        return FakeGameFactory.CreateUeGame(_fs);
    }

    private PlanRequest Request(FakeGame game, GamePlatform platform = GamePlatform.Steam, bool frameGen = false,
        bool allowOverwrite = false, InspectionSummary? inspection = null)
    {
        var target = new GameTarget(
            game.Title, platform, null, null, game.InstallRoot, game.DeployDirectory,
            game.ExecutableName,
            _fingerprints.Compute(platform, null, game.InstallRoot, game.ExecutableName));

        return new PlanRequest
        {
            Game = target,
            Inspection = inspection ?? game.DefaultInspection,
            Payload = new PayloadBundle(PayloadRoot, "0.9.1.0", PayloadOrigin.LocalBuild),
            Profile = BuiltinProfiles.AmdRdna4,
            Writability = DirectoryWritability.Writable,
            BackupRoot = BackupRoot,
            EnableFrameGen = frameGen,
            AllowOverwriteGameFile = allowOverwrite,
            PlanId = FixedPlanId,
        };
    }

    // ---- 基本形状 ----

    [Fact]
    public void Plan_copies_the_whole_optiscaler_subtree()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));

        var destinations = plan.Writes.Select(w => w.DestinationPath).ToArray();
        Assert.Contains($@"{game.DeployDirectory}\OptiScaler\amdxc64.dll", destinations);
        Assert.Contains($@"{game.DeployDirectory}\OptiScaler\D3D12_OptiScaler\d3d12core.dll", destinations);
        Assert.Contains($@"{game.DeployDirectory}\Licenses\DirectX_LICENSE.txt", destinations);
    }

    [Fact]
    public void Plan_never_writes_the_installer_scripts()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));

        foreach (var skipped in SyntheticPayloadFactory.SkippedFileNames)
        {
            Assert.DoesNotContain(plan.Writes, w =>
                w.DestinationPath.EndsWith(skipped, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Plan_renames_optiscaler_dll_to_the_chosen_identity()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));

        var last = plan.Writes[^1];
        Assert.Equal(DllIdentity.Dxgi, plan.Identity.Chosen);
        Assert.Equal($@"{game.DeployDirectory}\dxgi.dll", last.DestinationPath);
        Assert.Equal($@"{PayloadRoot}\OptiScaler.dll", last.SourcePath);
    }

    // ---- spec §5.4 写入顺序：点火开关最后落 ----

    [Fact]
    public void Write_order_is_subtree_then_ini_then_identity_dll()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));
        var destinations = plan.Writes.Select(w => w.DestinationPath).ToList();

        var iniIndex = destinations.FindIndex(d => d.EndsWith("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        var lastSubtreeIndex = destinations.FindLastIndex(d =>
            d.Contains($"\\OptiScaler\\", StringComparison.OrdinalIgnoreCase) ||
            d.Contains($"\\Licenses\\", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(destinations.Count - 1, iniIndex + 1);   // ini 是倒数第二个
        Assert.True(lastSubtreeIndex < iniIndex,
            $"所有子树文件必须排在 ini 之前（子树最后={lastSubtreeIndex}, ini={iniIndex}）");
    }

    [Fact]
    public void Ini_write_carries_patched_content_not_the_payload_original()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));
        var ini = plan.Writes[^2];

        Assert.NotNull(ini.InlineContent);
        Assert.Contains("Dx12Upscaler=ffx", ini.InlineContent);
        Assert.DoesNotContain("Dx12Upscaler=auto", ini.InlineContent);
        Assert.Equal($@"{PayloadRoot}\OptiScaler.ini", ini.SourcePath);
    }

    [Fact]
    public void Ini_write_preserves_every_comment_line_from_the_payload_ini()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));
        var original = _fs.File.ReadAllText($@"{PayloadRoot}\OptiScaler.ini");
        var patched = plan.Writes[^2].InlineContent!;

        var originalComments = original.Split("\r\n").Where(l => l.TrimStart().StartsWith(';')).ToArray();
        var patchedLines = patched.Split("\r\n");
        Assert.All(originalComments, c => Assert.Contains(c, patchedLines));
        Assert.True(patchedLines.Length >= original.Split("\r\n").Length);
    }

    // ---- spec §8.3 / §8.4 游戏特定 INI 覆盖 ----

    [Fact]
    public void Plan_locks_injection_to_this_games_process()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));

        var edit = plan.IniEdits.Single(e => e.Section == "ProcessFilter" && e.Key == "TargetProcessName");
        Assert.Equal(game.ExecutableName, edit.NewValue);
        Assert.Contains("TargetProcessName=" + game.ExecutableName, plan.Writes[^2].InlineContent);
    }

    [Fact]
    public void Plan_turns_on_file_logging_because_default_auto_is_false()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));

        Assert.Contains(plan.IniEdits, e => e.Section == "Log" && e.Key == "LogToFile" && e.NewValue == "true");
        Assert.Contains(plan.IniEdits, e => e.Section == "Log" && e.Key == "LogLevel" && e.NewValue == "2");
    }

    [Fact]
    public void FrameGen_addon_is_absent_unless_requested()
    {
        var game = PrepareUeGame();

        var off = Planner().Plan(Request(game, frameGen: false));
        var on = Planner().Plan(Request(game, frameGen: true));

        Assert.Equal("false", off.IniEdits.Single(e => e.Section == "FrameGen" && e.Key == "Enabled").NewValue);
        Assert.Equal("true", on.IniEdits.Single(e => e.Section == "FrameGen" && e.Key == "Enabled").NewValue);
        Assert.Contains(on.IniEdits, e => e.Section == "OptiFG" && e.Key == "HUDFix" && e.NewValue == "true");
    }

    [Fact]
    public void FrameGen_input_switches_to_dlssg_when_game_has_streamline()
    {
        var game = PrepareUeGame();
        var inspection = new InspectionSummary
        {
            GraphicsApis = new[] { GraphicsApi.Dx12 },
            DetectedSdks = new[] { UpscalerSdk.Dlss, UpscalerSdk.Streamline },
            HasEngineFolder = true,
            IsVulkanOnly = false,
        };

        var plan = Planner().Plan(Request(game, frameGen: true, inspection: inspection));

        Assert.Equal("dlssg", plan.IniEdits.Single(e => e.Section == "FrameGen" && e.Key == "FGInput").NewValue);
    }

    // ---- 备份 ----

    [Fact]
    public void Existing_identity_dll_is_backed_up_not_deleted()
    {
        var game = PrepareUeGame();
        var existing = $@"{game.DeployDirectory}\dxgi.dll";
        _fs.AddFile(existing, new MockFileData("GAME OWN dxgi"));
        _versions.SetOriginalFilename(existing, "GameGraphics.dll");

        var plan = Planner().Plan(Request(game, allowOverwrite: true));

        var backup = plan.Backups.Single(b => b.OriginalPath == existing);
        Assert.Equal(BackupReason.OverwriteGameFile, backup.Reason);
        Assert.StartsWith($@"{BackupRoot}\{plan.Game.Fingerprint}\{FixedPlanId}\", backup.BackupPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Previous_optiscaler_install_is_marked_as_such()
    {
        var game = PrepareUeGame();
        var existing = $@"{game.DeployDirectory}\dxgi.dll";
        _fs.AddFile(existing, new MockFileData("OLD OPTISCALER"));
        _versions.SetOriginalFilename(existing, "OptiScaler.dll");

        var plan = Planner().Plan(Request(game));

        Assert.Equal(BackupReason.OverwritePreviousOpti, plan.Backups.Single(b => b.OriginalPath == existing).Reason);
        Assert.DoesNotContain(plan.Warnings, w => w.Severity == WarningSeverity.Caution);
    }

    [Fact]
    public void Existing_optiscaler_ini_is_backed_up_with_reason_ini_modified()
    {
        var game = PrepareUeGame();
        _fs.AddFile($@"{game.DeployDirectory}\OptiScaler.ini", new MockFileData("[Upscalers]\r\n"));

        var plan = Planner().Plan(Request(game));

        var backup = plan.Backups.Single(b => b.OriginalPath.EndsWith("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(BackupReason.IniModified, backup.Reason);
    }

    [Fact]
    public void Fresh_directory_produces_no_backups()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));

        Assert.Empty(plan.Backups);
    }

    // ---- 布局与警告 ----

    [Fact]
    public void Gdk_game_deploys_into_the_exe_directory_not_the_content_root()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateGdkGame(_fs);

        var plan = Planner().Plan(Request(game, platform: GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Winmm, plan.Identity.Chosen);
        Assert.All(plan.Writes, w => Assert.StartsWith(game.DeployDirectory, w.DestinationPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Writes, w =>
            _fs.Path.GetDirectoryName(w.DestinationPath) == game.InstallRoot);
    }

    [Fact]
    public void Conflict_game_switches_identity_away_from_game_owned_dlls()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateConflictGame(_fs);
        foreach (var name in new[] { "version.dll", "winmm.dll", "d3d12.dll" })
        {
            _versions.SetOriginalFilename($@"{game.DeployDirectory}\{name}", "GameFile.dll");
        }

        var plan = Planner().Plan(Request(game, platform: GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Dbghelp, plan.Identity.Chosen);
        Assert.Equal(IdentityStatus.RequiresUserDecision, plan.Identity.Status);
        Assert.Contains(plan.Warnings, w => w.Severity == WarningSeverity.Caution);
    }

    [Fact]
    public void Game_without_any_upscaler_sdk_gets_a_blocking_warning()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game, inspection: game.NoSdkInspection));

        Assert.True(plan.HasBlockingWarning);
    }

    [Fact]
    public void Engine_folder_game_deployed_outside_binaries_win64_gets_a_caution()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateUeGame(_fs);
        // 故意把部署目录误设为游戏根目录——这正是官方 BAT 那段 Engine 警告要防的错装位置
        var target = new GameTarget(game.Title, GamePlatform.Steam, null, null,
            game.InstallRoot, game.InstallRoot, game.ExecutableName,
            _fingerprints.Compute(GamePlatform.Steam, null, game.InstallRoot, game.ExecutableName));

        var plan = Planner().Plan(Request(game) with { Game = target });

        Assert.Contains(plan.Warnings, w =>
            w.Severity == WarningSeverity.Caution &&
            w.Message.Contains("Binaries", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dx11_game_on_ffx_12_profile_gets_an_interop_performance_notice()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateUeGame(_fs);
        var inspection = new InspectionSummary
        {
            GraphicsApis = new[] { GraphicsApi.Dx11 },
            DetectedSdks = new[] { UpscalerSdk.Dlss },
            HasEngineFolder = true,
            IsVulkanOnly = false,
        };

        var plan = Planner().Plan(Request(game, inspection: inspection));

        Assert.Contains(plan.Warnings, w =>
            w.Severity == WarningSeverity.Info &&
            w.Message.Contains("10%", StringComparison.Ordinal));
    }

    [Fact]
    public void Writability_maps_to_elevation_requirement()
    {
        var game = PrepareUeGame();
        var planner = Planner();

        Assert.Equal(ElevationRequirement.NotRequired,
            planner.Plan(Request(game) with { Writability = DirectoryWritability.Writable }).Elevation);
        Assert.Equal(ElevationRequirement.Required,
            planner.Plan(Request(game) with { Writability = DirectoryWritability.NeedsElevation }).Elevation);
        Assert.Equal(ElevationRequirement.Blocked,
            planner.Plan(Request(game) with { Writability = DirectoryWritability.Blocked }).Elevation);
    }

    [Fact]
    public void NeedsElevation_produces_a_caution_warning()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game) with { Writability = DirectoryWritability.NeedsElevation });

        Assert.Contains(plan.Warnings, w => w.Severity == WarningSeverity.Caution);
    }

    [Fact]
    public void Same_request_twice_yields_identical_plans()
    {
        var game = PrepareUeGame();
        var planner = Planner();

        var a = planner.Plan(Request(game));
        var b = planner.Plan(Request(game));

        JsonAssert.Equivalent(a, b);
    }

    [Fact]
    public void Plan_serializes_and_roundtrips()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game, frameGen: true));

        var restored = Deployer.Core.Serialization.DeployerJson.Deserialize<DeploymentPlan>(
            Deployer.Core.Serialization.DeployerJson.Serialize(plan));

        JsonAssert.Equivalent(plan, restored);
    }

    [Fact]
    public void Every_write_carries_a_sha256_and_length()
    {
        var game = PrepareUeGame();

        var plan = Planner().Plan(Request(game));

        Assert.All(plan.Writes, w =>
        {
            Assert.Matches("^[0-9a-f]{64}$", w.Sha256);
            Assert.True(w.Length > 0, $"{w.DestinationPath} 长度为 0");
        });
    }
}
```

- [ ] **Step 4: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter DeploymentPlannerTests`
Expected: 编译错误 `DeploymentPlanner 不存在`

- [ ] **Step 5: 写 `Planning/DeploymentPlanner.cs`**

```csharp
using System.IO.Abstractions;
using System.Text;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Profiles;
using Deployer.Core.Util;

namespace Deployer.Core.Planning;

public sealed class DeploymentPlanner : IDeploymentPlanner
{
    private readonly IFileSystem _fs;
    private readonly IDllIdentitySelector _identitySelector;
    private readonly PayloadWalker _walker;
    private readonly IIniPatcher _iniPatcher;

    public DeploymentPlanner(
        IFileSystem fs, IDllIdentitySelector identitySelector, PayloadWalker walker, IIniPatcher iniPatcher)
    {
        _fs = fs;
        _identitySelector = identitySelector;
        _walker = walker;
        _iniPatcher = iniPatcher;
    }

    public DeploymentPlan Plan(PlanRequest request)
    {
        var planId = request.PlanId ?? Guid.NewGuid();
        var deployDir = request.Game.DeployDirectory;

        // 1. 身份
        var identity = _identitySelector.Select(new IdentityContext(
            request.Game.Platform,
            request.Inspection.IsVulkanOnly,
            deployDir,
            request.AllowOverwriteGameFile,
            request.PreferAsiIdentity));

        // 2. 子树文件
        var writes = new List<FileWrite>();
        var backups = new List<BackupEntry>();
        var backupDir = _fs.Path.Combine(
            request.BackupRoot, request.Game.Fingerprint, planId.ToString("D"));

        foreach (var file in _walker.Walk(request.Payload.RootDirectory))
        {
            var destination = _fs.Path.Combine(deployDir, file.RelativePath);
            writes.Add(new FileWrite(file.FullPath, destination, file.Length, file.Sha256));
            AddBackupIfExisting(backups, destination, file.RelativePath, backupDir, BackupReason.OverwriteGameFile);
        }

        // 3. OptiScaler.ini（必经 IniPatcher，倒数第二落盘）
        var iniSource = _fs.Path.Combine(request.Payload.RootDirectory, "OptiScaler.ini");
        var iniEdits = BuildIniEdits(request);
        var patched = _iniPatcher.Patch(_fs.File.ReadAllText(iniSource), iniEdits);
        var resolvedEdits = patched.AppliedEdits.Concat(patched.AppendedEdits).ToList();
        var iniContent = patched.Content;
        var iniBytes = Encoding.UTF8.GetByteCount(iniContent);
        var iniDestination = _fs.Path.Combine(deployDir, "OptiScaler.ini");

        writes.Add(new FileWrite(
            iniSource, iniDestination, iniBytes, Hashing.Sha256OfString(iniContent), iniContent));
        AddBackupIfExisting(backups, iniDestination, "OptiScaler.ini", backupDir, BackupReason.IniModified);

        // 4. 身份 DLL（点火开关，最后一步）
        var identitySource = _fs.Path.Combine(request.Payload.RootDirectory, "OptiScaler.dll");
        var identityDestination = _fs.Path.Combine(deployDir, identity.FileName);
        var identityInfo = _fs.FileInfo.New(identitySource);
        writes.Add(new FileWrite(
            identitySource, identityDestination, identityInfo.Length,
            Hashing.Sha256OfFile(_fs, identitySource)));

        if (identity.ExistingFileReason is { } reason)
        {
            AddBackupIfExisting(backups, identityDestination, identity.FileName, backupDir, reason);
        }

        return new DeploymentPlan
        {
            PlanId = planId,
            Game = request.Game,
            Payload = PayloadInfo.From(request.Payload),
            Identity = identity,
            Profile = request.Profile,
            Writability = request.Writability,
            Elevation = MapElevation(request.Writability),
            Writes = writes,
            Backups = backups,
            IniEdits = resolvedEdits,
            Warnings = BuildWarnings(request, identity, backups),
        };
    }

    private IReadOnlyList<IniEdit> BuildIniEdits(PlanRequest request)
    {
        // 不需要在这里校验 Section/Key 非空：IniPatcher.Patch 已经把它们当成硬错误抛。
        // 这一层再校一次只是重复；而且放那里才对：IniPatcher 是真正会把 `[]`
        // 写进游戏文件的那一层，而 request.Profile.Edits 可能来自用户自己写的 JSON。
        var edits = new List<IniEdit>(request.Profile.Edits);

        if (request.EnableFrameGen)
        {
            var useStreamline = request.Inspection.DetectedSdks.Contains(UpscalerSdk.Streamline);
            edits.AddRange(BuiltinProfiles.FrameGenAddOn(useStreamline));
        }

        // 游戏特定项，不属于 Profile（spec §8.4）
        edits.Add(new IniEdit("ProcessFilter", "TargetProcessName", null, request.Game.ExecutableName));

        // LogToFile 默认 auto=false，不显式开启就永远不会有日志（spec §8.3）
        edits.Add(new IniEdit("Log", "LogToFile", null, "true"));
        edits.Add(new IniEdit("Log", "LogLevel", null, "2"));

        return edits;
    }

    private void AddBackupIfExisting(
        List<BackupEntry> backups, string destination, string relativePath, string backupDir, BackupReason reason)
    {
        if (!_fs.File.Exists(destination))
        {
            return;
        }

        var backupPath = _fs.Path.Combine(backupDir, relativePath);
        if (backups.Any(b => string.Equals(b.OriginalPath, destination, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        backups.Add(new BackupEntry(destination, backupPath, reason));
    }

    private static ElevationRequirement MapElevation(DirectoryWritability writability) => writability switch
    {
        DirectoryWritability.Writable => ElevationRequirement.NotRequired,
        DirectoryWritability.Unknown => ElevationRequirement.NotRequired,
        DirectoryWritability.NeedsElevation => ElevationRequirement.Required,
        DirectoryWritability.Blocked => ElevationRequirement.Blocked,
        _ => ElevationRequirement.NotRequired,
    };

    private static IReadOnlyList<PlanWarning> BuildWarnings(
        PlanRequest request, IdentityDecision identity, IReadOnlyList<BackupEntry> backups)
    {
        var warnings = new List<PlanWarning>();

        if (!request.Inspection.HasAnyUpscalerSdk)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking,
                $"{request.Game.Title} 里没找到任何超分 SDK（DLSS / XeSS / FSR）。"
              + "OptiScaler 只能把已有的超分调用转发到其他实现，游戏本身不调用就无从介入，装了也不会生效。"));
        }

        if (identity.Status == IdentityStatus.RequiresUserDecision)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution, identity.UserPromptMessage ?? "身份 DLL 全部冲突。"));
        }
        else if (identity.SkippedDueToConflict.Count > 0 && identity.UserPromptMessage is { } skipMessage)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Info, skipMessage));
        }

        if (identity.Chosen == DllIdentity.Asi)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                "选中了 OptiScaler.asi。只有游戏侧已有 ASI Loader（如 Ultimate ASI Loader）时才会被加载，否则完全无效。"));
        }

        if (request.Writability == DirectoryWritability.NeedsElevation)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                $"{request.Game.DeployDirectory} 不可写，执行时会弹 UAC 请求管理员权限（常见于老式 MSIXVC / WindowsApps 安装）。"));
        }
        else if (request.Writability == DirectoryWritability.Blocked)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking,
                $"{request.Game.DeployDirectory} 无法写入，即使提权也不行（可能是加密包或目录被锁定）。"));
        }

        if (request.Inspection.HasEngineFolder
            && !request.Game.DeployDirectory.Replace('/', '\\').EndsWith(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                $"检测到 UE 游戏（存在 Engine\\），但部署目录不是 Binaries\\Win64：{request.Game.DeployDirectory}。"
              + "装错位置会导致 DLL 根本不被加载。正确的目录应该是选中 exe 所在的 Binaries\\Win64。"));
        }

        if (UsesInterop(request) && request.Inspection.GraphicsApis.Contains(GraphicsApi.Dx11))
        {
            warnings.Add(new PlanWarning(WarningSeverity.Info,
                "这是一个 DX11 游戏，当前 Profile 走 D3D11on12 interop 以启用 FSR4，约有 10% 性能损失。"
              + "若帧数不理想，可切到「AMD RDNA4（原生，性能优先）」档，代价是画质上限降为 FSR3.1。"));
        }

        if (UsesInterop(request) && request.Inspection.IsVulkanOnly)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Info,
                "这是一个 Vulkan 游戏，当前 Profile 走 VK↔DX12 interop 以启用 FSR4，约有 10% 性能损失。"));
        }

        var gameFileCount = backups.Count(b => b.Reason == BackupReason.OverwriteGameFile);
        if (gameFileCount > 0)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                $"将覆盖 {gameFileCount} 个游戏自带文件。原件会先进影子备份，回滚时逐字节还原。"));
        }

        return warnings;
    }

    private static bool UsesInterop(PlanRequest request) =>
        request.Profile.Edits.Any(e =>
            e.Section == "Upscalers"
            && (e.Key == "Dx11Upscaler" || e.Key == "VulkanUpscaler")
            && e.NewValue.EndsWith("_12", StringComparison.Ordinal));
}
```

**为什么 `resolvedEdits` 用的是 patch 后的结果而不是 `BuildIniEdits` 的输出**：前者 `OldValue` 已回填（能渲染 `auto → ffx` 的 diff），后者 `OldValue` 全为 null。UI 预演必须用前者。

**`IniPatchResult` 若加 `Warnings` 通道（Task 9 设计输入）**：已裁定 `IniPatchResult` 不在 `IniPatcher` 这一层单方面加 `Warnings`，而是推迟到 Task 9、与既有 `PlanWarning`（`WarningSeverity` / `Message`）协调统一走 planner 的 `BuildWarnings`。「追加键落在下一个 section 的开头装饰块里」这类**观感降级**（详见 Task 4 已知限制：真 ini 37 个头全被横幅夹住，跨段追加时新键会楔进下一段的开头横幅与其 `[Header]` 之间，section 作用域不破、文件仍合法）是典型候选条目——由 planner 汇成一条 Info/Caution 级 `PlanWarning` 报给 UI 让用户知情，而不是在 patcher 层静默处理。

**为什么 `BuildWarnings` 要收 `backups` 参数**：「将覆盖 N 个游戏自带文件」这条警告的数据是 planner 自己在算 `Writes` 时产生的，`PlanRequest` 里根本没有。往 `PlanRequest` 上挂一个辅助方法去查备份只会把数据流弄反。

**为什么 `Length` 用 `Encoding.UTF8.GetByteCount`**：`InlineContent` 是字符串，而 `FileWrite.Length` 是字节数，executor 会用它做校验。两者必须同一口径，否则 INI 的 SHA256/长度校验会永远不过。

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter DeploymentPlannerTests`
Expected: `失败: 0，通过: 21`

`Same_request_twice_yields_identical_plans` 能过的前提是 `PlanId` 已固定且 `Writes` / `Backups` / `Warnings` 顺序均确定。若抱错在 `Backups` 上，检查 `PayloadWalker` 的排序是否稳定（Task 8 Step 7 的 `Deterministic_ordering_across_calls` 已经守住这一条）。

- [ ] **Step 7: 全量跑**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 92`

- [ ] **Step 8: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add deployment planner producing immutable ordered plans"
```

---

## Task 10: 影子备份与原子写 — 已实现

**实现文件：** [AtomicFileWriter.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Deployment/AtomicFileWriter.cs)、[IBackupStore.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Deployment/IBackupStore.cs)、[BackupStore.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Deployment/BackupStore.cs)。

`AtomicFileWriter` 对临时文件同时核对字节长度和 SHA256。Inline 内容用无 BOM UTF-8。目标已存在时只用 `File.Replace`；目标不存在时用 `File.Move`。已有 `.optideploy.tmp` 或 `.optideploy.bak` 会被拒绝，替换失败不走“先删目标再移动”的不安全回退。

`BackupStore` 只接受 8 位十六进制 Fingerprint，备份目标与 manifest/progress 操作限制在备份根和对应 plan 目录内；既存备份拒绝覆盖。备份前后核对哈希，JSON 文件通过 AtomicFileWriter 更新。读取失败会保留内部异常原因。`Prune` 仅作为显式 API，不由部署自动调用，避免删除仍需用于逆序回滚的旧备份链。

新增回归测试位于 [AtomicFileWriterTests.cs](../../../tools/OptiScalerDeployer/tests/Deployer.Core.Tests/AtomicFileWriterTests.cs) 与 [BackupStoreTests.cs](../../../tools/OptiScalerDeployer/tests/Deployer.Core.Tests/BackupStoreTests.cs)。最终定向验证见 Task 16；下方原始逐步示例与期望测试数是早期设计记录，不作为当前实现规范。

## Task 11: RollbackExecutor — 已实现

**实现文件：** [IRollbackExecutor.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Deployment/IRollbackExecutor.cs)、[RollbackExecutor.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Deployment/RollbackExecutor.cs)。

回滚在任何修改前验证 manifest 路径、部署目录内写入目标、备份路径和 plan 归属。每个备份先校验已记录的 SHA256，再经 AtomicFileWriter 还原；还原失败的原路径始终受保护，不会在后续“删除新增文件”步骤中被误删。临时文件清理只按 manifest 中列出的目标路径处理，不递归扫描游戏目录。新建目录仅在仍为空时删除。

旧 manifest 的 `BackupEntry.Sha256` 缺失时仍允许回滚，并校验本次复制过程；由于旧文件没有历史哈希，无法判断备份在此前是否已经损坏。损坏或缺失 manifest 会返回失败结果，并保留读取异常原因。

回归测试位于 [RollbackExecutorTests.cs](../../../tools/OptiScalerDeployer/tests/Deployer.Core.Tests/RollbackExecutorTests.cs)，覆盖损坏/缺失备份保护、跨 plan 路径拒绝、空清单不变性和不触碰无关后缀文件。原始危险回退实现与 wildcard 清理示例已从本计划移除；以当前源文件为准。

## Task 12: DeploymentExecutor — 已实现

**实现文件：** [IDeploymentExecutor.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Deployment/IDeploymentExecutor.cs)、[DeploymentExecutor.cs](../../../tools/OptiScalerDeployer/src/Deployer.Core/Deployment/DeploymentExecutor.cs)。

执行前拒绝阻塞计划、未解决的 DLL 身份、受阻目录、越界/重复目标、没有备份的既存目标、临时文件冲突和身份 DLL 非最后一步。manifest 已存在时拒绝重放同一个 PlanId。

执行顺序是：先写入不包含任何游戏目标的空 manifest；完成并验证全部原件备份；一次写入完整备份、全部计划目标和缺失目录列表；然后才开始游戏文件写入。每一步成功后更新 progress。备份阶段失败时游戏文件不变，空 manifest 仍可安全读取；写入阶段失败时尝试回滚，取消则回滚后继续抛出取消异常。CLI 当前没有提权 Worker，Worker 接管仍属 Plan D。

部署不会自动 Prune 旧 manifest。每次重新部署须使用新 PlanId；连续多次部署若要完全卸载，必须按 plan 创建时间逆序回滚。自动保留三份策略推迟到能够识别已回滚/仍有效恢复链的后续生命周期实现。

回归测试位于 [DeploymentExecutorTests.cs](../../../tools/OptiScalerDeployer/tests/Deployer.Core.Tests/DeploymentExecutorTests.cs)，覆盖备份失败、首次 manifest 写失败、执行中取消、重放拒绝、身份顺序和路径约束。Task 16 汇总当前定向与最终验证结果。
## Task 13: 安装台账

**当前状态（2026-10-09）**：JsonInstallLedger 及回归测试已实现并通过 14 项定向测试。当前跨实例每次操作重新读取台账；损坏数据保留原件并报告 LoadError，具体以源码与 Task 16 为准。下方早期片段仅作历史记录。

Spec §5.6。台账只存索引（Fingerprint → 最新 PlanId + 备份根），明细在各 manifest 里。它是「每个游戏零共享状态」的落地点（spec §8.4）：卸载 A 绕不可能碰到 B。

**Files:**
- Create: `src/Deployer.Core/Persistence/IInstallLedger.cs`
- Create: `src/Deployer.Core/Persistence/JsonInstallLedger.cs`
- Test: `tests/Deployer.Core.Tests/JsonInstallLedgerTests.cs`

- [ ] **Step 1: 写 `Persistence/IInstallLedger.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Persistence;

public interface IInstallLedger
{
    IReadOnlyList<InstallRecord> All();

    InstallRecord? Get(string fingerprint);

    /// <summary>写入或更新（按 Fingerprint 幂等）。</summary>
    void Save(InstallRecord record);

    bool Remove(string fingerprint);

    /// <summary>只改状态，保留其余字段。Fingerprint 不存在时返回 false。</summary>
    bool SetState(string fingerprint, InstallState state);
}
```

- [ ] **Step 2: 写 `JsonInstallLedgerTests.cs`**

```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;
using Deployer.Core.Persistence;
using Xunit;

namespace Deployer.Core.Tests;

public class JsonInstallLedgerTests
{
    private const string Path = @"C:\users\tester\AppData\Local\OptiScalerDeployer\ledger.json";

    private readonly MockFileSystem _fs = new();

    private JsonInstallLedger Ledger() => new(_fs, Path);

    private static InstallRecord Record(string fingerprint, string title = "Game A") => new()
    {
        Fingerprint = fingerprint,
        GameTitle = title,
        DeployDirectory = $@"C:\Games\{title}\Binaries\Win64",
        LatestPlanId = Guid.NewGuid(),
        BackupRoot = $@"C:\backup-root\{fingerprint}",
        State = InstallState.Installed,
        PayloadVersion = "0.9.1.0",
        IdentityFileName = "dxgi.dll",
        InstalledUtc = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void All_is_empty_when_ledger_file_missing()
    {
        Assert.Empty(Ledger().All());
    }

    [Fact]
    public void Save_then_Get_roundtrips()
    {
        var record = Record("aaaa1111");
        Ledger().Save(record);

        Assert.Equal(record, new JsonInstallLedger(_fs, Path).Get("aaaa1111"));
    }

    [Fact]
    public void Save_replaces_an_existing_entry_instead_of_duplicating_it()
    {
        var ledger = Ledger();
        ledger.Save(Record("aaaa1111", "Game A"));
        ledger.Save(Record("aaaa1111", "Game A renamed"));

        var reloaded = new JsonInstallLedger(_fs, Path);
        Assert.Single(reloaded.All());
        Assert.Equal("Game A renamed", reloaded.Get("aaaa1111")!.GameTitle);
    }

    [Fact]
    public void Two_games_coexist_without_shared_state()
    {
        var ledger = Ledger();
        ledger.Save(Record("aaaa1111", "Game A"));
        ledger.Save(Record("bbbb2222", "Game B"));

        var reloaded = new JsonInstallLedger(_fs, Path);
        Assert.Equal(2, reloaded.All().Count);
        Assert.NotEqual(reloaded.Get("aaaa1111")!.BackupRoot, reloaded.Get("bbbb2222")!.BackupRoot);
    }

    [Fact]
    public void Remove_deletes_only_the_named_entry()
    {
        var ledger = Ledger();
        ledger.Save(Record("aaaa1111"));
        ledger.Save(Record("bbbb2222"));

        Assert.True(new JsonInstallLedger(_fs, Path).Remove("aaaa1111"));

        var reloaded = new JsonInstallLedger(_fs, Path);
        Assert.Null(reloaded.Get("aaaa1111"));
        Assert.NotNull(reloaded.Get("bbbb2222"));
    }

    [Fact]
    public void Remove_unknown_fingerprint_returns_false()
    {
        Assert.False(Ledger().Remove("deadbeef"));
    }

    [Fact]
    public void SetState_changes_only_the_state()
    {
        var record = Record("aaaa1111");
        Ledger().Save(record);

        Assert.True(new JsonInstallLedger(_fs, Path).SetState("aaaa1111", InstallState.RolledBack));

        var updated = new JsonInstallLedger(_fs, Path).Get("aaaa1111")!;
        Assert.Equal(InstallState.RolledBack, updated.State);
        Assert.Equal(record.LatestPlanId, updated.LatestPlanId);
        Assert.Equal(record.GameTitle, updated.GameTitle);
    }

    [Fact]
    public void SetState_on_unknown_fingerprint_returns_false()
    {
        Assert.False(Ledger().SetState("deadbeef", InstallState.Failed));
    }

    [Fact]
    public void Fingerprint_matching_is_case_insensitive()
    {
        Ledger().Save(Record("AaAa1111"));

        Assert.NotNull(new JsonInstallLedger(_fs, Path).Get("aaaa1111"));
    }

    [Fact]
    public void Corrupt_ledger_file_falls_back_to_empty_instead_of_throwing()
    {
        _fs.AddFile(Path, new MockFileData("<<< not json >>>"));

        Assert.Empty(Ledger().All());
    }

    [Fact]
    public void Save_creates_the_parent_directory()
    {
        Ledger().Save(Record("aaaa1111"));

        Assert.True(_fs.File.Exists(Path));
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter JsonInstallLedgerTests`
Expected: 编译错误 `JsonInstallLedger 不存在`

- [ ] **Step 4: 写 `Persistence/JsonInstallLedger.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Serialization;

namespace Deployer.Core.Persistence;

public sealed class JsonInstallLedger : IInstallLedger
{
    private readonly IFileSystem _fs;
    private readonly string _path;

    public JsonInstallLedger(IFileSystem fs, string path)
    {
        _fs = fs;
        _path = path;
    }

    /// <summary>默认位置：%LOCALAPPDATA%\OptiScalerDeployer\ledger.json</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OptiScalerDeployer", "ledger.json");

    public IReadOnlyList<InstallRecord> All() => Load();

    public InstallRecord? Get(string fingerprint) =>
        Load().FirstOrDefault(r => Matches(r, fingerprint));

    public void Save(InstallRecord record)
    {
        var all = Load();
        all.RemoveAll(r => Matches(r, record.Fingerprint));
        all.Add(record);
        Persist(all);
    }

    public bool Remove(string fingerprint)
    {
        var all = Load();
        var removed = all.RemoveAll(r => Matches(r, fingerprint));
        if (removed > 0)
        {
            Persist(all);
        }

        return removed > 0;
    }

    public bool SetState(string fingerprint, InstallState state)
    {
        var all = Load();
        var index = all.FindIndex(r => Matches(r, fingerprint));
        if (index < 0)
        {
            return false;
        }

        all[index] = all[index] with { State = state };
        Persist(all);
        return true;
    }

    private static bool Matches(InstallRecord record, string fingerprint) =>
        string.Equals(record.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);

    private List<InstallRecord> Load()
    {
        try
        {
            if (!_fs.File.Exists(_path))
            {
                return new List<InstallRecord>();
            }

            return DeployerJson.Deserialize<List<InstallRecord>>(_fs.File.ReadAllText(_path))
                .Where(r => !string.IsNullOrWhiteSpace(r.Fingerprint))
                .ToList();
        }
        catch (Exception)
        {
            // 台账损坏不能阻止启动；最坏情况是用户看不到已装列表，但 manifest 仍在磁盘上，
            // CLI 的 rollback --manifest 依旧可用。容忍的代价是下一次 Save 会覆写掉坏文件；
            // 这是可接受的，因为台账只是**索引**而不是真相源——每个游戏的
            // backups\<fingerprint>\<planId>\manifest.json 才是，ListPlanIds 能重新找到它们。
            // 注意与 BackupStore.ReadJson 的区别：那边是「拿不回游戏」的路径，必须响。
            return new List<InstallRecord>();
        }
    }

    private void Persist(List<InstallRecord> records)
    {
        var dir = _fs.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        _fs.File.WriteAllText(_path, DeployerJson.Serialize(records));
    }
}
```

注意 `DefaultPath` 里写的是 `System.IO.Path.Combine` 而不是 `Path.Combine`：本类有一个名为 `_path` 的字段，而 C# 的名称解析会让裸 `Path` 在类内部优先匹配…实际上不会（字段叫 `_path`），但显式写全 `System.IO.Path` 能避免下一次重命名时踩坑。其余类里的 `Path` 保持简写。

- [ ] **Step 5: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 146`

- [ ] **Step 6: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add json install ledger keyed by game fingerprint"
```

---

## Task 14: 端到端测试 — 已实现

实现与测试：[EndToEndTests.cs](../../../tools/OptiScalerDeployer/tests/Deployer.Core.Tests/EndToEndTests.cs)。测试覆盖不同游戏布局的 plan → apply → rollback 字节级往返、同一 PlanId 重放保护及隔离状态。该任务包含在最终验证中；具体计数见 Task 16。原始测试片段已移除，新增测试以当前源码为准。

## Task 15: 最小 CLI（端到端验证入口）— 已实现

实现：[Program.cs](../../../tools/OptiScalerDeployer/src/Deployer.Cli/Program.cs)；使用说明：[README.md](../../../tools/OptiScalerDeployer/README.md)；验证：[CliTests.cs](../../../tools/OptiScalerDeployer/tests/Deployer.Core.Tests/CliTests.cs)。CLI 提供 `plan`、`apply`、`rollback`、`ledger` 四个命令；参数按子命令校验，不支持 `--force`。测试覆盖真实上游 OptiScaler.ini 的本地磁盘处理、冲突与阻塞计划、台账读写失败、回滚和退出码。CLI 当前以 `CancellationToken.None` 调用 Core；中断时应使用 manifest 手动回滚。最终 CLI 定向测试 12 项通过，具体全量结果见 Task 16。

## Task 16: 文档收尾与最终验证

- [x] 同步 spec 持久化类型：`IdentityDecision` 六字段、`FileWrite.InlineContent` / 派生 `IsInline`、`BackupEntry.Sha256`，以及默认空的 `DeploymentManifest.CreatedDirectories`。
- [x] 记录真实执行顺序：先写空 manifest；全部备份完成并校验后一次发布完整回滚清单（含预期新建目录）；再写游戏文件。progress 只供诊断。
- [x] 记录备份长度与 SHA256 校验、原子恢复、同 PlanId 重放拒绝、路径约束及恢复失败时保护原件。
- [x] 记录自动 Prune 已移除：`BackupStore.Prune` 仅为显式 API；新 PlanId 的多次部署需按时间逆序回滚，避免删除恢复链。
- [x] 同步 Profile、ledger、CLI 四子命令的数据保护和错误处理行为，并说明 CLI 不支持 `--force`。
- [x] README 给出 CLI 使用方法、路径一致性要求、退出码与实际限制；说明 CLI 使用 `CancellationToken.None`，中断后可依 manifest 手动回滚。
- [x] 说明 Plan A 已实现范围和未完成的 Plan B scanners、Plan C WPF、Plan D elevation / Releases / health checks；真实 GPU 与游戏运行验收仍未执行。
- [x] 最终验证（协调者执行）：Debug build 成功，0 warnings / 0 errors；Release build 成功，0 warnings / 0 errors；Release 全量测试 230 passed / 0 failed / 0 skipped。定向组：Tasks 7–9/14 为 58，Tasks 10–12 为 35，Ledger 为 14，CLI 为 12，均通过。
- [ ] Commit（本次尚未提交）。

原始实现片段、阶段性测试数字和历史步骤仅作为过程记录；与当前实现冲突时，以源码、README 和本节验证结果为准。未执行的红阶段与 Commit 不补勾。

## Self-Review

Plan A 的实现、文档同步和最终构建/测试已完成。设计中剩余能力属于后续 Plan B/C/D；未执行真实游戏与 GPU 手工验收，也未提交更改。

## Execution Handoff

后续可以按设计进入 Plan B 的扫描与体检工作。本计划不包含提交记录。