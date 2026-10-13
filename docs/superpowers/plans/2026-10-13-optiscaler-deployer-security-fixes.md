# OptiScaler Deployer 安全修复与验证记录

日期：2026-10-13

本轮修复了安全审查指出的三个问题。Release 构建成功；全量测试 Core 336 项、App 38 项，共 374 项通过，0 失败、0 跳过。定向回归还验证了提权策略、普通权限下 CLI 回滚和受保护目录桌面提示。

## 修复

1. **提权 Worker 信任边界**：Worker 必须位于 Program Files 受保护目录。启动前检查 Worker 包目录、文件及其 ACL、所有者和重解析点，并持有文件系统句柄以阻止验证期间的替换。此方案依赖本机 ACL，不提供 Authenticode 签名或发布哈希校验；本轮未在另一台机器上安装并实际触发 UAC 验证。
2. **提权进程回滚**：桌面、CLI 和 Core 回滚入口会在读取恢复清单前检查 Windows 令牌；无法安全确认平台或令牌状态时拒绝继续。管理员权限进程中的路径型回滚被拒绝，包括 Worker 内部自动恢复。受保护目录部署若发生失败且自动恢复被拒绝，必须保留恢复清单和备份；普通用户权限能够写入目标目录时，可恢复目录权限后以普通用户运行恢复。若普通权限不能恢复，本版本暂不支持安全的管理员恢复。不要从管理员终端运行 CLI 回滚。
3. **部分部署恢复范围**：部署先写入不授权文件更改的空清单，验证完原件备份后再发布恢复范围。每个文件写入前保存待处理路径和预期 SHA256，成功后才将其记录为已完成。恢复只处理已完成项，以及内容哈希与待处理意图匹配的文件，避免因中断而把计划中尚未写入的文件误当成本次新增文件删除。

## 限制与验证边界

普通用户路径回滚仍依赖路径和重解析点检查；这些检查不等同于对每次文件操作使用 no-follow 句柄，不能证明并发目录替换下没有 TOCTOU 竞争。不要将其描述为完全消除了普通权限下的竞态风险。

本轮没有触发真实 UAC、安装发布包到 Program Files、执行受保护游戏目录的管理员恢复，也没有扫描、部署或修改现有游戏。本机做过只读 ACL 检查；Worker ACL 规则和令牌决策逻辑通过代码审查及定向自动化测试验证，但尚未在 Program Files 对完整 Worker 包运行 ACL 获取/锁定流程。跨机器安装与真实系统权限流程仍未验证。

单独发布到安全修复目录时连续两次失败。Worker 发布阶段的 MSBuild `ConvertToAbsolutePath` 收到含 `E-SafeNet\0LOCK` 的二进制路径并因 null 字符退出；删除并重新生成了已确认位于 Worker Release `obj` 目录中的单个文件列表后，错误仍重现。`artifacts/security-fixes-20261013/published` 只有部分 App 文件，不能当作可交付版本。

为继续验证可运行文件，将成功 Release 构建输出完整复制到 `artifacts/security-fixes-20261013/verified-release`。App、Core 和 Worker 的入口、程序集、依赖清单及运行时配置均存在；另核对了 8 个关键 App/Core/Worker 文件的 SHA256 与成功 Release 构建目录一致。此目录是从构建输出组装的验证副本，不是 `dotnet publish` 结果或安装包。

针对该验证副本运行了 15 项离线合成游戏检查，全部通过：发现与计划、审核门、部署与台账、重复计划拒绝、合成日志健康检查、物理回滚后的文件哈希和目录恢复、实际启动 Worker 子进程、重复 job 拒绝以及 Worker 部署回滚。本机被 git 忽略的验收证据位于 `tools/OptiScalerDeployer/artifacts/security-fixes-20261013/results.json`，并非仓库交付文件。桌面程序启动后窗口响应正常，标题为“OptiScaler 部署工具”，正常关闭并以退出码 0 结束。没有联网、触发 UAC、扫描或修改现有游戏。全量 Release 构建和测试结果不受独立 publish 失败影响。

## 后续验证更新（2026-10-13）

Xbox 自定义库发现增加 marker 和 manual-folder 回归测试，使用 `MockFileSystem`，不访问真实 Xbox 游戏目录。全量 Release 测试从安全修复时的 374 项更新为 383 项通过（Core 345、App 38），零失败、零跳过。

针对 GitHub API 403/429 新增 stable 官方 v0.9.4 固定描述符回退后，全量 Release 测试为 387 项通过（Core 349、App 38），零失败、零跳过。隔离真实下载 harness 先让 API 返回 403，再经生产下载、redirect allowlist、大小上限、SHA256、解包和缓存路径处理官方资产；实际 archive SHA256 为 `575CB4DF866116093DF75AF607E37FD70E10F5163E0F23FD5C804142E80EF0AD`，匹配固定值，成功解出 21 个文件，DLL 版本 `0.9.4.0`。第二次调用命中相同缓存，未再次请求资产。该运行及 `results.json` 存在本机被 git 忽略的 `tools/OptiScalerDeployer/artifacts/payload-rate-limit-fix-20261013/` 下，不属于仓库交付物。

根目录启动脚本现依次选择仓库内完整 Release build 输出、`artifacts/publish/`，最后才选择本机验收副本。build 输出含 App 运行依赖及完整 `worker/` 目录；单独复制 EXE 不能作为完整应用。当前验证目录仍由成功的 Release build 输出组装，不是 `dotnet publish` 产物或安装包。根入口启动 smoke 已通过，窗口响应正常并可正常关闭。真实游戏、真实 UAC、跨机器安装和 GPU/驱动兼容性仍未验证。
