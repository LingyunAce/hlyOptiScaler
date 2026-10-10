# OptiScaler Deployer Plan B 实施记录

## 范围

本阶段把发现平台安装目录、选择单个程序后的静态检查以及检查结果到计划生成的链路接入 Plan A Core/CLI。Plan C 的 WPF 界面、Plan D 的提权 Worker、在线 payload 和启动后健康检查仍未实现。

## 已落地的工作

- `IGameScanner` 插件架构组合 Steam、Epic、Xbox 和用户登记的手动目录。结果提供绝对安装路径、候选 EXE、可写性和可用的平台标识；相同路径的发现结果会合并。
- `scan` 输出候选程序、Xbox `PackageFamilyName` / `AppId` 与扫描警告。`scan --manual-root <dir>` 可只检查指定目录，不遍历主机游戏库；`manual-add` / `manual-remove` 管理持久目录。
- `inspect` 对明确选定的 EXE 显示静态体检。`plan` 使用同一体检结果，不再从命令行猜测 API 或 DLSS；无效、托管、x86、无支持 SDK 的程序会生成阻塞计划。
- 物理路径含 `WindowsApps` 的 Xbox 游戏无条件阻塞；解析到真实 XboxGames 目标的 junction 路径仍按实际目标检查。SDK 文件匹配要求具体 `.dll` 文件名，`.txt` 与 `.bak` 等文件不会当作 SDK。
- 体检生成 5 个上游 `[Inputs]` hook 启用开关（DLSS、XeSS、FSR2、FSR3、FFX API），并覆盖 profile 中同名键；真实体检的帧生成信号与超分 SDK 分开，避免只检测到 DLSS 就启用 `dlssg` 输入。
- 目标目录可写性由唯一临时文件探测。`Unknown`、`NeedsElevation` 和 `Blocked` 均阻止当前 CLI 部署；需要提权时待 Plan D Worker 支持。
- 扫描出现警告时不根据缺席推断卸载。台账对账仅对扫描结果中明确匹配、可读取且没有符号链接/重解析目录证据的部署路径检查身份 DLL；读取不完整会保留原状态。
- 已知 Xbox 包元数据范围来自当前 Windows 用户，不枚举其他账户，也不需要提升进程权限。只读 smoke 验证使用 Windows PowerShell 5.1 的 WinRT 包查询，返回 209 行合法 JSON；`AppId` 来自单个 manifest `Application.Id`，不拼入 Package Family Name。没有执行全盘扫描或真实游戏兼容性验收。实现依据：[FindPackagesForUser](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser)。PE 读取格式依据：[Microsoft PE Format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)。

## 验收与限制

最终验证已通过：Release 与 Debug 构建均为 0 警告、0 错误；Release 全量 `--no-build` 测试 277 通过、0 失败、0 跳过。分组定向结果为 discovery 20、inspection 20、integration 41；integration 数包含 Plan A 用例，分组数字不相加作为全量总数。自动化验证使用合成的有效 AMD64 PE 和隔离临时目录，覆盖扫描到检查、计划、部署、回滚以及阻塞分支。

Xbox 当前用户包查询还通过 Windows PowerShell 5.1 的 raw XPath / WinRT 只读 smoke，输出 209 行合法 JSON；结果没有写入项目。没有运行全盘主机扫描、真实游戏、驱动或 GPU 兼容性验收，也没有改写现有游戏目录。静态导入和旁置 DLL 检查不能证明游戏会在运行时加载 SDK。工作区改动尚未提交。

## 后续

Plan C 可复用 `GameLibrary`、`IGameInspector`、`DeploymentPlanner`、台账和 CLI 已验证的数据契约实现游戏列表、候选程序选择与计划预览。Plan D 再增加受控提权执行，然后才能放开 `NeedsElevation` 部署。
