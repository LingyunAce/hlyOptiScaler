# OptiScaler Deployer — Plan A: Core 引擎 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建 OptiScaler Deployer 的核心引擎 —— 一个无 UI、可完全单测的 C# 类库，能对任意游戏目录**安全部署**（影子备份 + 原子写 + 写入顺序保证）并**精确回滚** OptiScaler，配一个最小 CLI 做端到端验证。

**Architecture:** 纯类库 `Deployer.Core`，模块间单向依赖，`DeploymentPlanner` 是唯一汇聚点。核心支点是 **Plan 与 Execute 严格分离**：`DeploymentPlan` 是不可变纯数据 record，由 planner 纯函数计算；executor 只无脑执行。所有文件系统访问经 `System.IO.Abstractions` 的 `IFileSystem` 注入，因此全部测试跑在内存文件系统上，无需真游戏、无需真 OptiScaler 产物。

**Tech Stack:** .NET 8 / C# 12 / xUnit / System.IO.Abstractions（+ TestingHelpers 的 `MockFileSystem`）/ System.Text.Json / `dotnet` CLI

**Spec:** `docs/superpowers/specs/2026-10-08-optiscaler-deployer-design.md`（§5 部署计划、§6 身份决策、§8 Profile 与 INI、§11 测试策略）

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
│   │   │   ├── DeploymentProgress.cs     progress.json 形状（崩溃续做回滚）
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
        ├── WindowsFileVersionReaderTests.cs  全套里唯一碰真文件系统的测试（FileVersionInfo 用不了 MockFileSystem）
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
        └── EndToEndTests.cs              plan → apply → rollback 逐字节一致
```

**分解原则**：一个文件一个职责；`Domain/` 下全是无行为的 record/enum（唯一例外 `DllIdentityMap`，因为身份↔文件名的映射必须只有一份）；每个模块的接口与实现分文件，测试只依赖接口。

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

`FileVersionInfo` 走的是 Win32 版本资源 API，作用不到 `MockFileSystem` 上——这正是 Step 4 抽 `IFileVersionReader` 的理由，也是本文件成为全套里**唯一**碰真文件系统的测试的原因。只测否定路径：仓库里没有 PE 二进制夹具，正向路径无从造起；而否定路径是确定性的，也恰好就是 Task 7 保守分支依赖的契约（「读不出来 → 返回 null，绝不抛」→ 该槽位被当作游戏自带文件，宁可收进影子备份也不静默覆盖）。

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
- **零行内注释**：所有 `;` 注释都独占一行。因此「替换 `=` 右侧」不会误删注释。**这条事实只对当前这份仓库根的 `OptiScaler.ini` 成立**：被 patch 的 ini 来自 `Payload.RootDirectory`，而 `PayloadOrigin` 枚举含 `manualDirectory`（用户可以指定任意本地目录作为 payload），换一份 ini 不保证仍然零行内注释——参见下面「行尾注释会被一并替换掉」那条已知限制
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
    /// 注意**注释行算内容**：真 OptiScaler.ini 里很多 section 末尾有 `; ----` 横幅，
    /// 新键会落在横幅**之后**而不是紧贴最后一个真实键。这是有意的——横幅在语义上属于
    /// 它上面那个 section 的收尾，把新键插在横幅与它描述的键之间才是真的拆散了注释。
    /// 全是空白行时返回 -1；调用处那个 `>= 0` 判断在当前所有路径下恒为真（三个写入点
    /// 都保证了非负），留着是防将来有人新增一个可能写入 -1 的路径。
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

**插入之后必须把插入点之后所有 section 的锚点整体前移一行**：`sectionAppendAnchor` 里的下标是扫描阶段对着**未修改**的 `lines` 记下来的，而追加阶段用 `lines.Insert(anchorLine + 1, …)` 改的是同一个列表。插入使插入点之后的每一行都下移一行，所以**每一个** `>= anchorLine + 1` 的锚点都过期了，不只是当前 section 的那一个。只更新当前 section 的话，后面所有 section 的锚点各偏差 1，偏差还会随追加次数累积：偏差 ≥ 1 时新键不再落在 section 末尾，还可能插进一个注释块与它所解释的键之间把两者拆散（在一份 55% 是注释的文件里这是用户可见的退化）；偏差累积到超过该 section 的长度时，新键会落到 section 头**之前**，也就是写进了上一个 section——直接击穿类头 `<remarks>` 第 2 条声称的 section 作用域硬性要求。实测：输入 `[Upscalers]\r\nDx12Upscaler=auto\r\n\r\n[FrameGen]\r\nEnabled=auto\r\n` + edits `(Upscalers,New1,1) (Upscalers,New2,2) (FrameGen,New3,3)`，`New3=3` 会跑到 `[FrameGen]` 头之前、也就是写进了 `[Upscalers]`。实现上必须先 `sectionAppendAnchor.ToArray()` 拿快照再遍历：`Dictionary` 的枚举器会校验 `_version`，而索引器赋值会递增它，边枚举边写会抛 `InvalidOperationException`。section 总数最多几十个，整体扫一遍的代价可以忽略。「新建 section」分支走的是 `lines.Add(...)`，往列表末尾追加不会让任何既有锚点失效，所以**不需要**这个处理。回归看守是 `Appends_into_two_different_existing_sections_both_land_at_their_own_section_end`（既有的追加测试全都只碰一个 section，盖不到）。

**`LastContentLineIndex` 存在的理由**：若直接用「下一个 section 头的前一行」作为插入点，而那一行是分隔用的空行，新键就会落在空行之后、与 section 本体隔开。功能上仍正确（还在同一个 section 里），但人读起来像是另一个区块。注意它只跳**纯空白行**，`; ----` 这样的注释横幅算内容、会成为锚点，所以新键落在横幅**之后**——这是有意的，横幅在语义上属于它上面那个 section 的收尾，把新键插在横幅与它描述的键之间才是真的拆散了注释。它在全是空白行时返回 `-1`，而调用处那个 `anchorLine >= 0` 判断在当前所有路径下恒为真（三个写入点都保证了非负），是一段死防御，留着只为防将来新增一个可能写入 `-1` 的路径。

**section 头怎么解析**：判定抽在私有方法 `TryReadSectionHeader` 里，规则与通行 INI 解析器对齐——以 `[` 开头、扫到**第一个** `]` 为止，`]` 之后的内容（行内注释）一律忽略，内侧空白 trim 掉；没有 `]` 就返回 `false`，整行由调用方跳过。

为什么 `]` 之后允许有内容：`[Log] ; 只写日志` 是完全合法的 INI 写法。旧判定 `trimmed.StartsWith('[') && trimmed.TrimEnd().EndsWith(']')` 要求 `]` 必须在行尾，于是这一行既不是 section 头、又没有 `=`，被当成空行跳过——`currentSection` 保持不变，后面所有键被归到**上一个** section 名下。两个后果都是静默的：其一，针对 `A` 段的 edit 会改到物理上位于 `B` 段的那个同名键（实测 `[A]\r\nX=1\r\n[B] ; note\r\nK=2\r\n` + edit `(A,K,"9")` 会把 `B` 段的 `K=2` 改成 `K=9`）；其二，针对 `B` 段的 edit 匹配不上，于是往文件末尾追加出**第二个** `[B]` 段。可达性：仓库根的真 `OptiScaler.ini` 37 个 section 头全部干净，但被 patch 的 ini 来自 `Payload.RootDirectory`，而 `PayloadOrigin` 含 `manualDirectory`——用户可以指定任意本地目录作为 payload。

为什么没有 `]` 时整行跳过而不是当键解析：`[Foo=bar` 会被 `IndexOf('=')` 解析成键名 `[Foo`，那是在往游戏文件里制造一个谁也不认识的键。这是一处**有意的行为改变**（旧实现会当键解析）。其余分支与旧实现逐一核对过等价：`[ Section ]` → trim 后 `Section`；`[]` → 空 section 名；`[Sec[tion]` → 取第一个 `]`，得 `Sec[tion`（旧的 `[1..^1]` 对同一输入也得 `Sec[tion`）。

**为什么空 Section / 空 Key 是硬错误而不是「匹配不上就跳过」**：`currentSection` 初值是 `string.Empty`，一条 Section 为空白的 edit 会静默命中第一个 section 头之前的前言区；若没命中，就落进「新建 section」分支，往用户的 `OptiScaler.ini` 末尾追加一个 `[]` 段——那是往游戏文件里写垃圾，直接违背「绝不损坏游戏文件」这条第一承诺。Key 为空白同理，会命中任何以 `=` 开头的畸形行。失效方向是**静默写入垃圾**，所以必须抛 `ArgumentException` 而不是当作未命中。守卫放在 `IniPatcher` 而非调用方：它是真正落笔改游戏文件的那一层，而 edits 可能来自用户自己写的 profile JSON（Task 5 的 `JsonProfileStore` 从磁盘读），放在这最后一道关口才能同时覆盖 builtins、用户 JSON 与任何未来的调用方；Task 9 的 `BuildIniEdits` 因此不再重复校验。

**为什么先 Trim 归一化 Section / Key，而空白仍然抛**：守卫循环之前先把每条 edit 的 `Section` / `Key` 各自 `Trim()` 一遍，之后所有逻辑（守卫、`pending`、`orderedKeys`）都用归一化后的列表，返回的 `AppliedEdits` / `AppendedEdits` 里也是归一化后的值。不归一化的话 `" Log "` 能通过 `IsNullOrWhiteSpace` 守卫，却匹配不上扫描期从文件读出的 `"Log"`（`OrdinalIgnoreCase` 救不了空格差异），于是掉进「新建 section」分支往文件末尾追加一个字面的 `[ Log ]` 段——而 INI 解析器读 `[ Log ]` 时会 trim 内侧空格得到 `Log`，游戏文件里于是出现**两个同名 section**、值互相冲突。这与上面那段守卫点名要防的事（往 ini 末尾追加一个 `[]` 段）是同一个失效模式，只是入口从「空白」换成了「前后带空白」；只拦空白等于守卫只挡了一半，「同时覆盖 builtins、用户 JSON 与任何未来的调用方」那句也就不成立。

为什么这里选 Trim 而空白仍然抛 `ArgumentException`：两者防的是不同的东西。前后多打空格是**意图明确、错误可恢复**的——用户想改的就是 `Log` 段，Trim 之后的行为正是他想要的，不该让整次部署失败；而 Section / Key 全空白是**意图不可恢复**的，无从猜测用户想改哪个段哪个键，只能报错让他改 profile。注意 `Blank_key_is_rejected` 传的 `"   "` 经 Trim 后变成 `""`，仍会被守卫拒绝，那条测试继续通过。

**为什么 `SplitLines` 改成按 `\n` 切再剥掉行尾 `\r`**：若按检测到的主导 `newLine` 切，一个以 CRLF 为主、却夹了几行 bare LF 的文件，那几行会被并进相邻的同一个「行」字符串里。例如 `[Upscalers]\r\nFoo=1\nDx12Upscaler=auto\r\n` 会被切成 `["[Upscalers]", "Foo=1\nDx12Upscaler=auto"]`，于是针对 `Dx12Upscaler` 的 edit 实际匹配到的是 `Foo`。失效方向是**静默漏改**：`Join` 能原样还原所以文件不会被写坏，但「配置改了却没生效」是最难排查的一类失败。代价是输出会把零星 bare LF 归一化成主导行尾——这是对「最小干预」的一次有意识让步，换取的是「不会静默漏改」。仓库根目录的真 `OptiScaler.ini` 实测 `crlf=1571 bareLf=0`，但**这不能推广到实际被 patch 的那份文件**：被 patch 的 ini 来自 `Payload.RootDirectory`，而 `PayloadOrigin` 枚举含 `manualDirectory`——用户可以指定任意本地目录作为 payload，那份 ini 可能来自任何地方（被用户手改过、从别的机器拷来、被某个编辑器重存过）；而且不同 OptiScaler 版本的 ini 行尾也不保证一致。加固本身仍然正确，只是威胁模型要说准，不能窄化成「用户拿 Unix 编辑器手改过」这一条路径；纯 CRLF 与纯 LF 两种文件在改动前后行为完全一致（加固改动前的既有测试全部保持绿）。

**这条 `ArgumentException` 在下游怎么呈现**：它从 `IniPatcher.Patch` 抛出，穿过纯函数 planner、executor，最终由 Task 15 CLI 顶层那句 `return Fail(ex.Message, ex)` 打到 stderr——消息里带着 `Section=""`/`Key=""` 的实际值，正好是用户手改 profile JSON 时定位错误所需的线索，所以**不需要**再包一层 `DeployerException`（包了只会把这条线索换成一句更泛的话术）。计划 C 的 WPF UI 需遵守同一条：把这个异常的消息原文呈现给用户，而不是显示成「未知错误」。

**`SplitLines` 会补一个尾换行**：若输入不以换行结尾，`Join` 后输出会多一个（例如 `"[A]\nK=1"` → `"[A]\nK=1\n"`）。这对 INI 无害（OptiScaler 的 SimpleIni 不关心），但计划 B 写「真 INI 回归测试」时必须明确断言口径：要么断言 `Patch(真文件)` 的输出以换行结尾，要么先确认真文件本来就以换行结尾（实测 1571 行 CRLF，是）。别把这条当成 bug 去「修」。这条已知行为已由 `Input_without_trailing_newline_gains_one_when_edits_are_applied` 钉住（空 edits 走早返回分支，不补）。

**匹配到的键行，其 `=` 右侧的行尾注释会被一并替换掉**：`K=1 ; keep me` 被改成 `K=9`，`; keep me` 消失。这在契约上不算违约（`IIniPatcher` 承诺的是「所有**以** `;` 或 `#` **开头**的行原样保留」，行尾注释不在承诺范围内），且真 payload ini 实测零行内注释，故当前不可达。**不做保留是有意的**：通行 INI 解析器很可能把值后面的 `;` 视为值的一部分，拼回去会让 OptiScaler 读到 `"9 ; keep me"` 而解析失败——那比丢一条注释严重得多，失效方向更糟。（`external/simpleini` 是未初始化的子模块，本地无源码可查证，所以这里不凭猜测改行为。）

- [x] **Step 5: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter IniPatcherTests`
Expected: `失败: 0，通过: 23`

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

- [ ] **Step 1: 写 `BuiltinProfiles` 的失败测试**

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

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter BuiltinProfilesTests`
Expected: 编译错误 `BuiltinProfiles 不存在`

- [ ] **Step 3: 写 `Profiles/BuiltinProfiles.cs`**

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
    /// 帧生成附加档。用户勾选后才叠加到基础 Profile 之上，绝不默认开启。
    /// HUDFix 必须开：帧生成场景下 HUD 会产生鬼影（spec §1 的 OptiScaler 能力清单）。
    /// </summary>
    public static IReadOnlyList<IniEdit> FrameGenAddOn(bool useStreamlineInput)
    {
        var edits = new List<IniEdit>
        {
            new("FrameGen", "Enabled", null, "true"),
            new("FrameGen", "FGInput", null, useStreamlineInput ? "dlssg" : "upscaler"),
            new("FrameGen", "FGOutput", null, "fsrfg"),
            new("FSR", "FGIndex", null, "0"),
            new("OptiFG", "HUDFix", null, "true"),
        };

        return edits;
    }
}
```

注意 `[OptiFG] HUDFix` 这个键名要在计划 D 之前对着真 `OptiScaler.ini` 核一遍（该 section 有 22 个 HUDFix 相关键）。若键名不符，`IniPatcher` 的行为是**追加**而非报错，游戏里表现为「该项不生效」而不是崩溃——这正是 spec §13 列的上游键名变动缓解措施。核对命令：

```powershell
cd "d:\AI\project\hlyOptiScaler"
Select-String -Path OptiScaler.ini -Pattern '^\[OptiFG\]' -Context 0,40 | ForEach-Object { $_.Context.PostContext }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter BuiltinProfilesTests`
Expected: `失败: 0，通过: 9`

- [ ] **Step 5: 写 `Profiles/IProfileStore.cs`**

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

- [ ] **Step 6: 写 `JsonProfileStore` 的失败测试**

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

- [ ] **Step 7: 写 `Profiles/JsonProfileStore.cs`**

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

- [ ] **Step 8: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 30`

- [ ] **Step 9: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add builtin upscaler profiles and json profile store"
```

---

## Task 6: Payload 来源 + 测试夹具工厂

Spec §9。本计划只实现 `LocalBuildPayloadSource`（GitHub Releases 属计划 D，需要 SharpCompress 与网络）。

**Files:**
- Create: `tests/Deployer.TestData/SyntheticPayloadFactory.cs`
- Create: `src/Deployer.Core/Payload/IPayloadSource.cs`
- Create: `src/Deployer.Core/Payload/LocalBuildPayloadSource.cs`
- Create: `src/Deployer.Core/Payload/PayloadSourceChain.cs`
- Create: `src/Deployer.Core/Payload/ManualDirectoryPayloadSource.cs`
- Test: `tests/Deployer.Core.Tests/LocalBuildPayloadSourceTests.cs`
- Test: `tests/Deployer.Core.Tests/PayloadSourceChainTests.cs`

- [ ] **Step 1: 写 `tests/Deployer.TestData/SyntheticPayloadFactory.cs`**

`x64\Release\a\` 在本机尚不存在（已核实 `Test-Path` 为 `False`），所以测试必须能自己造一份。这个工厂同时是「payload 布局」的可执行文档。

```csharp
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.TestData;

/// <summary>
/// 在 IFileSystem（通常是 MockFileSystem）上造一份与 OptiScaler.vcxproj PostBuildEvent
/// 输出布局一致的假 payload。
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
        @"OptiScaler\amd_fidelityfx_dx12.dll",
        @"OptiScaler\amd_fidelityfx_upscaler_dx12.dll",
        @"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll",
        @"OptiScaler\amd_fidelityfx_vk.dll",
        @"OptiScaler\amdxc64.dll",
        @"OptiScaler\libxess.dll",
        @"OptiScaler\Licenses\FidelityFX_v2_LICENSE.md",
        @"OptiScaler\D3D12_OptiScaler\d3d12core.dll",
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

- [ ] **Step 2: 写 `Payload/IPayloadSource.cs`**

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

- [ ] **Step 3: 写 `LocalBuildPayloadSource` 的失败测试**

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

- [ ] **Step 5: 写 `Payload/LocalBuildPayloadSource.cs`**

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

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter LocalBuildPayloadSourceTests`
Expected: `失败: 0，通过: 6`

- [ ] **Step 7: 写 `Payload/ManualDirectoryPayloadSource.cs`**

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

- [ ] **Step 8: 写 `Payload/PayloadSourceChain.cs`**

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

- [ ] **Step 9: 写 `PayloadSourceChainTests.cs`**

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

- [ ] **Step 10: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 41`

- [ ] **Step 11: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add payload sources and synthetic payload fixture"
```

---

## Task 7: DLL 身份决策

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

## Task 10: 影子备份与原子写

Spec §5.5 / §5.6。

**对 Task 2 的第三处类型修正**：`DeploymentManifest` 需要一个 `CreatedDirectories` 字段。没有它，回滚只能删文件、删不掉自己新建的空目录（`OptiScaler\`、`OptiScaler\D3D12_OptiScaler\`、`Licenses\`），回滚后的目录就不是「逐字节一致」了。修改 `src/Deployer.Core/Domain/DeploymentManifest.cs`，在 `WrittenPaths` 后追加：

```csharp
    /// <summary>本次部署新建的目录（不包含本来就存在的）。回滚时自底向上删除已空的这些目录。</summary>
    public required IReadOnlyList<string> CreatedDirectories { get; init; }
```

同时把 Task 3 Step 7 的 `Manifest_roundtrips` 测试里补上 `CreatedDirectories = new[] { @"C:\game\OptiScaler" },`。

**Files:**
- Modify: `src/Deployer.Core/Domain/DeploymentManifest.cs`
- Modify: `tests/Deployer.Core.Tests/DeployerJsonTests.cs`
- Create: `src/Deployer.Core/Deployment/AtomicFileWriter.cs`
- Create: `src/Deployer.Core/Deployment/IBackupStore.cs`
- Create: `src/Deployer.Core/Deployment/BackupStore.cs`
- Test: `tests/Deployer.Core.Tests/AtomicFileWriterTests.cs`
- Test: `tests/Deployer.Core.Tests/BackupStoreTests.cs`

- [ ] **Step 1: 写 `AtomicFileWriterTests.cs`**

```csharp
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class AtomicFileWriterTests
{
    private const string SourcePath = @"D:\payload\OptiScaler\amdxc64.dll";
    private const string DestPath = @"C:\game\OptiScaler\amdxc64.dll";

    private readonly MockFileSystem _fs = new();

    private FileWrite CopyWrite(string content = "PAYLOAD BYTES")
    {
        _fs.AddFile(SourcePath, new MockFileData(Encoding.UTF8.GetBytes(content)));
        return new FileWrite(
            SourcePath, DestPath, Encoding.UTF8.GetByteCount(content), Hashing.Sha256OfString(content));
    }

    private FileWrite InlineWrite(string content = "[Upscalers]\r\nDx12Upscaler=ffx\r\n") =>
        new(SourcePath, DestPath, Encoding.UTF8.GetByteCount(content),
            Hashing.Sha256OfString(content), content);

    [Fact]
    public void Copy_write_lands_the_file_with_expected_bytes()
    {
        var writer = new AtomicFileWriter(_fs);

        writer.Write(CopyWrite());

        Assert.Equal("PAYLOAD BYTES", _fs.File.ReadAllText(DestPath));
    }

    [Fact]
    public void Inline_write_lands_inline_content_not_the_source_file()
    {
        _fs.AddFile(SourcePath, new MockFileData("ORIGINAL INI"));
        var writer = new AtomicFileWriter(_fs);

        writer.Write(InlineWrite());

        Assert.Equal("[Upscalers]\r\nDx12Upscaler=ffx\r\n", _fs.File.ReadAllText(DestPath));
    }

    [Fact]
    public void Creates_missing_parent_directories_and_reports_them()
    {
        var writer = new AtomicFileWriter(_fs);

        var created = writer.Write(CopyWrite());

        Assert.Contains(@"C:\game\OptiScaler", created, StringComparer.OrdinalIgnoreCase);
        Assert.True(_fs.Directory.Exists(@"C:\game\OptiScaler"));
    }

    [Fact]
    public void Does_not_report_directories_that_already_existed()
    {
        _fs.AddDirectory(@"C:\game\OptiScaler");
        var writer = new AtomicFileWriter(_fs);

        var created = writer.Write(CopyWrite());

        Assert.Empty(created);
    }

    [Fact]
    public void No_temp_or_bak_file_survives_a_successful_write()
    {
        var writer = new AtomicFileWriter(_fs);

        writer.Write(CopyWrite());

        Assert.Empty(_fs.Directory.GetFiles(@"C:\game\OptiScaler", "*.optideploy.*"));
    }

    [Fact]
    public void Existing_destination_is_overwritten()
    {
        _fs.AddFile(DestPath, new MockFileData("OLD CONTENT THAT IS LONGER"));
        var writer = new AtomicFileWriter(_fs);

        writer.Write(CopyWrite());

        Assert.Equal("PAYLOAD BYTES", _fs.File.ReadAllText(DestPath));
    }

    [Fact]
    public void Checksum_mismatch_on_inline_content_throws_and_leaves_no_partial_file()
    {
        _fs.AddFile(SourcePath, new MockFileData("ORIGINAL INI"));
        var writer = new AtomicFileWriter(_fs);
        var tampered = new FileWrite(SourcePath, DestPath, 100,
            Hashing.Sha256OfString("PAYLOAD BYTES"), "完全不同的内容");

        Assert.Throws<ChecksumMismatchException>(() => writer.Write(tampered));
        Assert.False(_fs.File.Exists(DestPath));
        Assert.False(_fs.Directory.Exists(@"C:\game\OptiScaler")
            && _fs.Directory.GetFiles(@"C:\game\OptiScaler", "*.optideploy.*").Length > 0);
    }

    [Fact]
    public void Checksum_mismatch_on_copy_throws()
    {
        _fs.AddFile(SourcePath, new MockFileData("REAL PAYLOAD"));
        var writer = new AtomicFileWriter(_fs);
        var wrong = new FileWrite(SourcePath, DestPath, 12, Hashing.Sha256OfString("something else"));

        Assert.Throws<ChecksumMismatchException>(() => writer.Write(wrong));
        Assert.False(_fs.File.Exists(DestPath));
    }

    [Fact]
    public void Missing_source_file_throws_file_not_found()
    {
        var writer = new AtomicFileWriter(_fs);
        var write = new FileWrite(@"D:\payload\nope.dll", DestPath, 1, Hashing.Sha256OfString("x"));

        Assert.Throws<FileNotFoundException>(() => writer.Write(write));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter AtomicFileWriterTests`
Expected: 编译错误 `AtomicFileWriter 不存在`

- [ ] **Step 3: 写 `Deployment/AtomicFileWriter.cs`**

```csharp
using System.IO.Abstractions;
using System.Text;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Util;

namespace Deployer.Core.Deployment;

/// <summary>
/// spec §5.5 的单文件原子写：tmp → 校验 SHA256 → File.Replace。
/// 本类**不负责**备份：备份必须在调用前完成（否则 File.Replace 的 .bak 会变成第二份副本，
/// 而原件已在影子目录里）。
/// </summary>
public sealed class AtomicFileWriter
{
    private const string TmpSuffix = ".optideploy.tmp";
    private const string BakSuffix = ".optideploy.bak";

    private readonly IFileSystem _fs;

    public AtomicFileWriter(IFileSystem fs) => _fs = fs;

    /// <summary>写入一个文件。返回本次**新建**的目录（本来就存在的不算）。</summary>
    public IReadOnlyList<string> Write(FileWrite write)
    {
        var created = EnsureDirectories(write.DestinationPath);
        var tmp = write.DestinationPath + TmpSuffix;

        try
        {
            if (write.IsInline)
            {
                _fs.File.WriteAllText(tmp, write.InlineContent!,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            else
            {
                if (!_fs.File.Exists(write.SourcePath))
                {
                    throw new FileNotFoundException($"payload 文件不存在：{write.SourcePath}", write.SourcePath);
                }

                _fs.File.Copy(write.SourcePath, tmp, overwrite: true);
            }

            var actual = Hashing.Sha256OfFile(_fs, tmp);
            if (!string.Equals(actual, write.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChecksumMismatchException(tmp, write.Sha256, actual);
            }

            Commit(tmp, write.DestinationPath);
            return created;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private void Commit(string tmp, string destination)
    {
        if (!_fs.File.Exists(destination))
        {
            _fs.File.Move(tmp, destination);
            return;
        }

        var bak = destination + BakSuffix;
        TryDelete(bak);

        try
        {
            _fs.File.Replace(tmp, destination, bak);
            TryDelete(bak);   // 原件已在影子备份，不留第二份
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // File.Replace 跳卷、目标被占用、或文件系统不支持替换。
            // 原件已在影子备份里，所以直接删目标是安全的。
            _fs.File.Delete(destination);
            _fs.File.Move(tmp, destination);
            TryDelete(bak);
        }
    }

    private IReadOnlyList<string> EnsureDirectories(string destinationPath)
    {
        var dir = _fs.Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(dir))
        {
            return Array.Empty<string>();
        }

        var created = new List<string>();
        var current = dir;
        while (!string.IsNullOrEmpty(current) && !_fs.Directory.Exists(current))
        {
            created.Insert(0, current);
            var parent = _fs.Path.GetDirectoryName(current);
            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        _fs.Directory.CreateDirectory(dir);
        return created;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不能掩盖主异常
        }
    }
}
```

写 tmp 用 **不带 BOM 的 UTF8**。`OptiScaler.ini` 上游无 BOM，带 BOM 写回去会让 SimpleIni 把第一行 section 读成 `\uFEFF[Upscalers]`，直接导致整个 `[Upscalers]` 段失效。这是一个很容易踩、且症状很隐蔽的坑。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter AtomicFileWriterTests`
Expected: `失败: 0，通过: 9`

- [ ] **Step 5: 写 `Deployment/IBackupStore.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Deployment;

public interface IBackupStore
{
    /// <summary>&lt;backupRoot&gt;\&lt;fingerprint&gt;\&lt;planId&gt;\</summary>
    string DirectoryFor(string fingerprint, Guid planId);

    string ManifestPathFor(string fingerprint, Guid planId);

    string ProgressPathFor(string fingerprint, Guid planId);

    /// <summary>
    /// 把原件拷进影子目录，保持相对路径镜像。原件不存在则抛（计划已过期）。
    /// 返回带 <see cref="BackupEntry.Sha256"/> 的新 entry：哈希只能在真正拷贝的这一刻记下，
    /// 调用方必须把返回值（而不是传入的 entry）写进 manifest。
    /// 拷贝前后各算一次哈希，不符即抛 <c>ChecksumMismatchException</c>——备份阶段就烂掉的
    /// 备份件比没有备份更危险，因为它会让后续回滚看起来成功。
    /// </summary>
    BackupEntry Backup(BackupEntry entry);

    void WriteManifest(DeploymentManifest manifest);

    DeploymentManifest? ReadManifest(string manifestPath);

    void WriteProgress(DeploymentProgress progress);

    DeploymentProgress? ReadProgress(string progressPath);

    /// <summary>按 manifest.CreatedUtc 倒序返回该游戏已有的 planId。</summary>
    IReadOnlyList<Guid> ListPlanIds(string fingerprint);

    /// <summary>只保留最近 <paramref name="keep"/> 份，其余连目录一并删除。spec §5.6：keep = 3。</summary>
    IReadOnlyList<Guid> Prune(string fingerprint, int keep);
}
```

- [ ] **Step 6: 写 `BackupStoreTests.cs`**

```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class BackupStoreTests
{
    private const string Root = @"C:\backup-root";
    private const string Fingerprint = "a1b2c3d4";

    private readonly MockFileSystem _fs = new();
    private BackupStore Store() => new(_fs, Root);

    private static BackupEntry Entry(Guid planId, string name) => new(
        $@"C:\game\{name}",
        $@"{Root}\{Fingerprint}\{planId:D}\{name}",
        BackupReason.OverwriteGameFile);

    [Fact]
    public void DirectoryFor_uses_fingerprint_then_planid()
    {
        var planId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Assert.Equal($@"{Root}\{Fingerprint}\11111111-1111-1111-1111-111111111111",
            Store().DirectoryFor(Fingerprint, planId));
    }

    [Fact]
    public void Backup_copies_the_original_into_the_shadow_directory_without_touching_it()
    {
        _fs.AddFile(@"C:\game\winmm.dll", new MockFileData("GAME OWN WINMM"));
        var entry = Entry(Guid.NewGuid(), "winmm.dll");

        Store().Backup(entry);

        Assert.True(_fs.File.Exists(entry.BackupPath));
        Assert.Equal("GAME OWN WINMM", _fs.File.ReadAllText(entry.BackupPath));
        Assert.True(_fs.File.Exists(entry.OriginalPath), "备份不得删掉或移动原件");
    }

    [Fact]
    public void Backup_preserves_nested_relative_path()
    {
        _fs.AddFile(@"C:\game\OptiScaler\sub\x.dll", new MockFileData("X"));
        var entry = new BackupEntry(
            @"C:\game\OptiScaler\sub\x.dll",
            $@"{Root}\{Fingerprint}\{Guid.NewGuid():D}\OptiScaler\sub\x.dll",
            BackupReason.OverwritePreviousOpti);

        Store().Backup(entry);

        Assert.True(_fs.File.Exists(entry.BackupPath));
    }

    [Fact]
    public void Backup_of_missing_original_throws_instead_of_silently_skipping()
    {
        var entry = Entry(Guid.NewGuid(), "ghost.dll");

        Assert.Throws<FileNotFoundException>(() => Store().Backup(entry));
    }

    [Fact]
    public void Manifest_roundtrips_through_disk()
    {
        var store = Store();
        var planId = Guid.NewGuid();
        var manifest = TestManifest(planId, DateTime.UtcNow);

        store.WriteManifest(manifest);

        JsonAssert.Equivalent(manifest, store.ReadManifest(store.ManifestPathFor(Fingerprint, planId)));
    }

    [Fact]
    public void Backup_records_the_source_hash_so_rollback_can_verify_it()
    {
        _fs.AddFile(@"C:\game\winmm.dll", new MockFileData("GAME OWN WINMM"));
        var entry = Entry(Guid.NewGuid(), "winmm.dll");

        var returned = Store().Backup(entry);

        // 传进来的 entry 不变（planner 那份计划里 Sha256 仍是 null），返回值才带哈希。
        // executor 必须接住返回值写进 manifest，否则回滚只能盲拷。
        Assert.Null(entry.Sha256);
        Assert.Equal(Hashing.Sha256OfFile(_fs, entry.OriginalPath), returned.Sha256);
        Assert.Equal(entry.OriginalPath, returned.OriginalPath);
        Assert.Equal(entry.BackupPath, returned.BackupPath);
        Assert.Equal(entry.Reason, returned.Reason);
    }

    [Fact]
    public void ReadManifest_returns_null_for_missing_file()
    {
        Assert.Null(Store().ReadManifest($@"{Root}\nope\nope\manifest.json"));
    }

    [Fact]
    public void ReadManifest_throws_with_the_real_cause_when_the_file_exists_but_is_unreadable()
    {
        var store = Store();
        var path = store.ManifestPathFor(Fingerprint, Guid.NewGuid());
        _fs.AddFile(path, new MockFileData("{ 不是 JSON"));

        // 「存在但读不出来」绝不能和「不存在」一样返回 null：那样 RollbackExecutor
        // 只会说「备份清单损坏或被删除」，把真正的起因连同堆栈一起吞掉。
        // 「无法回滚」是用户唯一拿不回游戏的那条路径，它必须带着现场信息响。
        var ex = Assert.Throws<InvalidDataException>(() => store.ReadManifest(path));
        Assert.Contains(path, ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void ListPlanIds_still_lists_a_plan_whose_manifest_is_corrupt()
    {
        var store = Store();
        var good = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bad = Guid.Parse("22222222-2222-2222-2222-222222222222");
        store.WriteManifest(TestManifest(good, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        _fs.AddFile(store.ManifestPathFor(Fingerprint, bad), new MockFileData("{ 不是 JSON"));

        // 这是 ReadJson 严格化之后**唯一**的容忍点：一份坏清单不该把整个列表挡死，
        // 否则用户连还能用的备份都看不见。坏的那份排到最后，真去回滚它时才会响亮地失败。
        Assert.Equal(new[] { good, bad }, store.ListPlanIds(Fingerprint));
    }

    [Fact]
    public void Progress_roundtrips_through_disk()
    {
        var store = Store();
        var planId = Guid.NewGuid();
        var progress = new DeploymentProgress
        {
            PlanId = planId,
            ManifestPath = store.ManifestPathFor(Fingerprint, planId),
            CompletedSteps = new[] { @"C:\game\OptiScaler\amdxc64.dll" },
        };

        store.WriteProgress(progress);

        JsonAssert.Equivalent(progress, store.ReadProgress(store.ProgressPathFor(Fingerprint, planId)));
    }

    [Fact]
    public void ListPlanIds_is_newest_first()
    {
        var store = Store();
        var old = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var newer = Guid.Parse("22222222-2222-2222-2222-222222222222");
        store.WriteManifest(TestManifest(old, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        store.WriteManifest(TestManifest(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(new[] { newer, old }, store.ListPlanIds(Fingerprint));
    }

    [Fact]
    public void ListPlanIds_ignores_directories_that_are_not_guids()
    {
        var store = Store();
        store.WriteManifest(TestManifest(Guid.NewGuid(), DateTime.UtcNow));
        _fs.AddDirectory($@"{Root}\{Fingerprint}\not-a-guid");

        Assert.Single(store.ListPlanIds(Fingerprint));
    }

    [Fact]
    public void Prune_keeps_the_newest_n_and_deletes_the_rest()
    {
        var store = Store();
        var baseTime = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var ids = Enumerable.Range(0, 5)
            .Select(i => Guid.Parse($"0000000{i}-0000-0000-0000-000000000000"))
            .ToList();

        for (var i = 0; i < ids.Count; i++)
        {
            store.WriteManifest(TestManifest(ids[i], baseTime.AddMinutes(i)));
        }

        var removed = store.Prune(Fingerprint, keep: 3);

        Assert.Equal(2, removed.Count);
        Assert.Equal(3, store.ListPlanIds(Fingerprint).Count);
        Assert.False(_fs.Directory.Exists(store.DirectoryFor(Fingerprint, removed[0])));
    }

    [Fact]
    public void Prune_on_unknown_fingerprint_is_a_noop()
    {
        Assert.Empty(Store().Prune("deadbeef", keep: 3));
    }

    private static DeploymentManifest TestManifest(Guid planId, DateTime createdUtc) => new()
    {
        PlanId = planId,
        CreatedUtc = createdUtc,
        Fingerprint = Fingerprint,
        GameTitle = "Fake Game",
        DeployDirectory = @"C:\game",
        Payload = new PayloadInfo(PayloadOrigin.LocalBuild, "0.9.1.0", @"D:\payload"),
        Identity = new IdentityDecision(DllIdentity.Dxgi, "dxgi.dll", IdentityStatus.Resolved,
            Array.Empty<DllIdentity>(), null, null),
        Profile = BuiltinProfiles.AmdRdna4,
        Backups = Array.Empty<BackupEntry>(),
        WrittenPaths = Array.Empty<string>(),
        CreatedDirectories = Array.Empty<string>(),
    };
}
```

`Prune` 靠 `manifest.json` 里的 `CreatedUtc` 排序，而不是目录名或文件系统时间戳——后者在备份被拷贝/还原后会变，前者不会。

- [ ] **Step 7: 写 `Deployment/BackupStore.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Serialization;
using Deployer.Core.Util;

namespace Deployer.Core.Deployment;

public sealed class BackupStore : IBackupStore
{
    private const string ManifestFileName = "manifest.json";
    private const string ProgressFileName = "progress.json";

    private readonly IFileSystem _fs;
    private readonly string _root;

    public BackupStore(IFileSystem fs, string root)
    {
        _fs = fs;
        _root = root;
    }

    /// <summary>默认位置：%LOCALAPPDATA%\OptiScalerDeployer\backups</summary>
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OptiScalerDeployer", "backups");

    public string DirectoryFor(string fingerprint, Guid planId) =>
        _fs.Path.Combine(_root, fingerprint, planId.ToString("D"));

    public string ManifestPathFor(string fingerprint, Guid planId) =>
        _fs.Path.Combine(DirectoryFor(fingerprint, planId), ManifestFileName);

    public string ProgressPathFor(string fingerprint, Guid planId) =>
        _fs.Path.Combine(DirectoryFor(fingerprint, planId), ProgressFileName);

    public BackupEntry Backup(BackupEntry entry)
    {
        if (!_fs.File.Exists(entry.OriginalPath))
        {
            throw new FileNotFoundException(
                $"待备份的原件不存在：{entry.OriginalPath}。计划可能已过期，请重新生成。",
                entry.OriginalPath);
        }

        var dir = _fs.Path.GetDirectoryName(entry.BackupPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        // 拷贝**前**先算原件哈希：这是回滚时唯一的独立参照物，必须在原件还没被覆盖时拿到。
        var source = Hashing.Sha256OfFile(_fs, entry.OriginalPath);

        _fs.File.Copy(entry.OriginalPath, entry.BackupPath, overwrite: true);

        // 拷贝**后**再算备份件哈希。不符说明这次拷贝本身就没落地完整（磁盘满、杀软拦截、
        // 云占位符未水合、路径被重定向）。此时必须抛而不是继续：一份坏备份会让后续回滚
        // 「成功」地把坏文件装回游戏目录，比此刻失败难收拾得多。
        var copied = Hashing.Sha256OfFile(_fs, entry.BackupPath);
        if (!string.Equals(copied, source, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(entry.BackupPath, source, copied);
        }

        return entry.WithSha256(source);
    }

    public void WriteManifest(DeploymentManifest manifest) =>
        WriteJson(ManifestPathFor(manifest.Fingerprint, manifest.PlanId), manifest);

    public DeploymentManifest? ReadManifest(string manifestPath) => ReadJson<DeploymentManifest>(manifestPath);

    public void WriteProgress(DeploymentProgress progress)
    {
        var dir = _fs.Path.GetDirectoryName(progress.ManifestPath);
        if (dir is not null)
        {
            WriteJson(_fs.Path.Combine(dir, ProgressFileName), progress);
        }
    }

    public DeploymentProgress? ReadProgress(string progressPath) => ReadJson<DeploymentProgress>(progressPath);

    public IReadOnlyList<Guid> ListPlanIds(string fingerprint)
    {
        var gameRoot = _fs.Path.Combine(_root, fingerprint);
        if (!_fs.Directory.Exists(gameRoot))
        {
            return Array.Empty<Guid>();
        }

        var result = new List<(Guid Id, DateTime Created)>();
        foreach (var dir in _fs.Directory.GetDirectories(gameRoot))
        {
            if (!Guid.TryParse(_fs.Path.GetFileName(dir), out var planId))
            {
                continue;
            }

            // 这里**故意**容忍坏 manifest，是本类唯一的容忍点：只列出 planId 不该被其中
            // 一份坏清单挡死，否则用户连别的还能用的备份都看不见。坏的那份按
            // DateTime.MinValue 排到最后；真去回滚它时 ReadManifest 会带着内层异常响亮地失败。
            DateTime createdUtc;
            try
            {
                createdUtc = ReadJson<DeploymentManifest>(_fs.Path.Combine(dir, ManifestFileName))?.CreatedUtc
                             ?? DateTime.MinValue;
            }
            catch (Exception)
            {
                createdUtc = DateTime.MinValue;
            }

            result.Add((planId, createdUtc));
        }

        return result.OrderByDescending(x => x.Created).Select(x => x.Id).ToList();
    }

    public IReadOnlyList<Guid> Prune(string fingerprint, int keep)
    {
        var doomed = ListPlanIds(fingerprint).Skip(Math.Max(0, keep)).ToList();

        foreach (var planId in doomed)
        {
            var dir = DirectoryFor(fingerprint, planId);
            if (_fs.Directory.Exists(dir))
            {
                _fs.Directory.Delete(dir, recursive: true);
            }
        }

        return doomed;
    }

    private void WriteJson<T>(string path, T value)
    {
        var dir = _fs.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        _fs.File.WriteAllText(path, DeployerJson.Serialize(value));
    }

    private T? ReadJson<T>(string path) where T : class
    {
        if (!_fs.File.Exists(path))
        {
            // 文件不存在是**合法**状态：还没部署过，或已被 Prune 清掉。返回 null。
            return null;
        }

        try
        {
            return DeployerJson.Deserialize<T>(_fs.File.ReadAllText(path))
                ?? throw new InvalidDataException("内容是 JSON null。");
        }
        catch (Exception ex)
        {
            // 「存在但读不出来」与「不存在」是两件完全不同的事，绝不能都返回 null。
            // 混为一谈的代价：RollbackExecutor 只会说「备份清单损坏或被删除，需手工处理」，
            // 把真正的起因（JSON 半截写入、枚举名对不上、磁盘读失败）连同堆栈一起吞掉。
            // 「无法回滚」是用户唯一拿不回游戏的那条路径，它必须带着现场信息响。
            // 本方法**不**吞异常；唯一的容忍点在 ListPlanIds，且那里写了理由。
            throw new InvalidDataException($"读不出来：{path}", ex);
        }
    }
}
```

- [ ] **Step 8: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 113`

- [ ] **Step 9: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add atomic file writer and shadow backup store"
```

---

## Task 11: RollbackExecutor

先写回滚、再写执行器。顺序看着反常，但有两个硬理由：① `DeploymentExecutor` 失败时要**调用**回滚，先写它就得先留一个假实现再回头接；② spec §11.1 对回滚的断言最硬（「目录与执行前逐字节一致」），先把这个尺子造出来，执行器的测试才有参照。

回滚的两个触发点（spec §4）——健康检查失败自动触发（计划 D）与 UI 上「卸载」手动触发——走同一条路径。本任务只做这条路径本身。

**Files:**
- Create: `src/Deployer.Core/Deployment/IRollbackExecutor.cs`
- Create: `src/Deployer.Core/Deployment/RollbackExecutor.cs`
- Test: `tests/Deployer.Core.Tests/RollbackExecutorTests.cs`

- [ ] **Step 1: 写 `Deployment/IRollbackExecutor.cs`**

`RollbackResult` 已在 Task 2 Step 10 的 `DeploymentProgress.cs` 里定义。接口只需一个方法：

```csharp
namespace Deployer.Core.Deployment;

public interface IRollbackExecutor
{
    /// <summary>
    /// 按 manifest 精确逆操作。返回 Success=false 时不抛异常，
    /// 因为「回滚失败」必须能把已完成的还原清单交代给用户。
    /// </summary>
    RollbackResult Rollback(string manifestPath);
}
```

- [ ] **Step 2: 写 `RollbackExecutorTests.cs`**

```csharp
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class RollbackExecutorTests
{
    private const string Root = @"C:\backup-root";
    private const string Fingerprint = "a1b2c3d4";
    private const string DeployDir = @"C:\game\Binaries\Win64";

    private readonly MockFileSystem _fs = new();
    private readonly BackupStore _store;
    private readonly RollbackExecutor _rollback;

    public RollbackExecutorTests()
    {
        _store = new BackupStore(_fs, Root);
        _rollback = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), _store);
    }

    /// <summary>把整个目录的内容快照成 path → sha256，用来断言「逐字节一致」。</summary>
    private Dictionary<string, string> Snapshot(IFileSystem fs, string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!fs.Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in fs.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result[file] = Deployer.Core.Util.Hashing.Sha256OfFile(fs, file);
        }

        foreach (var dir in fs.Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            result[dir + "\\"] = "<dir>";
        }

        return result;
    }

    private DeploymentManifest Manifest(
        Guid planId,
        IReadOnlyList<BackupEntry> backups,
        IReadOnlyList<string> written,
        IReadOnlyList<string> createdDirs)
    {
        var manifest = new DeploymentManifest
        {
            PlanId = planId,
            CreatedUtc = DateTime.UtcNow,
            Fingerprint = Fingerprint,
            GameTitle = "Fake Game",
            DeployDirectory = DeployDir,
            Payload = new PayloadInfo(PayloadOrigin.LocalBuild, "0.9.1.0", @"D:\payload"),
            Identity = new IdentityDecision(DllIdentity.Dxgi, "dxgi.dll", IdentityStatus.Resolved,
                Array.Empty<DllIdentity>(), null, null),
            Profile = Deployer.Core.Profiles.BuiltinProfiles.AmdRdna4,
            Backups = backups,
            WrittenPaths = written,
            CreatedDirectories = createdDirs,
        };

        _store.WriteManifest(manifest);
        return manifest;
    }

    private static string BackupPath(Guid planId, string fileName) =>
        $@"{Root}\{Fingerprint}\{planId:D}\{fileName}";

    [Fact]
    public void Deletes_files_that_were_purely_new()
    {
        _fs.AddFile($@"{DeployDir}\dxgi.dll", new MockFileData("OPTISCALER"));
        var manifest = Manifest(Guid.NewGuid(), Array.Empty<BackupEntry>(),
            new[] { $@"{DeployDir}\dxgi.dll" }, Array.Empty<string>());

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.True(result.Success);
        Assert.False(_fs.File.Exists($@"{DeployDir}\dxgi.dll"));
        Assert.Contains($@"{DeployDir}\dxgi.dll", result.DeletedPaths);
    }

    [Fact]
    public void Restores_a_backed_up_game_file_byte_for_byte()
    {
        var original = $@"{DeployDir}\version.dll";
        var planId = Guid.NewGuid();
        var backupPath = BackupPath(planId, "version.dll");
        _fs.AddFile(backupPath, new MockFileData("GAME OWN VERSION DLL v1.2.3"));
        _fs.AddFile(original, new MockFileData("OPTISCALER REPLACEMENT"));

        // 主路径：BackupEntry 带着备份当时记下的哈希，还原后逐字比对。
        Manifest(planId,
            new[]
            {
                new BackupEntry(original, backupPath, BackupReason.OverwriteGameFile,
                    Hashing.Sha256OfFile(_fs, backupPath)),
            },
            new[] { original }, Array.Empty<string>());

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, planId));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("GAME OWN VERSION DLL v1.2.3", _fs.File.ReadAllText(original));
        Assert.Contains(original, result.RestoredPaths);
        Assert.DoesNotContain(original, result.DeletedPaths);
    }

    [Fact]
    public void Reports_failure_when_the_backup_was_corrupted_while_in_storage()
    {
        var original = $@"{DeployDir}\version.dll";
        var planId = Guid.NewGuid();
        var backupPath = BackupPath(planId, "version.dll");
        _fs.AddFile(backupPath, new MockFileData("GAME OWN VERSION DLL v1.2.3"));
        _fs.AddFile(original, new MockFileData("OPTISCALER REPLACEMENT"));

        // 先记下正确哈希，再在**备份完成之后**改坏备份件——模拟磁盘错误、杀软、
        // 云同步截断这类在存放期间发生的事。哈希与此刻的磁盘状态分属两个时间点，
        // 所以这不是「校验它等于校验自己」。
        var goodHash = Hashing.Sha256OfFile(_fs, backupPath);
        Manifest(planId,
            new[] { new BackupEntry(original, backupPath, BackupReason.OverwriteGameFile, goodHash) },
            new[] { original }, Array.Empty<string>());
        _fs.File.WriteAllText(backupPath, "TRUNCATED");

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, planId));

        // 没有这条校验，上面那个坏文件会被装回游戏目录，而 result.Success 会是 true：
        // 用户以为游戏复原了，实际上它已经坏了，而且再没有第二份副本。
        // 注意此时确实没有更好的选择（副本已坏），如实验警是唯一正确的做法。
        Assert.False(result.Success);
        Assert.Contains(original, result.ErrorMessage!);
    }

    [Fact]
    public void Still_rolls_back_a_manifest_written_before_the_hash_field_existed()
    {
        var original = $@"{DeployDir}\version.dll";
        var planId = Guid.NewGuid();
        var backupPath = BackupPath(planId, "version.dll");
        _fs.AddFile(backupPath, new MockFileData("GAME OWN VERSION DLL v1.2.3"));
        _fs.AddFile(original, new MockFileData("OPTISCALER REPLACEMENT"));

        // Sha256 缺省为 null，这就是本字段引入之前写下的旧 manifest 的样子。
        // 旧 manifest 必须仍然能回滚：「任何时候都能回滚」是核心承诺，不能因为
        // 新增了一个校验字段就把老用户的备份变成废纸。代价是这一条路径上没有校验。
        Manifest(planId,
            new[] { new BackupEntry(original, backupPath, BackupReason.OverwriteGameFile) },
            new[] { original }, Array.Empty<string>());

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, planId));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("GAME OWN VERSION DLL v1.2.3", _fs.File.ReadAllText(original));
    }

    [Fact]
    public void Removes_directories_it_created_when_they_end_up_empty()
    {
        _fs.AddFile($@"{DeployDir}\OptiScaler\amdxc64.dll", new MockFileData("X"));
        var manifest = Manifest(Guid.NewGuid(), Array.Empty<BackupEntry>(),
            new[] { $@"{DeployDir}\OptiScaler\amdxc64.dll" },
            new[] { $@"{DeployDir}\OptiScaler" });

        _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.False(_fs.Directory.Exists($@"{DeployDir}\OptiScaler"));
    }

    [Fact]
    public void Keeps_a_created_directory_that_still_holds_other_files()
    {
        _fs.AddFile($@"{DeployDir}\OptiScaler\amdxc64.dll", new MockFileData("X"));
        _fs.AddFile($@"{DeployDir}\OptiScaler\games_own_mod.dll", new MockFileData("MOD"));
        var manifest = Manifest(Guid.NewGuid(), Array.Empty<BackupEntry>(),
            new[] { $@"{DeployDir}\OptiScaler\amdxc64.dll" },
            new[] { $@"{DeployDir}\OptiScaler" });

        _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.True(_fs.Directory.Exists($@"{DeployDir}\OptiScaler"));
        Assert.True(_fs.File.Exists($@"{DeployDir}\OptiScaler\games_own_mod.dll"));
    }

    [Fact]
    public void Never_deletes_directories_it_did_not_create()
    {
        _fs.AddDirectory($@"{DeployDir}\GameContent");
        _fs.AddFile($@"{DeployDir}\dxgi.dll", new MockFileData("OPTISCALER"));
        var manifest = Manifest(Guid.NewGuid(), Array.Empty<BackupEntry>(),
            new[] { $@"{DeployDir}\dxgi.dll" }, Array.Empty<string>());

        _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.True(_fs.Directory.Exists($@"{DeployDir}\GameContent"));
        Assert.True(_fs.Directory.Exists(DeployDir));
    }

    [Fact]
    public void Cleans_up_leftover_tmp_and_bak_files()
    {
        _fs.AddFile($@"{DeployDir}\dxgi.dll.optideploy.tmp", new MockFileData("PARTIAL"));
        _fs.AddFile($@"{DeployDir}\dxgi.dll.optideploy.bak", new MockFileData("STALE"));
        var manifest = Manifest(Guid.NewGuid(), Array.Empty<BackupEntry>(),
            new[] { $@"{DeployDir}\dxgi.dll" }, Array.Empty<string>());

        _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.Empty(_fs.Directory.GetFiles(DeployDir, "*.optideploy.*"));
    }

    [Fact]
    public void Missing_manifest_yields_failure_not_an_exception()
    {
        var result = _rollback.Rollback($@"{Root}\nope\nope\manifest.json");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Missing_backup_file_is_reported_and_the_original_is_left_alone()
    {
        var original = $@"{DeployDir}\version.dll";
        _fs.AddFile(original, new MockFileData("OPTISCALER REPLACEMENT"));
        var ghostBackup = $@"{Root}\{Fingerprint}\ghost\version.dll";
        var manifest = Manifest(Guid.NewGuid(),
            new[] { new BackupEntry(original, ghostBackup, BackupReason.OverwriteGameFile) },
            new[] { original }, Array.Empty<string>());

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.False(result.Success);
        Assert.Equal("OPTISCALER REPLACEMENT", _fs.File.ReadAllText(original));
        Assert.Contains("version.dll", result.ErrorMessage);
    }

    [Fact]
    public void Rollback_is_idempotent()
    {
        _fs.AddFile($@"{DeployDir}\dxgi.dll", new MockFileData("OPTISCALER"));
        var manifest = Manifest(Guid.NewGuid(), Array.Empty<BackupEntry>(),
            new[] { $@"{DeployDir}\dxgi.dll" }, Array.Empty<string>());
        var path = _store.ManifestPathFor(Fingerprint, manifest.PlanId);

        var first = _rollback.Rollback(path);
        var second = _rollback.Rollback(path);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.False(_fs.File.Exists($@"{DeployDir}\dxgi.dll"));
    }

    [Fact]
    public void Snapshot_helper_proves_directory_is_untouched_after_a_noop_rollback()
    {
        _fs.AddFile($@"{DeployDir}\Game.exe", new MockFileData("EXE"));
        _fs.AddFile($@"{DeployDir}\Game.pak", new MockFileData("PAK"));
        var before = Snapshot(_fs, DeployDir);
        var manifest = Manifest(Array.Empty<BackupEntry>(), Array.Empty<string>(), Array.Empty<string>());

        _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.Equal(before, Snapshot(_fs, DeployDir));
    }
}
```

`Manifest(...)` 显式接受 `planId`，而不是内部 `Guid.NewGuid()`：因为 `BackupEntry.BackupPath` 必须位于该 planId 目录下，测试需要先知道 planId 才能造出真实的备份文件。让辅助方法自己生成就得事后重写 manifest，那是自找的麻烦。

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter RollbackExecutorTests`
Expected: 编译错误 `RollbackExecutor 不存在`

- [ ] **Step 4: 写 `Deployment/RollbackExecutor.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Util;

namespace Deployer.Core.Deployment;

public sealed class RollbackExecutor : IRollbackExecutor
{
    private readonly IFileSystem _fs;
    private readonly AtomicFileWriter _writer;
    private readonly IBackupStore _backups;

    public RollbackExecutor(IFileSystem fs, AtomicFileWriter writer, IBackupStore backups)
    {
        _fs = fs;
        _writer = writer;
        _backups = backups;
    }

    public RollbackResult Rollback(string manifestPath)
    {
        DeploymentManifest manifest;
        try
        {
            // ReadManifest 只在「文件不存在」时返回 null；「存在但读不出来」会抛，
            // 并把真正的起因（JSON 语法错、枚举名对不上、磁盘读失败）放在 InnerException 里。
            // 两条路径都在这里收口成一个带原因的 RollbackResult，因为接口契约规定
            // 「回滚失败不抛异常」——它必须能把已完成的还原清单交代给用户。
            manifest = _backups.ReadManifest(manifestPath)
                ?? throw new FileNotFoundException($"找不到备份清单：{manifestPath}", manifestPath);
        }
        catch (Exception ex)
        {
            return new RollbackResult(false, Array.Empty<string>(), Array.Empty<string>(),
                $"无法读取备份清单：{manifestPath}。需手工处理。"
                + $"原因：{ex.Message}"
                + (ex.InnerException is { } inner ? $"（{inner.Message}）" : null));
        }

        var restored = new List<string>();
        var deleted = new List<string>();
        var errors = new List<string>();

        var restoredOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 还原被覆盖的原件（优先级高于删除）
        foreach (var entry in manifest.Backups)
        {
            if (!_fs.File.Exists(entry.BackupPath))
            {
                errors.Add($"备份文件丢失，无法还原：{entry.OriginalPath}");
                continue;
            }

            try
            {
                CopyBack(entry);
                restored.Add(entry.OriginalPath);
                restoredOriginals.Add(entry.OriginalPath);
            }
            catch (Exception ex)
            {
                errors.Add($"还原失败：{entry.OriginalPath}（{ex.Message}）");
            }
        }

        // 2. 删掉纯新增的文件（没有对应备份的那些）
        foreach (var written in manifest.WrittenPaths)
        {
            if (restoredOriginals.Contains(written))
            {
                continue;
            }

            TryDeleteFile(written, deleted, errors);
            TryDeleteFile(written + ".optideploy.tmp", deleted, errors);
            TryDeleteFile(written + ".optideploy.bak", deleted, errors);
        }

        // 3. 清掉部署目录里残留的 tmp/bak（包括不在 WrittenPaths 里的）
        CleanStrayArtifacts(manifest.DeployDirectory);

        // 4. 自底向上删掉自己新建、且已空的目录
        foreach (var dir in manifest.CreatedDirectories
                     .OrderByDescending(d => d.Count(c => c == '\\' || c == '/')))
        {
            try
            {
                if (_fs.Directory.Exists(dir)
                    && !_fs.Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    _fs.Directory.Delete(dir, recursive: false);
                    deleted.Add(dir);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"删除空目录失败：{dir}（{ex.Message}）");
            }
        }

        return new RollbackResult(
            errors.Count == 0, restored, deleted,
            errors.Count == 0 ? null : string.Join("\n", errors));
    }

    private void CopyBack(BackupEntry entry)
    {
        var dir = _fs.Path.GetDirectoryName(entry.OriginalPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        // 直接覆写而不走 AtomicFileWriter：回滚的目标是把文件变回原样，中途失败留在
        // 原地的半个文件比覆写失败更糟；tmp+bak 反而会在游戏目录里多留残留物。
        _fs.File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);

        // 还原后校验。entry.Sha256 是**备份当时**由 BackupStore 记下的，与此刻的磁盘
        // 状态分属两个时间点、相互独立，所以这不是「校验它等于校验自己」。
        // 备份件若在存放期间被磁盘错误或杀软改动过，这里会响；不校验就会把坏文件
        // 装回游戏目录并宣称回滚成功。抛出去由 Rollback 的 catch 计入 errors，
        // 最终 Success=false——没有更好的副本可用，如实验警是唯一正确的做法。
        // Sha256 为 null 只可能是本字段引入之前写下的旧 manifest，此时跳过校验（前向兼容）。
        if (entry.Sha256 is not null)
        {
            var actual = Hashing.Sha256OfFile(_fs, entry.OriginalPath);
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChecksumMismatchException(entry.OriginalPath, entry.Sha256, actual);
            }
        }
    }

    private void TryDeleteFile(string path, List<string> deleted, List<string> errors)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
                deleted.Add(path);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"删除失败：{path}（{ex.Message}）");
        }
    }

    private void CleanStrayArtifacts(string deployDirectory)
    {
        if (!_fs.Directory.Exists(deployDirectory))
        {
            return;
        }

        foreach (var stray in _fs.Directory.EnumerateFiles(deployDirectory, "*.optideploy.*", SearchOption.AllDirectories))
        {
            try
            {
                _fs.File.Delete(stray);
            }
            catch (Exception)
            {
                // 残留文件删不掉不影响正确性，不值得让整个回滚报失败
            }
        }
    }
}
```

两个容易写错的细节：

1. **还原优先于删除**。`WrittenPaths` 里包含那些「既被写过、又有备份」的路径（身份 DLL 覆盖了游戏自带 `version.dll` 就是这种）。如果先跑删除循环，游戏自带文件就永久没了。`restoredOriginals` 集合就是这道闸门。
2. **目录只删自己建的、且已空的**。绝不递归删 `OptiScaler\`——那正是官方 `Remove_OptiScaler.bat` 的 `del /Q OptiScaler\*` + `rd` 会误伤用户 mod 的地方（spec §5.2）。

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter RollbackExecutorTests`
Expected: `失败: 0，通过: 10`

- [ ] **Step 6: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add manifest-driven rollback executor"
```

---

## Task 12: DeploymentExecutor

Spec §5.4 / §5.5。executor 只无脑执行一个 plan，不做任何决策。

**回滚依据的设计决定**：`manifest.json` 在执行**开始前**就写盘，因此只要它存在，任何中途失败（包括进程被强杀）都能完整回滚：`WrittenPaths` 里尚未落盘的文件走「删如果存在」，已落盘的走「还原或删除」，两种情况均安全。`progress.json` 因此**不是回滚的必要输入**，它只是诊断信息（告诉用户死在哪一步），以及 Plan D 里主进程接管崩溃 Worker 时的提示。把回滚建立在单一数据源上，比「两个文件必须互相一致」健壮得多。

**Files:**
- Create: `src/Deployer.Core/Deployment/IDeploymentExecutor.cs`
- Create: `src/Deployer.Core/Deployment/DeploymentExecutor.cs`
- Test: `tests/Deployer.Core.Tests/DeploymentExecutorTests.cs`

- [ ] **Step 1: 写 `Deployment/IDeploymentExecutor.cs`**

```csharp
using Deployer.Core.Domain;

namespace Deployer.Core.Deployment;

public sealed record DeploymentResult(
    bool Success,
    string ManifestPath,
    IReadOnlyList<string> CompletedSteps,
    string? ErrorMessage,
    bool RolledBack);

public interface IDeploymentExecutor
{
    DeploymentResult Execute(DeploymentPlan plan, CancellationToken ct);
}
```

- [ ] **Step 2: 写 `DeploymentExecutorTests.cs`**

```csharp
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class DeploymentExecutorTests
{
    private const string PayloadRoot = @"D:\repo\x64\Release\a";
    private const string BackupRoot = @"C:\backup-root";

    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();

    private DeploymentExecutor Executor(IBackupStore? store = null)
    {
        var backupStore = store ?? new BackupStore(_fs, BackupRoot);
        var writer = new AtomicFileWriter(_fs);
        return new DeploymentExecutor(_fs, writer, backupStore,
            new RollbackExecutor(_fs, writer, backupStore));
    }

    private DeploymentPlan BuildPlan(GamePlatform platform = GamePlatform.Steam, bool frameGen = false)
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateUeGame(_fs);
        var planner = new DeploymentPlanner(
            _fs, new DllIdentitySelector(_fs, _versions), new PayloadWalker(_fs), new IniPatcher());

        var target = new GameTarget(game.Title, platform, null, null, game.InstallRoot,
            game.DeployDirectory, game.ExecutableName,
            new FingerprintCalculator().Compute(platform, null, game.InstallRoot, game.ExecutableName));

        return planner.Plan(new PlanRequest
        {
            Game = target,
            Inspection = game.DefaultInspection,
            Payload = new PayloadBundle(PayloadRoot, "0.9.1.0", PayloadOrigin.LocalBuild),
            Profile = BuiltinProfiles.AmdRdna4,
            Writability = DirectoryWritability.Writable,
            BackupRoot = BackupRoot,
            EnableFrameGen = frameGen,
        });
    }

    [Fact]
    public void Successful_deployment_creates_identity_dll_subtree_and_patched_ini()
    {
        var plan = BuildPlan();

        var result = Executor().Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.RolledBack);
        var dir = plan.Game.DeployDirectory;
        Assert.True(_fs.File.Exists($@"{dir}\dxgi.dll"));
        Assert.True(_fs.File.Exists($@"{dir}\OptiScaler\amdxc64.dll"));
        Assert.True(_fs.File.Exists($@"{dir}\Licenses\DirectX_LICENSE.txt"));

        var ini = _fs.File.ReadAllText($@"{dir}\OptiScaler.ini");
        Assert.Contains("Dx12Upscaler=ffx", ini);
        Assert.Contains($"TargetProcessName={plan.Game.ExecutableName}", ini);
        Assert.Contains("LogToFile=true", ini);
    }

    [Fact]
    public void Deployed_ini_has_no_utf8_bom()
    {
        var plan = BuildPlan();

        Executor().Execute(plan, CancellationToken.None);

        var bytes = _fs.File.ReadAllBytes($@"{plan.Game.DeployDirectory}\OptiScaler.ini");
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "带 BOM 会让 SimpleIni 把首个 section 读成 \uFEFF[Upscalers]，整段失效");
    }

    [Fact]
    public void Deployment_does_not_touch_the_game_executable()
    {
        var plan = BuildPlan();
        var exe = plan.Game.InstallRoot + @"\SwGame\Binaries\Win64\SwGame-Win64-Shipping.exe";
        var before = _fs.File.ReadAllText(exe);

        Executor().Execute(plan, CancellationToken.None);

        Assert.Equal(before, _fs.File.ReadAllText(exe));
    }

    [Fact]
    public void Payload_directory_is_left_intact_so_it_can_be_reused()
    {
        var plan = BuildPlan();
        var before = _fs.Directory.GetFiles(PayloadRoot, "*", SearchOption.AllDirectories).Length;

        Executor().Execute(plan, CancellationToken.None);

        Assert.Equal(before, _fs.Directory.GetFiles(PayloadRoot, "*", SearchOption.AllDirectories).Length);
        Assert.True(_fs.File.Exists($@"{PayloadRoot}\OptiScaler.dll"),
            "必须 copy 而不是 move：同一份 payload 要能装到多个游戏");
    }

    [Fact]
    public void Manifest_lists_every_written_path_and_created_directory()
    {
        var plan = BuildPlan();
        var store = new BackupStore(_fs, BackupRoot);

        var result = Executor().Execute(plan, store: store);
        var manifest = store.ReadManifest(result.ManifestPath);

        Assert.NotNull(manifest);
        Assert.Equal(plan.PlanId, manifest!.PlanId);
        Assert.Equal(
            plan.Writes.Select(w => w.DestinationPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            manifest.WrittenPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(manifest.CreatedDirectories,
            d => d.EndsWith(@"\OptiScaler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Manifest_is_written_before_any_file_lands()
    {
        var plan = BuildPlan();
        var store = new RecordingBackupStore(new BackupStore(_fs, BackupRoot));

        Executor(store: store).Execute(plan, CancellationToken.None);

        Assert.True(store.ManifestWrittenBeforeFirstWrite,
            "manifest 必须在任何文件操作之前落盘，否则中途崩溃就无法回滚");
    }

    [Fact]
    public void Existing_identity_dll_is_backed_up_then_replaced()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateUeGame(_fs);
        var existing = $@"{game.DeployDirectory}\dxgi.dll";
        _fs.AddFile(existing, new MockFileData("GAME OWN DXGI"));
        _versions.SetOriginalFilename(existing, "OptiScaler.dll");

        var planner = new DeploymentPlanner(_fs, new DllIdentitySelector(_fs, _versions),
            new PayloadWalker(_fs), new IniPatcher());
        var target = new GameTarget(game.Title, GamePlatform.Steam, null, null, game.InstallRoot,
            game.DeployDirectory, game.ExecutableName,
            new FingerprintCalculator().Compute(GamePlatform.Steam, null, game.InstallRoot, game.ExecutableName));
        var plan = planner.Plan(new PlanRequest
        {
            Game = target,
            Inspection = game.DefaultInspection,
            Payload = new PayloadBundle(PayloadRoot, "0.9.1.0", PayloadOrigin.LocalBuild),
            Profile = BuiltinProfiles.AmdRdna4,
            Writability = DirectoryWritability.Writable,
            BackupRoot = BackupRoot,
        });

        var result = Executor().Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        var backup = Assert.Single(plan.Backups);
        Assert.Equal(BackupReason.OverwritePreviousOpti, backup.Reason);
        Assert.Equal("GAME OWN DXGI", _fs.File.ReadAllText(backup.BackupPath));
        Assert.Equal("SYNTHETIC::OptiScaler.dll::v0.9.1.0", _fs.File.ReadAllText(existing));
    }

    [Fact]
    public void Failure_midway_rolls_back_and_leaves_no_ignition_switch()
    {
        var plan = BuildPlan();
        var broken = plan.Writes.ToList();
        var index = broken.Count / 2;
        broken[index] = broken[index] with { Sha256 = new string('0', 64) };
        var brokenPlan = plan with { Writes = broken };
        var dir = plan.Game.DeployDirectory;

        var result = Executor().Execute(brokenPlan, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.RolledBack, "必须已回滚");
        Assert.False(_fs.File.Exists($@"{dir}\dxgi.dll"), "回滚后点火开关必须不存在");
        Assert.False(_fs.File.Exists($@"{dir}\OptiScaler\amdxc64.dll"));
        Assert.True(_fs.File.Exists($@"{dir}\SwGame-Win64-Shipping.exe"), "游戏本体必须完好");
    }

    [Fact]
    public void Blocking_warning_prevents_execution_entirely()
    {
        var plan = BuildPlan();
        var blocked = plan with
        {
            Warnings = plan.Warnings.Append(
                new PlanWarning(WarningSeverity.Blocking, "游戏无超分 SDK")).ToList(),
        };

        var result = Executor().Execute(blocked, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.CompletedSteps);
        Assert.False(_fs.File.Exists($@"{plan.Game.DeployDirectory}\dxgi.dll"));
    }

    [Fact]
    public void CompletedSteps_records_every_write()
    {
        var plan = BuildPlan();

        var result = Executor().Execute(plan, CancellationToken.None);

        Assert.Equal(plan.Writes.Count, result.CompletedSteps.Count);
    }

    [Fact]
    public void Cancellation_before_start_writes_nothing()
    {
        var plan = BuildPlan();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Executor().Execute(plan, cts.Token));
        Assert.False(_fs.File.Exists($@"{plan.Game.DeployDirectory}\dxgi.dll"));
    }

    [Fact]
    public void Old_backups_are_pruned_to_three()
    {
        var plan = BuildPlan();
        var store = new BackupStore(_fs, BackupRoot);
        for (var i = 0; i < 4; i++)
        {
            _fs.AddFile(
                store.ManifestPathFor(plan.Game.Fingerprint, Guid.Parse($"0000000{i}-0000-0000-0000-000000000000")),
                new MockFileData(Deployer.Core.Serialization.DeployerJson.Serialize(new DeploymentManifest
                {
                    PlanId = Guid.Parse($"0000000{i}-0000-0000-0000-000000000000"),
                    CreatedUtc = DateTime.UtcNow.AddDays(-10 + i),
                    Fingerprint = plan.Game.Fingerprint,
                    GameTitle = plan.Game.Title,
                    DeployDirectory = plan.Game.DeployDirectory,
                    Payload = plan.Payload,
                    Identity = plan.Identity,
                    Profile = plan.Profile,
                    Backups = Array.Empty<BackupEntry>(),
                    WrittenPaths = Array.Empty<string>(),
                    CreatedDirectories = Array.Empty<string>(),
                })));
        }

        Executor(store: store).Execute(plan, CancellationToken.None);

        Assert.Equal(3, store.ListPlanIds(plan.Game.Fingerprint).Count);
    }

    /// <summary>记录调用顺序，用来断言 manifest 先于一切文件操作。</summary>
    private sealed class RecordingBackupStore : IBackupStore
    {
        private readonly IBackupStore _inner;
        private readonly List<string> _calls = new();

        public RecordingBackupStore(IBackupStore inner) => _inner = inner;

        public bool ManifestWrittenBeforeFirstWrite
        {
            get
            {
                var manifest = _calls.IndexOf("manifest");
                var write = _calls.FindIndex(c => c is "backup" or "progress");
                return manifest >= 0 && (write < 0 || manifest < write);
            }
        }

        public string DirectoryFor(string fingerprint, Guid planId) => _inner.DirectoryFor(fingerprint, planId);
        public string ManifestPathFor(string fingerprint, Guid planId) => _inner.ManifestPathFor(fingerprint, planId);
        public string ProgressPathFor(string fingerprint, Guid planId) => _inner.ProgressPathFor(fingerprint, planId);
        public BackupEntry Backup(BackupEntry entry) { _calls.Add("backup"); return _inner.Backup(entry); }
        public void WriteManifest(DeploymentManifest m) { _calls.Add("manifest"); _inner.WriteManifest(m); }
        public DeploymentManifest? ReadManifest(string p) => _inner.ReadManifest(p);
        public void WriteProgress(DeploymentProgress p) { _calls.Add("progress"); _inner.WriteProgress(p); }
        public DeploymentProgress? ReadProgress(string p) => _inner.ReadProgress(p);
        public IReadOnlyList<Guid> ListPlanIds(string f) => _inner.ListPlanIds(f);
        public IReadOnlyList<Guid> Prune(string f, int keep) => _inner.Prune(f, keep);
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test OptiScalerDeployer.sln --filter DeploymentExecutorTests`
Expected: 编译错误 `DeploymentExecutor 不存在`

- [ ] **Step 4: 写 `Deployment/DeploymentExecutor.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Errors;

namespace Deployer.Core.Deployment;

public sealed class DeploymentExecutor : IDeploymentExecutor
{
    private const int KeepBackups = 3;   // spec §5.6

    private readonly IFileSystem _fs;
    private readonly AtomicFileWriter _writer;
    private readonly IBackupStore _backups;
    private readonly IRollbackExecutor? _rollback;

    public DeploymentExecutor(
        IFileSystem fs, AtomicFileWriter writer, IBackupStore backups, IRollbackExecutor? rollback = null)
    {
        _fs = fs;
        _writer = writer;
        _backups = backups;
        _rollback = rollback;
    }

    public DeploymentResult Execute(DeploymentPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (plan.HasBlockingWarning)
        {
            return new DeploymentResult(false, string.Empty, Array.Empty<string>(),
                "计划包含阻塞级警告，不能执行。请先处理警告。", rolledBack: false);
        }

        var fingerprint = plan.Game.Fingerprint;
        var manifestPath = _backups.ManifestPathFor(fingerprint, plan.PlanId);
        var createdDirectories = new List<string>();
        var completed = new List<string>();

        // backups 起初就是 plan.Backups（Sha256 全为 null），第 2 步换成带哈希的版本。
        // 之所以用一个单独的列表：BuildManifest 会在第 1/2b/4 步被调用三次，每次都应该
        // 反映「当下已知的最完整真相」。
        var backups = plan.Backups.ToList();

        // 1. manifest 先落盘（CreatedDirectories 与 Sha256 此时还未知，后面回填）。
        //    这一步换来的是「此后任何时刻崩溃都能完整回滚」。
        _backups.WriteManifest(BuildManifest(plan, createdDirectories, backups));

        try
        {
            // 2. 把所有要被覆盖的原件抢进影子目录——必须在任何写入之前全部完成。
            //    Backup 返回的是带 Sha256 的新 entry，必须接住返回值；丢掉它就等于
            //    把回滚退化成盲拷（拷回去的是不是当初那份，无从知道）。
            backups = new List<BackupEntry>();
            foreach (var entry in plan.Backups)
            {
                ct.ThrowIfCancellationRequested();
                backups.Add(_backups.Backup(entry));
            }

            // 2b. 立即用带哈希的 backups 重写 manifest。等到第 4 步再写会留下一个窗口：
            //     如果在写完文件之后、回填之前崩溃，磁盘上那份 manifest 的哈希全是 null，
            //     回滚就只能盲拷。manifest 是小 JSON，多写一次换来窗口关闭，值得。
            _backups.WriteManifest(BuildManifest(plan, createdDirectories, backups));

            _backups.WriteProgress(DeploymentProgress.Empty(plan.PlanId, manifestPath));

            // 3. 按 plan 的顺序写：子树 → ini → 点火开关
            foreach (var write in plan.Writes)
            {
                ct.ThrowIfCancellationRequested();
                createdDirectories.AddRange(_writer.Write(write));
                completed.Add(write.DestinationPath);
                _backups.WriteProgress(new DeploymentProgress
                {
                    PlanId = plan.PlanId,
                    ManifestPath = manifestPath,
                    CompletedSteps = completed.ToList(),
                });
            }

            // 4. 回填真实的 CreatedDirectories（backups 里的哈希已在 2b 落盘）
            _backups.WriteManifest(BuildManifest(plan, createdDirectories, backups));

            _backups.Prune(fingerprint, KeepBackups);
            return new DeploymentResult(true, manifestPath, completed, errorMessage: null, rolledBack: false);
        }
        catch (OperationCanceledException)
        {
            TryRollback(manifestPath);
            throw;
        }
        catch (Exception ex)
        {
            var rolledBack = TryRollback(manifestPath);
            var message = ex is DeployerException ? ex.Message : $"部署失败：{ex.Message}";
            return new DeploymentResult(false, manifestPath, completed, message, rolledBack);
        }
    }

    private DeploymentManifest BuildManifest(DeploymentPlan plan, IReadOnlyList<string> createdDirectories,
        IReadOnlyList<BackupEntry> backups) => new()
    {
        PlanId = plan.PlanId,
        CreatedUtc = DateTime.UtcNow,
        Fingerprint = plan.Game.Fingerprint,
        GameTitle = plan.Game.Title,
        DeployDirectory = plan.Game.DeployDirectory,
        Payload = plan.Payload,
        Identity = plan.Identity,
        Profile = plan.Profile,
        Backups = backups,
        WrittenPaths = plan.Writes.Select(w => w.DestinationPath).ToList(),
        CreatedDirectories = createdDirectories.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };

    private bool TryRollback(string manifestPath)
    {
        if (_rollback is null)
        {
            return false;
        }

        try
        {
            return _rollback.Rollback(manifestPath).Success;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
```

三个不要改的地方：

1. **manifest 写两次**。第一次写时 `CreatedDirectories` 为空，因为还没创建。不要「优化」成只在内存里攻着最后写一次——那会把「任何时刻可回滚」这个不变式换成「只有成功时才可回滚」。
2. **备份全部完成后才开始写**。反过来（边写边备份）会让「身份 DLL 已覆盖但原件尚未入影子目录」这个窗口存在，此时崩溃就是真损坏。
3. **`Execute` 不抛异常**（除 `OperationCanceledException`）。失败以 `Success = false` 返回，因为调用方需要 `CompletedSteps` 与 `RolledBack` 才能向用户交代发生了什么；抛异常会把这些上下文丢在堆栈里。取消则必须往上抛——它是调用方的主动意图，不是失败。

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test OptiScalerDeployer.sln --filter DeploymentExecutorTests`
Expected: `失败: 0，通过: 12`

- [ ] **Step 6: 全量跑**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0，通过: 135`

- [ ] **Step 7: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add deployment executor with ordered writes and auto-rollback"
```

---

## Task 13: 安装台账

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

## Task 14: 端到端测试

spec §11.1 对回滚的要求是「断言目录与执行前**逐字节一致**」。这是整个计划里最重的一条断言，单独一个 Task 写。

**Files:**
- Test: `tests/Deployer.Core.Tests/EndToEndTests.cs`

- [ ] **Step 1: 写 `EndToEndTests.cs`**

```csharp
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

/// <summary>plan → execute → rollback 全链，针对 spec §11.2 的三种假游戏目录。</summary>
public class EndToEndTests
{
    private const string PayloadRoot = @"D:\repo\x64\Release\a";
    private const string BackupRoot = @"C:\backup-root";

    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();

    private record Snapshot(IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Directories);

    private static Snapshot Take(IFileSystem fs, string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fs.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files[f] = Deployer.Core.Util.Hashing.Sha256OfFile(fs, f);
        }

        var dirs = fs.Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

        return new Snapshot(files, dirs);
    }

    private (DeploymentPlan plan, DeploymentExecutor executor, RollbackExecutor rollback, BackupStore store)
        Arrange(FakeGame game, GamePlatform platform, UpscalerProfile? profile = null, bool frameGen = false)
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);

        var store = new BackupStore(_fs, BackupRoot);
        var writer = new AtomicFileWriter(_fs);
        var rollback = new RollbackExecutor(_fs, writer, store);
        var executor = new DeploymentExecutor(_fs, writer, store, rollback);
        var planner = new DeploymentPlanner(
            _fs, new DllIdentitySelector(_fs, _versions), new PayloadWalker(_fs), new IniPatcher());

        var target = new GameTarget(game.Title, platform, null, null, game.InstallRoot,
            game.DeployDirectory, game.ExecutableName,
            new FingerprintCalculator().Compute(platform, null, game.InstallRoot, game.ExecutableName));

        var plan = planner.Plan(new PlanRequest
        {
            Game = target,
            Inspection = game.DefaultInspection,
            Payload = new PayloadBundle(PayloadRoot, "0.9.1.0", PayloadOrigin.LocalBuild),
            Profile = profile ?? BuiltinProfiles.AmdRdna4,
            Writability = DirectoryWritability.Writable,
            BackupRoot = BackupRoot,
            EnableFrameGen = frameGen,
            AllowOverwriteGameFile = true,
        });

        return (plan, executor, rollback, store);
    }

    public static TheoryData<string> GameKinds => new() { "ue", "gdk", "conflict" };

    private static FakeGame MakeGame(MockFileSystem fs, string kind) => kind switch
    {
        "ue" => FakeGameFactory.CreateUeGame(fs),
        "gdk" => FakeGameFactory.CreateGdkGame(fs),
        "conflict" => FakeGameFactory.CreateConflictGame(fs),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(GameKinds))]
    public void Deploy_then_rollback_restores_the_game_directory_byte_for_byte(string kind)
    {
        var game = MakeGame(_fs, kind);
        var before = Take(_fs, game.InstallRoot);
        var (plan, executor, rollback, store) = Arrange(game, PlatformFor(kind));

        var deployed = executor.Execute(plan, CancellationToken.None);
        Assert.True(deployed.Success, deployed.ErrorMessage);

        var rolledBack = rollback.Rollback(deployed.ManifestPath);
        Assert.True(rolledBack.Success, rolledBack.ErrorMessage);

        Assert.Equal(before, Take(_fs, game.InstallRoot));
    }

    [Theory]
    [MemberData(nameof(GameKinds))]
    public void Deployment_actually_lands_the_ignition_switch(string kind)
    {
        var game = MakeGame(_fs, kind);
        var (plan, executor, _, _) = Arrange(game, PlatformFor(kind));

        var result = executor.Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        var identityPath = _fs.Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName);
        Assert.True(_fs.File.Exists(identityPath), $"{identityPath} 未落盘");
        Assert.Equal("SYNTHETIC::OptiScaler.dll::v0.9.1.0", _fs.File.ReadAllText(identityPath));
    }

    [Theory]
    [MemberData(nameof(GameKinds))]
    public void Rollback_always_removes_the_ignition_switch(string kind)
    {
        var game = MakeGame(_fs, kind);
        var (plan, executor, rollback, _) = Arrange(game, PlatformFor(kind));
        var identityPath = _fs.Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName);

        executor.Execute(plan, CancellationToken.None);
        Assert.True(_fs.File.Exists(identityPath));

        rollback.Rollback(executor.Execute(plan, CancellationToken.None).ManifestPath);
        Assert.False(_fs.File.Exists(identityPath),
            "回滚后点火开关必须消失，否则游戏下次启动仍会加载 OptiScaler");
    }

    [Fact]
    public void Conflict_game_identity_dlls_are_all_preserved_after_rollback()
    {
        var game = FakeGameFactory.CreateConflictGame(_fs);
        var (plan, executor, rollback, _) = Arrange(game, GamePlatform.Steam);
        var owned = new[] { "version.dll", "winmm.dll", "d3d12.dll" }
            .Select(n => _fs.Path.Combine(game.DeployDirectory, n))
            .ToDictionary(p => p, p => _fs.File.ReadAllText(p), StringComparer.OrdinalIgnoreCase);

        executor.Execute(plan, CancellationToken.None);
        rollback.Rollback(_fs.Path.Combine(
            BackupRoot, plan.Game.Fingerprint, plan.PlanId.ToString("D"), "manifest.json"));

        foreach (var (path, content) in owned)
        {
            Assert.True(_fs.File.Exists(path), $"{path} 回滚后丢失");
            Assert.Equal(content, _fs.File.ReadAllText(path));
        }
    }

    [Fact]
    public void Deploying_the_same_plan_twice_is_safe_and_still_rolls_back()
    {
        var game = FakeGameFactory.CreateUeGame(_fs);
        var before = Take(_fs, game.InstallRoot);
        var (plan, executor, rollback, _) = Arrange(game, GamePlatform.Steam);

        var first = executor.Execute(plan, CancellationToken.None);
        Assert.True(first.Success, first.ErrorMessage);
        var second = executor.Execute(plan, CancellationToken.None);
        Assert.True(second.Success, second.ErrorMessage);

        // 回滚最新一份就能回到原始状态（第二份的备份里存的是第一份部署的产物，
        // 但它与原始游戏文件无关，所以必须回滚**最早**那份才能真正还原）
        rollback.Rollback(second.ManifestPath);
        rollback.Rollback(first.ManifestPath);

        Assert.Equal(before, Take(_fs, game.InstallRoot));
    }

    [Fact]
    public void Two_different_games_never_share_backup_or_ledger_state()
    {
        var ue = FakeGameFactory.CreateUeGame(_fs);
        var conflict = FakeGameFactory.CreateConflictGame(_fs);
        var (uePlan, ueExecutor, ueRollback, _) = Arrange(ue, GamePlatform.Steam);
        var (cfPlan, cfExecutor, _, _) = Arrange(conflict, GamePlatform.Manual);

        Assert.NotEqual(uePlan.Game.Fingerprint, cfPlan.Game.Fingerprint);
        Assert.True(ueExecutor.Execute(uePlan, CancellationToken.None).Success);
        Assert.True(cfExecutor.Execute(cfPlan, CancellationToken.None).Success);

        // 卸载 UE 游戏，冲突游戏必须完好无损
        var conflictBefore = Take(_fs, conflict.InstallRoot);
        ueRollback.Rollback(_fs.Path.Combine(
            BackupRoot, uePlan.Game.Fingerprint, uePlan.PlanId.ToString("D"), "manifest.json"));

        Assert.Equal(conflictBefore, Take(_fs, conflict.InstallRoot));
    }

    [Fact]
    public void Nvidia_profile_produces_a_working_plan_too()
    {
        var game = FakeGameFactory.CreateUeGame(_fs);
        var (plan, executor, _, _) = Arrange(game, GamePlatform.Steam, profile: BuiltinProfiles.NvidiaRtx);

        var result = executor.Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("Dx12Upscaler=dlss",
            _fs.File.ReadAllText($@"{game.DeployDirectory}\OptiScaler.ini"));
    }

    [Fact]
    public void FrameGen_addon_lands_in_the_deployed_ini()
    {
        var game = FakeGameFactory.CreateGdkGame(_fs);
        var (plan, executor, _, _) = Arrange(game, GamePlatform.Xbox, frameGen: true);

        var result = executor.Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        var ini = _fs.File.ReadAllText($@"{game.DeployDirectory}\OptiScaler.ini");
        Assert.Contains("Enabled=true", ini);
        Assert.Contains("FGOutput=fsrfg", ini);
        Assert.Contains("HUDFix=true", ini);
    }

    private static GamePlatform PlatformFor(string kind) => kind switch
    {
        "gdk" => GamePlatform.Xbox,
        "conflict" => GamePlatform.Manual,
        _ => GamePlatform.Steam,
    };
}
```

`Deploying_the_same_plan_twice_is_safe_and_still_rolls_back` 里那段注释不是修辞，是一个**真实的语义陷阱**：重复部署同一 planId 时，第二次的影子备份里存的是第一次部署的产物（即 OptiScaler 自己的 DLL），不是游戏原件。因此只回滚最新一份会把游戏留在「装了 OptiScaler 但台账说已卸载」的状态。正确做法是逆序回滚全部份数。这个陷阱在计划 C 的 UI 上必须体现为「卸载时逆序回放所有未回滚的份数」，而不是「只回滚最新一份」。

- [ ] **Step 2: 跑测试**

Run: `dotnet test OptiScalerDeployer.sln --filter EndToEndTests`
Expected: `失败: 0，通过: 12`（三个 Theory 各展开 3 个 case）

若 `Deploy_then_rollback_restores_the_game_directory_byte_for_byte` 在 `conflict` 上失败，最可能的原因是：planner 把身份选成了 `dbghelp`（纯新增），但 `AllowOverwriteGameFile = true` 使选择器直接选了 `winmm`（覆盖游戏自带文件）。两者都应能逐字节还原；若不能，查 `RollbackExecutor` 的「还原优先于删除」闸门是否生效。

若 `Directories` 对不上，查 `CreatedDirectories` 是否漏了嵌套目录（`OptiScaler\D3D12_OptiScaler`）——`AtomicFileWriter.EnsureDirectories` 应该把整条链都返回。

- [ ] **Step 3: 全量跑**

Run: `dotnet test OptiScalerDeployer.sln`
Expected: `失败: 0`

- [ ] **Step 4: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "test(deployer): add plan-execute-rollback end-to-end coverage for three game layouts"
```

---

## Task 15: 最小 CLI（端到端验证入口）

没有它，前面 14 个 Task 的成果只能在测试宿主里看到。CLI 不引任何第三方参数解析库（YAGNI：四个子命令、每个三五个参数）。

**Files:**
- Modify: `src/Deployer.Cli/Program.cs`

- [ ] **Step 1: 写 `Program.cs`**

```csharp
using System.IO.Abstractions;
using Deployer.Core.Abstractions;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Payload;
using Deployer.Core.Persistence;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.Core.Serialization;

namespace Deployer.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "plan" => Plan(args[1..]),
                "apply" => Apply(args[1..]),
                "rollback" => Rollback(args[1..]),
                "ledger" => Ledger(args[1..]),
                _ => Fail($"未知子命令：{args[0]}"),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, ex);
        }
    }

    private static int Plan(string[] args)
    {
        var o = Options.Parse(args);
        var fs = new FileSystem();
        var versions = new WindowsFileVersionReader();

        var payloadRoot = o.Require("--payload");
        var installRoot = o.Require("--install-root");
        var deployDir = o.Get("--deploy-dir") ?? installRoot;
        var exeName = o.Require("--exe");
        var platform = Enum.Parse<GamePlatform>(o.Get("--platform") ?? "Manual", ignoreCase: true);
        var profileId = o.Get("--profile") ?? BuiltinProfiles.AmdRdna4Id;

        var store = new JsonProfileStore(fs, JsonProfileStore.DefaultPath);
        var profile = store.Find(profileId)
            ?? throw new ArgumentException($"未知的 profile：{profileId}。可用：{string.Join(", ", store.All().Select(p => p.Id))}");

        // 计划 A 没有体检器（计划 B），此处保守假设：DX12 + 有 DLSS，不触发 Blocking 警告。
        // 真正的体检接入后这段删除。
        var inspection = new InspectionSummary
        {
            GraphicsApis = new[] { GraphicsApi.Dx12 },
            DetectedSdks = o.Has("--no-sdk") ? Array.Empty<UpscalerSdk>() : new[] { UpscalerSdk.Dlss },
            HasEngineFolder = fs.Directory.Exists(fs.Path.Combine(installRoot, "Engine")),
            IsVulkanOnly = o.Has("--vulkan"),
        };

        var payload = new ManualDirectoryPayloadSource(payloadRoot, fs, versions);
        var bundle = payload.TryGetAsync(CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new Deployer.Core.Errors.PayloadNotFoundException(
                $"{payloadRoot} 里找不到 OptiScaler.dll + OptiScaler.ini");

        var target = new GameTarget(
            o.Get("--title") ?? fs.Path.GetFileName(installRoot.TrimEnd('\\', '/')) ?? "Unknown",
            platform, packageFamilyName: null, appId: null, installRoot, deployDir, exeName,
            new FingerprintCalculator().Compute(platform, null, installRoot, exeName));

        var plan = new DeploymentPlanner(
            fs,
            new DllIdentitySelector(fs, versions),
            new PayloadWalker(fs),
            new IniPatcher())
            .Plan(new PlanRequest
            {
                Game = target,
                Inspection = inspection,
                Payload = bundle,
                Profile = profile,
                Writability = DirectoryWritability.Writable,
                BackupRoot = o.Get("--backup-root") ?? BackupStore.DefaultRoot,
                EnableFrameGen = o.Has("--framegen"),
                AllowOverwriteGameFile = o.Has("--allow-overwrite"),
            });

        PrintPlan(plan);

        var outPath = o.Get("--out");
        if (outPath is not null)
        {
            fs.File.WriteAllText(outPath, DeployerJson.Serialize(plan));
            Console.WriteLine($"\n计划已写入 {outPath}");
        }

        return plan.HasBlockingWarning ? 2 : 0;
    }

    private static int Apply(string[] args)
    {
        var o = Options.Parse(args);
        var fs = new FileSystem();
        var planPath = o.Require("--plan");
        var plan = DeployerJson.Deserialize<DeploymentPlan>(fs.File.ReadAllText(planPath));

        var store = new BackupStore(fs, o.Get("--backup-root") ?? BackupStore.DefaultRoot);
        var writer = new AtomicFileWriter(fs);
        var executor = new DeploymentExecutor(fs, writer, store,
            new RollbackExecutor(fs, writer, store));

        if (plan.HasBlockingWarning && !o.Has("--force"))
        {
            return Fail("计划包含阻塞级警告。确认无误后可加 --force。");
        }

        var result = executor.Execute(plan, CancellationToken.None);

        Console.WriteLine(result.Success
            ? $"部署成功。manifest: {result.ManifestPath}\n共写入 {result.CompletedSteps.Count} 个文件。"
            : $"部署失败：{result.ErrorMessage}\n已回滚：{result.RolledBack}");

        if (result.Success)
        {
            var ledger = new JsonInstallLedger(fs,
                o.Get("--ledger") ?? JsonInstallLedger.DefaultPath);
            ledger.Save(new InstallRecord
            {
                Fingerprint = plan.Game.Fingerprint,
                GameTitle = plan.Game.Title,
                DeployDirectory = plan.Game.DeployDirectory,
                LatestPlanId = plan.PlanId,
                BackupRoot = store.DirectoryFor(plan.Game.Fingerprint, plan.PlanId),
                State = InstallState.Installed,
                PayloadVersion = plan.Payload.Version,
                IdentityFileName = plan.Identity.FileName,
                InstalledUtc = DateTime.UtcNow,
            });
            Console.WriteLine("台账已更新。");
        }

        return result.Success ? 0 : 1;
    }

    private static int Rollback(string[] args)
    {
        var o = Options.Parse(args);
        var fs = new FileSystem();
        var manifestPath = o.Require("--manifest");
        var store = new BackupStore(fs, o.Get("--backup-root") ?? BackupStore.DefaultRoot);
        var writer = new AtomicFileWriter(fs);

        var result = new RollbackExecutor(fs, writer, store).Rollback(manifestPath);

        Console.WriteLine(result.Success
            ? $"回滚成功。还原 {result.RestoredPaths.Count} 个原件，删除 {result.DeletedPaths.Count} 个新增文件。"
            : $"回滚失败：{result.ErrorMessage}");

        return result.Success ? 0 : 1;
    }

    private static int Ledger(string[] args)
    {
        var o = Options.Parse(args);
        var fs = new FileSystem();
        var ledger = new JsonInstallLedger(fs, o.Get("--ledger") ?? JsonInstallLedger.DefaultPath);
        var all = ledger.All();

        if (all.Count == 0)
        {
            Console.WriteLine("台账为空。");
            return 0;
        }

        foreach (var r in all)
        {
            Console.WriteLine($"{r.Fingerprint}  {r.State,-14} {r.IdentityFileName,-16} "
                            + $"v{r.PayloadVersion,-10} {r.GameTitle}");
            Console.WriteLine($"            {r.DeployDirectory}");
            Console.WriteLine($"            manifest: {r.BackupRoot}\\{r.LatestPlanId:D}\\manifest.json");
        }

        return 0;
    }

    private static void PrintPlan(DeploymentPlan plan)
    {
        Console.WriteLine($"=== 部署计划 {plan.PlanId:D} ===");
        Console.WriteLine($"游戏        : {plan.Game.Title}  [{plan.Game.Platform}]");
        Console.WriteLine($"部署目录    : {plan.Game.DeployDirectory}");
        Console.WriteLine($"可执行文件  : {plan.Game.ExecutableName}");
        Console.WriteLine($"指纹        : {plan.Game.Fingerprint}");
        Console.WriteLine($"Payload     : {plan.Payload.Origin} v{plan.Payload.Version}  ({plan.Payload.RootDirectory})");
        Console.WriteLine($"Profile     : {plan.Profile.Id} - {plan.Profile.DisplayName}");
        Console.WriteLine($"身份 DLL    : {plan.Identity.FileName}  [{plan.Identity.Status}]"
                        + (plan.Identity.ExistingFileReason is { } r ? $"  将覆盖（{r}）" : "  纯新增"));
        Console.WriteLine($"提权        : {plan.Elevation}");
        Console.WriteLine($"\n将写入 {plan.Writes.Count} 个文件（按落盘顺序）：");
        foreach (var w in plan.Writes)
        {
            Console.WriteLine($"  {(w.IsInline ? "[patch]" : "[copy] ")} {w.DestinationPath}");
        }

        Console.WriteLine($"\n将备份 {plan.Backups.Count} 个原件：");
        foreach (var b in plan.Backups)
        {
            Console.WriteLine($"  {b.OriginalPath}  ->  {b.BackupPath}  ({b.Reason})");
        }

        Console.WriteLine($"\nINI 覆盖 {plan.IniEdits.Count} 项：");
        foreach (var e in plan.IniEdits)
        {
            Console.WriteLine($"  [{e.Section}] {e.Key}: {e.OldValue ?? "<新增>"} -> {e.NewValue}");
        }

        if (plan.Warnings.Count > 0)
        {
            Console.WriteLine($"\n警告 {plan.Warnings.Count} 条：");
            foreach (var w in plan.Warnings)
            {
                Console.WriteLine($"  [{w.Severity}] {w.Message}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            opti-deploy —— OptiScaler Deployer CLI（Plan A：Core 引擎验证入口）

            子命令：
              plan      生成部署计划（不写任何文件，除非指定 --out）
              apply     执行一个已序列化的计划
              rollback  按 manifest 精确回滚
              ledger    列出安装台账

            plan 参数：
              --payload <dir>        OptiScaler 产物目录（含 OptiScaler.dll + OptiScaler.ini）必填
              --install-root <dir>   游戏根目录，必填
              --deploy-dir <dir>     部署目录，缺省等于 install-root
              --exe <name>           游戏可执行文件名，必填
              --title <name>         游戏标题，缺省取 install-root 末级目录名
              --platform <p>         Xbox | Steam | Epic | Manual，缺省 Manual
              --profile <id>         amd-rdna4 | amd-rdna4-native | nvidia-rtx，缺省 amd-rdna4
              --backup-root <dir>    影子备份根，缺省 %LOCALAPPDATA%\OptiScalerDeployer\backups
              --framegen             叠加帧生成附加档
              --allow-overwrite      允许覆盖游戏自带同名 DLL（会先备份）
              --vulkan               标记为纯 Vulkan 游戏（影响身份优先级）
              --no-sdk               标记为无任何超分 SDK（会产生 Blocking 警告）
              --out <file>           将计划序列化为 JSON

            apply 参数：    --plan <file>  [--backup-root <dir>]  [--ledger <file>]  [--force]
            rollback 参数： --manifest <file>  [--backup-root <dir>]
            ledger 参数：   [--ledger <file>]

            退出码：0 成功 | 1 失败 | 2 计划含 Blocking 警告
            """);
    }

    private static int Fail(string message, Exception? ex = null)
    {
        Console.Error.WriteLine($"错误：{message}");

        if (ex is not null)
        {
            // CLI 就是日志：技术细节没有别的落点，丢了就再没人能复现现场。
            // 对自家异常而言 DeployerException.ToString() 已把 Diagnostic 追加在 Message 之后，
            // 对外来异常而言会带堆栈——两种情形下打 ex.ToString() 一条就够。
            // 空一行隔开，让用户话术不被技术输出淹掉。
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.ToString());
        }

        return 1;
    }

    /// <summary>极简参数解析：--key value 或 --flag。不引第三方库。</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var next = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : null;
                o._values[args[i]] = next;
            }

            return o;
        }

        public bool Has(string key) => _values.ContainsKey(key);

        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

        public string Require(string key) =>
            Get(key) ?? throw new ArgumentException($"缺少必需参数 {key}");
    }
}
```

`Console.WriteLine(""" ... """)` 用的是 C# 11 的 raw string literal，`LangVersion` 已定为 12，可用。里面的 `\OptiScalerDeployer\backups` 在 raw string 里不需转义。

- [ ] **Step 2: 构建**

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet build OptiScalerDeployer.sln -c Debug
```
Expected: `0 个错误`

- [ ] **Step 3: 手工端到端验证（真磁盘）**

本机没有 `x64\Release\a`，所以用一份**手工造的假 payload** 跑完整链路。全部放在 `%TEMP%` 下，不碰任何真游戏。

Run:
```powershell
$sandbox = Join-Path $env:TEMP "opti-deployer-e2e"
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
$payload = Join-Path $sandbox "payload"
$game = Join-Path $sandbox "FakeGame\Binaries\Win64"
$backup = Join-Path $sandbox "backups"
$ledger = Join-Path $sandbox "ledger.json"
New-Item -ItemType Directory -Force -Path $payload, "$payload\OptiScaler", $game | Out-Null

"FAKE OPTISCALER DLL" | Set-Content "$payload\OptiScaler.dll" -Encoding utf8NoBOM
Copy-Item "d:\AI\project\hlyOptiScaler\OptiScaler.ini" "$payload\OptiScaler.ini"
"FAKE AMDXC64" | Set-Content "$payload\OptiScaler\amdxc64.dll" -Encoding utf8NoBOM
"FAKE GAME EXE" | Set-Content "$game\FakeGame.exe" -Encoding utf8NoBOM

"=== 快照：部署前 ==="
Get-ChildItem -Recurse (Join-Path $sandbox "FakeGame") | Select-Object FullName, Length
```

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet run --project src\Deployer.Cli -- plan `
  --payload $payload --install-root (Join-Path $sandbox "FakeGame") --deploy-dir $game `
  --exe FakeGame.exe --title "Fake Game" --platform Manual --profile amd-rdna4 `
  --backup-root $backup --out (Join-Path $sandbox "plan.json")
```

Expected（逐项核对）：
- `身份 DLL    : dxgi.dll  [Resolved]  纯新增`
- `将写入 4 个文件`，**最后一行是 `dxgi.dll`**，倒数第二行是 `OptiScaler.ini` 且标为 `[patch]`
- `setup_windows.bat` / `setup_linux.sh` 不出现在列表里
- `INI 覆盖` 里能看到 `[Upscalers] Dx12Upscaler: auto -> ffx`、`[ProcessFilter] TargetProcessName: auto -> FakeGame.exe`、`[Log] LogToFile: auto -> true`
- 退出码 `0`（`$LASTEXITCODE`）

Run:
```powershell
dotnet run --project src\Deployer.Cli -- apply --plan (Join-Path $sandbox "plan.json") --backup-root $backup --ledger $ledger
"=== 部署后 ==="
Get-ChildItem -Recurse (Join-Path $sandbox "FakeGame") | Select-Object FullName, Length
Select-String -Path "$game\OptiScaler.ini" -Pattern '^(Dx12Upscaler|UpscalerIndex|TargetProcessName|LogToFile)='
(Get-Content "$game\OptiScaler.ini" | Where-Object { $_.TrimStart().StartsWith(';') }).Count
```
Expected：`部署成功`；目录里出现 `dxgi.dll` / `OptiScaler.ini` / `OptiScaler\amdxc64.dll`；四个键都是钉死值；**注释行数与源 `OptiScaler.ini` 一致**（这是「保留注释」最直接的证据）。

Run:
```powershell
$manifest = Get-ChildItem -Recurse -Filter manifest.json $backup | Select-Object -First 1 -ExpandProperty FullName
dotnet run --project src\Deployer.Cli -- rollback --manifest $manifest --backup-root $backup
"=== 回滚后 ==="
Get-ChildItem -Recurse (Join-Path $sandbox "FakeGame") | Select-Object FullName, Length
dotnet run --project src\Deployer.Cli -- ledger --ledger $ledger
```
Expected：`回滚成功。还原 0 个原件，删除 4 个新增文件。`；目录里**只剩 `FakeGame.exe`**，`OptiScaler\` 目录也没了；`ledger` 命令能列出那条记录。

- [ ] **Step 4: 验证冲突处理（真磁盘）**

Run:
```powershell
"GAME OWN VERSION DLL" | Set-Content "$game\version.dll" -Encoding utf8NoBOM
"GAME OWN WINMM DLL"   | Set-Content "$game\winmm.dll"   -Encoding utf8NoBOM
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet run --project src\Deployer.Cli -- plan `
  --payload $payload --install-root (Join-Path $sandbox "FakeGame") --deploy-dir $game `
  --exe FakeGame.exe --platform Manual --profile amd-rdna4 --backup-root $backup
```
Expected：身份仍是 `dxgi.dll`（两个冲突文件都不在默认优先级表的前两位）。再加 `--platform Xbox` 重跑：Expected 变为 `version.dll`，且警告里有一条 `[Info] 因与游戏自带文件冲突，已跳过 winmm.dll，改用 version.dll。`

- [ ] **Step 5: 清理沙箱**

Run: `Remove-Item $sandbox -Recurse -Force`

**不要**把这个沙箱脚本提交进仓库；它是一次性验收手段。若想固化，归入计划 B 的 `--cli scan` 集成测试。

- [ ] **Step 6: Commit**

```bash
git add tools/OptiScalerDeployer
git commit -m "feat(deployer): add minimal CLI for plan/apply/rollback/ledger"
```

---

## Task 16: 回写 spec 与收尾

本计划在实现过程中对 spec 做了三处类型细化，必须回写到设计文档，否则 spec 与代码不一致（计划 B/C/D 会继续以 spec 为准）。

**Files:**
- Modify: `docs/superpowers/specs/2026-10-08-optiscaler-deployer-design.md`

- [ ] **Step 1: 把 §5.1 的 `IdentityDecision` 改为六参版**

原文：
```csharp
public sealed record IdentityDecision(
    DllIdentity Chosen, string FileName, IdentityStatus Status,
    IReadOnlyList<DllIdentity> SkippedDueToConflict, string? UserPromptMessage);
```
改为：
```csharp
public sealed record IdentityDecision(
    DllIdentity Chosen, string FileName, IdentityStatus Status,
    IReadOnlyList<DllIdentity> SkippedDueToConflict,
    BackupReason? ExistingFileReason,   // null = 该位置本来空着（纯新增）
    string? UserPromptMessage);
```
并在该代码块下方补一句：`ExistingFileReason` 存在的理由是让 planner 不必重读 `OriginalFilename` 就能区分「覆盖上次装的 OptiScaler」与「覆盖游戏自带文件」。

- [ ] **Step 2: 把 §5.1 的 `FileWrite` 改为带 `InlineContent`**

原文：
```csharp
public sealed record FileWrite(string SourcePath, string DestinationPath, long Length, string Sha256);
```
改为：
```csharp
public sealed record FileWrite(
    string SourcePath, string DestinationPath, long Length, string Sha256,
    string? InlineContent = null);   // 非 null 时写该内容（已 patch 的 OptiScaler.ini）
```
并在 §5.2 的表格里把 `OptiScaler.ini` 那一行的「说明」补上：以 `InlineContent` 形式携在 plan 里，executor 不重跑 IniPatcher。

- [ ] **Step 3: 给 §5.6 的 manifest 内容清单补上 `CreatedDirectories`**

原文：
> 目录内保持相对路径镜像 + `manifest.json`（PlanId、时间戳、Payload 版本、身份、全部 Backups、全部 Writes 目标路径、Profile 快照）

改为：
> 目录内保持相对路径镜像 + `manifest.json`（PlanId、时间戳、Payload 版本、身份、全部 Backups、全部 Writes 目标路径、**本次新建的目录列表**、Profile 快照）。目录列表是回滚能把 `OptiScaler\` 等新建空目录一并清掉的唯一依据。

- [ ] **Step 4: 给 §5.5 补上「manifest 先于一切写盘」这条不变式**

在 §5.5 「整计划」那段后面补一句：

> `manifest.json` 在执行**开始前**就写盘（`CreatedDirectories` 与 `BackupEntry.Sha256` 先置空、完成后回填）。因此只要 manifest 存在，任何中途失败——包括进程被强杀——都能完整回滚。`progress.json` 不是回滚的必要输入，它只是诊断信息与 Plan D 中主进程接管崩溃 Worker 的提示。回滚建立在单一数据源上，比「两个文件必须互相一致」健壮得多。

- [ ] **Step 5: 给 §5.5 补上 UTF-8 无 BOM 的要求**

在原子写步骤列表末尾补一条：

> 6. 写 ini 用**不带 BOM 的 UTF-8**。`OptiScaler.ini` 上游无 BOM，带 BOM 写回去会让 SimpleIni 把首行 section 读成 `\uFEFF[Upscalers]`，整个 `[Upscalers]` 段直接失效。

- [ ] **Step 6: 给 §10 补上重复部署的回滚陷阱**

在 §10 的失败点表格后补一段：

> **重复部署同一游戏的回滚顺序**：第二次部署时，影子备份里存的是第一次部署的产物（即 OptiScaler 自己的 DLL），不是游戏原件。因此只回滚最新一份会把游戏留在「装着 OptiScaler 但台账说已卸载」的状态。卸载必须**逆序回放该指纹下全部未回滚的份数**。

- [ ] **Step 7: 给 §5.1 补上跨版本持久化类型的前向兼容约定**

在 §5.1 的 `DeploymentManifest` / `InstallRecord` 定义附近补一段：

> `DeploymentManifest`（`manifest.json`）与 `InstallRecord`（`ledger.json`）是**跨版本长寿命持久化**类型：安装时写盘，可能数月后被更高版本的工具读回。`System.Text.Json` 在反序列化缺失 `required` 成员的 JSON 时会抛异常，因此 v1 发布之后，对这两个类型的任何新增字段**必须是可空类型或带默认值，禁止使用 `required`**——否则旧版本写下的 manifest/ledger 在新版工具下会读不回来，用户将无法回滚当初装过的游戏，而「任何时候都能回滚」正是本工具的核心承诺。若确需破坏性变更，先引入 `SchemaVersion` 并写兼容分支。（`DeploymentPlan` 只在同一二进制版本内跨进程传递，不受此约束。）

- [ ] **Step 8: 给 §5.6（影子备份）与 §11.1（回滚）补上哈希校验链**

spec 初稿里回滚被描述成「把备份件拷回去」，没提校验。实现时发现这不够：备份件在存放期间可能被磁盘错误、杀软或云同步截断，盲拷回去会**把坏文件装进游戏目录并宣称回滚成功**，而且此时再没有第二份副本。在 §5.6 补：

> 影子备份在拷贝时记下原件的 SHA256（`BackupEntry.Sha256`，写进 `manifest.json`）。拷贝前后各算一次，不符即抛——备份阶段就烂掉的备份件比没有备份更危险。哈希只能在真正拷贝的那一刻记下（planner 不哈希游戏自带文件，太慢也不是它的职责），所以 `Backup` 返回带哈希的新 entry，executor 必须接住返回值并在备份完成后**立即**重写 manifest；等到全部写完再回填会留下一个窗口，在那里崩溃就会让磁盘上的哈希全是 null。

在 §11.1 补：

> 回滚还原每个文件后重算哈希并与 `BackupEntry.Sha256` 比对。这**不是**「校验它等于校验自己」：哈希是备份当时记下的，与回滚那一刻的磁盘状态分属两个时间点，相互独立。不符则计入 `RollbackResult.Errors` 且 `Success=false`——此时确实没有更好的副本可用，如实验警是唯一正确的做法，静默宣称成功则是本工具承诺绝不发生的事。`Sha256` 为 `null` 只可能是该字段引入之前写下的旧 manifest，此时跳过校验：不能因为新增了一个校验字段就把老用户的备份变成废纸。

同时在 §5.6 补一句关于 `manifest.json` 读失败的规定：

> 「文件不存在」是合法状态（还没部署过、或已被 `Prune` 清掉），返回 null；「存在但读不出来」必须抛并带上内层异常。两者混为一谈的代价是：回滚失败时只会得到「清单损坏或被删除」，而真正的起因（JSON 半截写入、枚举名对不上、磁盘读失败）连同堆栈一起被吞掉。「无法回滚」是用户唯一拿不回游戏的那条路径，它必须带着现场信息响。唯一的容忍点是 `ListPlanIds`：一份坏清单不该把整个列表挡死，否则用户连还能用的备份都看不见。

- [ ] **Step 9: 全量验证**

Run:
```powershell
cd "d:\AI\project\hlyOptiScaler\tools\OptiScalerDeployer"
dotnet build OptiScalerDeployer.sln -c Release
dotnet test OptiScalerDeployer.sln -c Release
```
Expected: build `0 警告 0 错误`；test `失败: 0`

- [ ] **Step 10: Commit**

```bash
git add docs tools
git commit -m "docs(deployer): sync spec with Plan A type refinements"
```

---

## Self-Review

按 writing-plans 的自审清单，对着 spec 逐项过一遍。

### 1. Spec 覆盖度

| Spec 章节 | 计划 A 里的落点 | 状态 |
|---|---|---|
| §1.5 非目标：不做批量部署 | `DeploymentPlanner.Plan` 只接受**单个** `GameTarget`，接口层面无法批量 | ✅ |
| §1.5 非目标：不做反作弊检测 | 全计划无 EAC/BattlEye/Vanguard 字样 | ✅ |
| §2 权限模型（方案 A） | `ElevationRequirement` 在 plan 里计算；**提权通道本体属计划 D** | ⚠ 部分（按计划分片） |
| §3.1 目录布局 | Task 1；额外多了 `src/Deployer.Cli/`（计划分片说明里已交代） | ✅ |
| §3.2 十一个模块 | Ini(T4) Profiles(T5) Payload(T6) Identity(T7) Planning(T8-9) Deployment(T10-12) Persistence(T13)；GameDiscovery/Inspection 属计划 B，HealthCheck/Elevation 属计划 D | ⚠ 部分（按计划分片） |
| §3.4 Plan/Execute 分离 | Task 9 的纯函数 planner + Task 12 的无决策 executor；`Plan_serializes_and_roundtrips` 盯住跨进程传输能力 | ✅ |
| §5.1 数据结构 | Task 2 全部定义，Task 7/10 三处细化并在 Task 16 回写 spec | ✅ |
| §5.2 文件映射规则 | Task 8 `PayloadWalker.ExcludedRootFiles`；`Plan_never_writes_the_installer_scripts` | ✅ |
| §5.3 部署目录 | planner 不重算（以传入的 `GameTarget.DeployDirectory` 为准），但对 UE 游戏部署到非 `Binaries\Win64` 产 Caution 警告；exe 选择逻辑属计划 B | ✅ |
| §5.4 写入顺序 | `Write_order_is_subtree_then_ini_then_identity_dll` | ✅ |
| §5.5 原子写 | Task 10 `AtomicFileWriter`；SHA256 校验；progress.json | ✅ |
| §5.6 影子备份 | Task 10 `BackupStore`；路径规则、manifest、Prune(3) | ✅ |
| §6 身份决策 | Task 7 全分支（三张优先级表 + 三种槽位判定 + 全冲突 + ASI） | ✅ |
| §7 体检 | **计划 B**；本计划只定 `InspectionSummary` 数据形状 | ⚠ 按计划分片 |
| §8.1 内置 Profile | Task 5；amd-rdna4 / amd-rdna4-native / nvidia-rtx / 帧生成附加档 | ✅ |
| §8.2 IniPatcher 保留注释 | Task 4；两条不变式 + section 作用域 + 大小写 + 追加位置 | ✅ |
| §8.3 LogToFile 坑 | `Plan_turns_on_file_logging_because_default_auto_is_false` | ✅ |
| §8.4 每游戏单独注入 | `[ProcessFilter] TargetProcessName` 自动填 + 指纹粒度台账/备份；`Two_different_games_never_share_backup_or_ledger_state` | ✅ |
| §9 Payload 来源 | Task 6；LocalBuild + Manual + Chain。**GitHubRelease 属计划 D** | ⚠ 部分 |
| §10 错误处理 | Task 3 六个异常类；`DeploymentResult` 不抛异常。HealthCheck 相关属计划 D | ✅ |
| §11.1 单测清单 | IniPatcher ✅ / DllIdentitySelector ✅ / DeploymentPlanner ✅ / RollbackExecutor ✅ / IJobTransport ⚠（计划 D）/ PeImportReader+UpscalerSdkDetector ⚠（计划 B） | ⚠ 部分 |
| §11.2 集成测试三种假游戏 | Task 6 `SyntheticPayloadFactory` + Task 9 `FakeGameFactory` + Task 14 `EndToEndTests` | ✅ |
| §11.3 手工验收 | Task 15 Step 3/4（沙箱验收）。**真游戏验收需计划 C 的 UI 或手动跑 CLI** | ✅ |
| §13 风险：上游 INI 键名变动 | `IniPatcher` 对缺失键追加而不报错；Task 5 Step 3 给了核对命令 | ✅ |

### 2. 占位符扫描

无占位符、无 TBD / TODO、无「实现适当的错误处理」类含糊指令。每个 Step 给出的都是**完整可直接落盘编译**的代码，不存在「以下省略」「参照上文补全」的段落。

初稿里曾有五处需要先纠正再执行的缺陷，已在本计划内就地改净，执行者照抄即可：

| 缺陷 | 若不修会怎样 | 现状 |
|---|---|---|
| `Hashing.Sha256OfFile(string)` 直接用 `System.IO.File` | 在 `MockFileSystem` 上会去真磁盘找文件，所有依赖哈希的测试全红 | 改为 `Sha256OfFile(IFileSystem, string)`，调用点同步 |
| `IniPatcher` 追加键时依赖 `Dictionary` 枚举序 + 两次 `Reverse()` | 同一 section 追加多个键会乱序，且可能落到 section 末尾空行之后 | 改用显式 `orderedKeys` 正序插入 + `LastContentLineIndex`，补两个测试钉住 |
| `IdentityDecision` / `FileWrite` / `DeploymentManifest` 少一个字段 | planner 无法区分覆盖原因、executor 拿不到 patch 后的 INI、回滚删不掉自建空目录 | 三处已细化（Task 7/9/10 的 Step 0），Task 16 回写 spec |
| planner 调用两个不存在的成员 | 直接编译不过 | 改为 `.AppendedEdits` 与 `BuildWarnings(..., backups)` |
| CLI 里多一个 `ProfileStore` 包装类 | `JsonProfileStore` 已实现 `IProfileStore`，包装纯属噪音 | 已删，`Plan` 直接构造 `JsonProfileStore` |
| `ChecksumMismatchException` / `HealthCheckFailedException` 把构造函数参数用在属性初始化器里 | 4 个 CS0103 硬编译错误，Task 3 直接建不起来 | 已改为构造函数体 / 表达式构造体（Task 3 Step 1） |
| 5 处断言用 `Assert.Equal` 直接比含 `IReadOnlyList<T>` 成员的 record | record 对这类字段是**引用相等**（xUnit 又会优先走 `IEquatable<T>` 而绕开自己的集合比较），测试必然变红，且失败信息极具误导性 | 新增 `JsonAssert.Equivalent` 并改完 5 处（Task 3 Step 7、Task 9 两处、Task 10 两处） |
| `BackupEntry` 不记哈希，`CopyBack` 的注释还把它说成「校验它等于校验自己」 | 影子备份若在存放期间被磁盘错误/杀软截断，回滚会**把坏文件装回游戏目录并报 `Success=true`**，而且此时再没有第二份副本。这个理由不成立：哈希可以在备份时记下，与回滚那一刻的磁盘状态分属两个时间点 | `BackupEntry` 加可空 `Sha256`；`Backup` 改为返回带哈希的 entry 且拷贝前后各算一次；executor 备份完立即重写 manifest；`CopyBack` 还原后校验，不符计入 errors（Task 2/10/11/12，Task 16 Step 8 回写 spec） |
| `BackupStore.ReadJson` 把「存在但读不出来」吞成 null | 回滚失败时只能报「清单损坏或被删除」，真正的起因（JSON 半截写入、枚举名对不上、磁盘读失败）连同堆栈一起被吞——而这是用户唯一拿不回游戏的那条路径 | 改为抛 `InvalidDataException` 并包住内层异常；`RollbackExecutor` 把原因写进 `ErrorMessage`；`ListPlanIds` 作为**唯一**容忍点并写明理由（Task 10/11） |
| CLI 的 `Fail(ex.Message)` 把 `Diagnostic` 整条丢掉 | `DeployerException.ToString()` 追加的诊断串在 CLI 路径上无人调用；现场信息没有别的落点，用户报 bug 时只能说「错误：找不到 OptiScaler 产物」 | `Fail` 加可选 `Exception?` 参数，额外打一行 `ex.ToString()`；顶层 catch 改成 `Fail(ex.Message, ex)`（Task 15） |

执行过程中唯一需要临时调整的既知项：Task 12 Step 2 的 `Executor()` 辅助方法依赖 Task 11 的 `RollbackExecutor`。若你打乱任务顺序、先做 Task 12，那一个「失败中途回滚」的测试需临时 `Skip`；按 Task 编号顺序执行则无此问题。

### 3. 类型一致性

已逐项校对：

| 类型/方法 | 定义处 | 使用处 | 一致 |
|---|---|---|---|
| `IniPatchResult.AppliedEdits` / `.AppendedEdits` | Task 4 Step 1 | Task 9 Step 5（已修正占位名） | ✅ |
| `FileWrite.IsInline` / `.InlineContent` | Task 9 Step 0 修正 | Task 10 `AtomicFileWriter`、Task 15 `PrintPlan` | ✅ |
| `IdentityDecision`（6 参） | Task 7 Step 0 修正 | Task 3/10/11/12/14 所有构造处 | ✅ |
| `DeploymentManifest.CreatedDirectories` | Task 10 Step 0 修正 | Task 11 `RollbackExecutor`、Task 12 `BuildManifest` | ✅ |
| `Hashing.Sha256OfFile(IFileSystem, string)` | Task 3 Step 3 → Task 8 Step 8 修正 | Task 10 `AtomicFileWriter`、Task 9 `DeploymentPlanner`、Task 11/14 的 `Snapshot` | ✅ |
| `PayloadWalker.Walk` | Task 8 Step 8 | Task 9 planner | ✅ |
| `IdentityContext`（5 参） | Task 7 Step 2 | Task 9 planner | ✅ |
| `DllIdentitySelector.PriorityFor` / `XboxPriority` | Task 7 Step 5 | Task 7 测试 | ✅ |
| `IBackupStore.DirectoryFor/ManifestPathFor/ProgressPathFor` | Task 10 Step 5 | Task 11/12/14/15 | ✅ |
| `RollbackResult`（4 参） | Task 2 Step 10 | Task 11/12 | ✅ |
| `DeploymentResult`（5 参） | Task 12 Step 1 | Task 12/15 | ✅ |
| `BackupStore.DefaultRoot` / `JsonInstallLedger.DefaultPath` / `JsonProfileStore.DefaultPath` | Task 10/13/5 | Task 15 CLI | ✅ |
| `InstallRecord`（9 字段） | Task 2 Step 11 | Task 13/15 | ✅ |
| `BuiltinProfiles.AmdRdna4Id` / `.All` / `.FrameGenAddOn` | Task 5 Step 3 | Task 9 planner、Task 15 CLI | ✅ |

### 4. 关于 Expected 数字

每个 Step 里写的「通过: N」是**累加推算值，仅供参考**。真正的闸门是 `失败: 0`。如果实际数字对不上但失败数为 0，不要为了凑数字去增删测试——把计划里的数字改掉即可。若某个 Theory 的 case 数与预期不符，先确认 `MemberData` / `InlineData` 有没有写错。

### 5. 本计划完成后能得到什么

一条完整可用的命令行链路：对**任意目录**（包括真游戏目录）生成部署计划 → 审阅 → 安全部署 → 精确回滚，全程影子备份、原子写、写入顺序保证、INI 注释零损失。缺的是「自动找到游戏」与「自动判断游戏用什么 API/SDK」（计划 B）、图形界面（计划 C）、提权与健康检查（计划 D）。

---

## Execution Handoff

计划已完成并保存到 `docs/superpowers/plans/2026-10-08-optiscaler-deployer-plan-a-core.md`。两种执行方式：

1. **Subagent-Driven（推荐）** —— 每个 Task 派一个全新 subagent，Task 之间做两阶段 review，迭代快、上下文干净
2. **Inline Execution** —— 在当前会话里用 executing-plans 批量执行，带检查点

选哪种？
