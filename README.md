# LocaleSmith（译匠）

LocaleSmith，中文名“译匠”，是一款面向 Minecraft Java 模组、材质包和光影包的 Windows 原生翻译工作台。当前仓库包含可构建的 Rust 原生扫描核心、.NET 10 业务与安全基础设施、WinUI 3 MVVM 桌面界面、独立 MCP stdio 进程和 x64 MSIX Packaging Project。

## 当前实现

- Rust `cdylib` 通过稳定 C ABI 输出类型化 JSON 清单；扫描 JAR/ZIP 的安全路径、Fabric/Forge/NeoForge/Quilt/legacy Forge 元数据、`modId`、语言资源、签名证据和 Java `ldc`/`ldc_w` 字符串引用。
- .NET 通过 `LibraryImport` 直接调用原生 DLL，并以事务工作区处理 JAR、ZIP 和展开后的材质包/光影包目录。目录输入先生成经过 metadata 与 SHA-256 复核的不可变快照。
- 翻译流水线读取 `.json`、`.lang`、`pack.txt` 和受支持的 `.mcmeta` 文本，按内容哈希增量复用。每个队列作业只生成用户所选的正式版或语气版单一产物；另一风格可另行入队，并复用相同原文哈希下已缓存的风格译文。普通条目按字符目标分批；超过该目标的单条文本不会被拒绝、拆分或压缩，而会原样单独交给 provider，真实上下文超限会作为 provider 错误返回。现代 Minecraft 输出使用小写 `zh_cn` 资源名。
- Ollama、OpenAI-compatible Chat Completions 和 Anthropic Messages 适配器实现统一 `IModelService`；网络向导内置 DeepSeek、Qwen、Xiaomi MiMo、MiniMax、OpenAI、豆包、智谱 GLM、Kimi 八个可编辑预设及自定义入口，模型源可在界面中新增、编辑、删除、测试并即时切换。Ollama 默认地址为 `http://127.0.0.1:11434`。
- OpenAI-compatible `tool_calls`、Anthropic `tool_use` 和 Ollama `tool_calls` 已接入统一、有轮次/调用数上限的 provider-native 工具循环。Kimi K3 返回的私有 `reasoning_content` 只在同一 Kimi 工具循环内有界、原样回放，不并入用户可见内容，也不会跨 provider 发送。模型侧只暴露只读 `system_context` 与只提出命令的 `cli_propose`；执行命令不属于工具循环。
- 云端 API Key 独立保存在 Windows Credential Manager。非密钥应用配置使用 AES-256-GCM 加密，随机 256 位主密钥同样由 Credential Manager 托管。模型源保存/删除与凭据变更采用补偿式事务，失败时恢复旧凭据，并清零临时可变缓冲。
- 增量缓存键隔离输入包身份、目标语言、已捕获的模型源和翻译契约版本；旧缓存会安全 miss。输出目录在每次作业开始时从最新 `WorkspacePath` 配置解析，并写入其 `LocaleSmith.Output` 子目录。
- WinUI 3 包含首次使用引导、翻译队列、双语助手、模型源管理、设置和 CLI 风险确认界面；Presentation 层采用 CommunityToolkit.Mvvm，视图不直接访问 HTTP、密钥或 Rust。助手不修剪或为用户消息/历史设定人工字符门槛；完整 UI 会话会交给选中 provider，上下文超限由其或工具编排安全包络显式报错，不假设 Chat Completions 会自动压缩。机器上下文、HTTP 响应/错误体及工具调用仍有安全上限。
- 独立 `JaxI18n.McpHost` 实现有界、限流的 MCP stdio 服务。stdio 名称为 `system.context`、`cli.propose`；模型适配层映射为 provider 可接受的 `system_context`、`cli_propose`。`cli.execute` 不出现在工具列表中。
- CLI 基础设施包含命令绑定的一次性确认 token、动态可执行文件白名单、绝对正则黑名单、沙箱工作目录、30 秒上限、JSONL 审计，以及 Windows Low IL restricted token、私有 desktop、挂入 Job 后才恢复执行的受限子进程启动器。Windows drive-root-relative 路径（如 `/Windows/...`）、junction 和 malformed path 均 fail closed；含 `api-key`、`token`、`secret`、`password` 或 `credential` 标记的参数在批准前拒绝。允许启动的命令必须先成功写入带 correlation id 的 `Started` 审计，审计不可用则不启动。

## 必须明确的边界

- **字节码外部化不是通用改写器。** 只改写经结构证明的精确模式：紧邻的 `ldc`/`ldc_w` 字符串与 Mojang `Component.literal(String):MutableComponent` 静态调用；它保持指令长度、改为精确 `translatable(String)` 引用，并生成 `assets/<modid>/lang/en_us.json` 原文 fallback 和本作业所选风格的目标语言条目。分支/异常边界、未知 opcode、混淆或任何不精确模式都不改写；提交前重新扫描验证，失败整作业回滚。尚未用真实 Minecraft/loader 兼容矩阵证明游戏内语义。
- **Low IL 不是 AppContainer。** 当前受限 token 会禁用最大权限并启用 LUA 限制，设置 Low integrity SID `S-1-16-4096`，使用私有 desktop、显式继承的标准流管道和带进程数/内存/CPU/UI/kill-on-close 限制的 Job Object；进程先以 suspended 创建，成功挂入 Job 后才 `ResumeThread`。这仍不能阻止读取当前用户 ACL 原本允许的文件，也不默认阻断网络。`WorkingDirectory`/参数路径校验是应用策略，不是内核文件系统沙箱；Low IL 的写入还取决于目标目录的 Mandatory Integrity Control 标签。没有 unrestricted fallback。
- **CLI 执行永远不由模型授权。** provider-native 循环只允许读取安全上下文和校验/记录命令提议。主界面随后展示完整命令，用户必须勾选“我已知晓风险”并确认，应用重新运行策略并签发一次性 token 后才可执行。
- **重打包不是字节级无损。** 原输入始终保留不动，清单与关键元数据会校验，事务失败会回滚；但 ZIP 会重新压缩，原压缩流、extra field 字节/顺序和逐条目注释等不保证完全一致。数字签名修改默认阻断，可选 unsigned copy 会移除签名文件；重新签名尚未实现。
- **修改签名 JAR 必然使原签名失效。** 本项目保留原输入与签名证据，但不能在没有原作者私钥的情况下维持原签名；当前只支持阻断修改或显式生成 unsigned copy，不提供重签实现。
- **MSIX 尚不是生产发布件。** 当前 `0.1.0.1` 开发包已完成 payload/PRI/MCP、密码学签名和 loose AppsFolder 启动验证；但证书 `CN=JaxI18n Development` 是自签根、未受信任且无 timestamp，四个检查的 Root/TrustedPeople store 均没有匹配证书。生产前仍必须替换发布身份/证书，并完成可信时间戳和干净机正式安装、升级、卸载矩阵。

## 仓库结构

```text
native/jax_i18n_core/       Rust ZIP/JAR、metadata 与 classfile 扫描核心
src/JaxI18n.Core/           领域模型和统一服务契约
src/JaxI18n.NativeInterop/  C ABI 投影、DLL 解析与类型化清单
src/JaxI18n.Application/    翻译编排、增量计划、队列与事务边界
src/JaxI18n.Archive/        安全快照、提取、所选单风格重建、验证与回滚
src/JaxI18n.Infrastructure/ provider、Credential Manager、AES-GCM、CLI 与环境检测
src/JaxI18n.Mcp/            MCP JSON-RPC/stdio 协议与工具目录
src/JaxI18n.McpHost/        独立 MCP 控制台宿主
src/JaxI18n.Presentation/   可测试的 MVVM ViewModel 与 UI 契约
src/JaxI18n.App/            WinUI 3 视图、组合根和本地应用服务
tests/                      八个 .NET 测试项目和受限 CLI probe
packaging/                  x64 WAP/MSIX manifest、双语 PRI 资源和图标
design-system/              UI/UX Pro Max 设计系统与页面约束
docs/                       架构、威胁模型、路线图与验证记录
```

## 构建要求

- Windows 10 1809 或更高版本；WinUI 开发建议 Windows 11。
- .NET SDK 10.0.302，由 `global.json` 固定。
- Rust 1.97.1 MSVC 工具链及 `rustfmt`、`clippy`，由 `rust-toolchain.toml` 固定。
- Windows SDK 10.0.26100 和 MSVC/C++ 构建工具。
- Windows App SDK 2.3.1 与 CommunityToolkit.Mvvm 8.4.2，由 NuGet 还原。
- 生成 MSIX 还需要含 Desktop Bridge/WAP targets 的 Visual Studio Developer PowerShell；普通 `dotnet` CLI 不构建 WAP 工程。

先构建 Rust release DLL，再构建 .NET：

```powershell
cargo build --manifest-path native/jax_i18n_core/Cargo.toml --release
dotnet restore JaxI18n.slnx
dotnet build JaxI18n.slnx -c Release
```

运行验证门：

```powershell
cargo fmt --manifest-path native/jax_i18n_core/Cargo.toml --all -- --check
cargo clippy --manifest-path native/jax_i18n_core/Cargo.toml --all-targets --all-features -- -D warnings
cargo test --manifest-path native/jax_i18n_core/Cargo.toml --all-targets
dotnet test JaxI18n.slnx -c Release
```

当前源码验证基线为 .NET Release **260/260**、0 warnings/0 errors，`dotnet format` clean；Rust **26/26**；应用 `zh-CN`/`en-US` `.resw` 各 **332** 个且 key 对齐。最终源码安全审计为 **FINAL GREEN（P0/P1/P2 均为 0）**；这是源码审计结果，不替代外部渗透测试或文档中列出的残余风险。当前开发 MSIX 为 `0.1.0.1`、36,244,251 bytes、SHA-256 `D809405DABCB632AFC0ECF5B8CFA2EAA9B35D162F6D35A73034758691AC276D1`；完整验证和非生产信任边界见 `docs/verification.md`。
