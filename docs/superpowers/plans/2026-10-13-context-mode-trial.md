# context-mode 本地 MCP 试用记录

日期：2026-10-13

## 范围与配置

在隔离运行目录中试用 `context-mode` 1.0.169，Node 24.14.0、npm 11.9.0，直接 MCP SDK 依赖为 `@modelcontextprotocol/sdk` 1.26.0。此次通过单独的 stdio MCP 客户端调用服务；它验证 JSON-RPC 行为，不代表当前 Codex 桌面会话已经加载该工具。试用完成时，当前桌面线程仍未暴露 context-mode 工具，需要在新的或重载的 Codex host 中单独检查。

项目级 MCP 配置使用精确包版本及隔离 storage，并仅允许 `ctx_execute`、`ctx_execute_file`、`ctx_batch_execute`、`ctx_index`、`ctx_search` 五个工具。项目此前已受信任。全局 Codex 配置的试用前后哈希一致；没有改变全局配置、trust、hooks 或 feature flags。项目 `.codex` 配置、context-mode runtime、依赖包和 storage 是本机试验材料，不属于本报告或仓库提交。

## 调用与观测

以七个输入文件的原始 UTF-8 字节共 68,656 bytes，另组成带文件名和行号的 79,138-byte composite。工具调用共 8 次，包含一次失败的 `ctx_execute_file` 和在同一输入上的修正重试。累计 request JSON 为 3,732 bytes；SDK 解析的响应重新序列化为 28,226 bytes，其中工具 content text 共 27,017 bytes；各调用累计延迟 498 ms。首次 `tools/list` 的保存文件是 pretty-printed SDK 结果，大小 35,794 bytes；compact SDK 结果为 30,026 bytes，发现耗时未单独计时。

这些数字是 SDK 解析结果的重新序列化大小，不是 MCP 线缆 frame 大小；wire envelope 未直接测量。content text 比原始输入字节少 60.6%，比带文件名 composite 少 65.9%。这些只是字节数比较，不是 token、延迟或费用节省证明；有针对性的 `rg` 或脚本也可以缩小输出。context-mode `ctx_stats` 的供应商估算不作为独立测量结果。

试验 runtime dependency tree 占 139,237,469 bytes，隔离 SQLite storage 占 546,054 bytes。Node 报告 `node:sqlite` experimental warning，但此次 MCP 调用没有因此失败。较大依赖体积和独立 stdio 客户端的配置成本应一并考虑。

## 隔离边界

隔离没有完全验证。`CONTEXT_MODE_DATA_DIR` 隔离 Codex adapter 自身的 session/memory 数据，但 pinned bundle 的旧 `ctx_stats` 路径未收到自定义 `memoryRoot`，会枚举默认 Claude projects 下 memory 文件的文件名和前缀类别；观察到 19 项、6 个项目的汇总，没有读取 memory 正文。自动 W 记录也会调用该路径计算 `totalEvents`，因此仅不调用 `ctx_stats` 不能证明没有其他全局元数据访问。

没有改 `CLAUDE_CONFIG_DIR`，因为它也可能改变全局安全策略加载位置。试验后只读检查没有发现默认全局 Codex context-mode session 数据文件。此结果不消除上面的 Claude 文件名/类别元数据限制，也不证明进程整体没有其他元数据读取。

Hooks 未配置或测试；compaction 未测试。新连接成功搜索此前索引内容，只证明试验 storage 的索引持久化，不证明 compaction 恢复行为。后续如测试需走独立的原生信任流程，并单独确认具体行为。

## 证据位置

原始请求/响应、schema、基线、试用摘要和完整 runtime 文件位于本机 git-ignored 的 `tools/OptiScalerDeployer/artifacts/context-mode-trial-20261013/`。它们未复制到仓库交付范围；报告中的数值来自该本机试用记录。该目录包含 project runtime、`node_modules` 和 storage，不应加入提交。
