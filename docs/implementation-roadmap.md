# LocaleSmith | 译匠 实施路线图与当前状态

> 状态日：2026-08-25
> 本文用“已实现 / 有限实现 / 待完成”区分源码事实与发布目标。本文当前源码自动化基线为 .NET Release **855/855**、Rust **28/28**。

## 1. 当前里程碑总览

| 阶段 | 状态 | 当前实现 | 仍需完成 |
| --- | --- | --- | --- |
| P0 工程基线 | 已实现 | .NET 10 solution、WinUI 3、Rust `cdylib` C ABI、x64、WAP 工程、分层测试 | 生产 CI、ARM64、长期 ABI 兼容矩阵 |
| P1 安全摄取 | 已实现 | ZIP/JAR 只读扫描、目录不可变快照、路径/reparse/ADS/大小限制、事务 journal/rollback | 大规模第三方 corpus、进程外 Archive Worker |
| P2 metadata/翻译条目 | 已实现 | Fabric/Quilt/Forge/NeoForge/legacy Forge、文件名 fallback、JSON/lang/pack.txt/已知 mcmeta | 更多 loader/version/编码 fixture |
| P3 配置/provider | 已实现 | AES-256-GCM、Credential Manager、多 source CRUD/test/switch、补偿事务、首次引导 | 真实账户/proxy/TLS/Key 生命周期验收 |
| P4 单选风格/增量/重建 | 已实现 | 每作业互斥选择 formal/informal、单字段严格 JSON/占位符、跨风格 cache 合并复用、动态 WorkspacePath、验证后 commit | 术语审核体验、性能/大包矩阵 |
| P5 硬编码 inventory | 已实现 | Rust `ldc`/`ldc_w` 引用扫描与类型化候选 | 更广 javac/Kotlin/obfuscation corpus |
| P6 字节码改写 | 有限实现 | 精确 Mojang `Component.literal(String)` → `translatable(String)`；`en_us` fallback；本作业所选目标风格；重扫/回滚 | 不是通用重写器；真实 Minecraft/loader/version matrix 未完成 |
| P7 模型工具/MCP/CLI | 有限实现 | 三 provider native tool loop；App 项目上下文提供三个只读工具，两个写工具受当前消息一次性用户授权；全部绑定捕获的 ProjectId/模型源；独立 stdio Host 仍仅 `system.context`/`cli.propose`；`translation.start` 复用真实事务队列；UI 独立 CLI 确认；敏感参数/路径 fail-closed；Low IL restricted token/private desktop/Job | 项目工作区未跨重启持久化；非 AppContainer；网络/用户可读文件未隔离；无独立 Broker/MIC sandbox provisioning |
| P8 WinUI UX/多语言 | 已实现可用界面 | onboarding、Dashboard 模组项目、Assistant、model sources、settings、CLI confirmation；按 `ProjectId + ModelSourceId` 隔离会话；确定性模型/工具活动；Provider usage 明确完整性；zh-CN/en-US/ja-JP/fr-FR/ru-RU key 对齐；UI UX Pro Max 设计约束 | provider 上下文超限显式报错，不承诺自动压缩；完整键盘/Narrator/高对比/缩放人工验收、pseudo locale |
| P9 MSIX 发布 | 未签名 payload 已验证 | version 1.2.0.0；默认 Dev Identity、显式 Store flavor、x64 WAP、五语言 PRI、App/Rust/MCP Host、makepri 与全 payload 哈希审计 | Store 签名/时间戳、干净机正式安装升级卸载、真实购买恢复、ARM64 |

## 2. 已完成的可执行基线

### 2.1 Rust 与互操作

- `native/localesmith_core` 固定 Rust 1.97.1 MSVC，产物为 x64 `localesmith_core.dll`。
- C ABI 返回有界 UTF-8 JSON；.NET `LocaleSmith.NativeInterop` 通过 `[LibraryImport]` 调用并投影成类型化清单。
- Rust 测试基线 **28/28**；`fmt --check`、`clippy -D warnings` 和 release build 属于发布前验证门。

后续：加入 ABI/export snapshot、更多 fuzz/corpus；只有出现非 .NET WinRT 消费者时才评估 WinMD bridge。

### 2.2 归档事务与 `modId`

已实现：

- 文件输入只读锁；目录输入先生成不可变 ZIP 快照，并复核 metadata/SHA-256/最终 inventory。
- 拒绝 Zip Slip、绝对/UNC/ADS/device name、symlink/junction/reparse point、路径碰撞和配置的资源上限。
- loader 检测顺序：Quilt、Fabric、NeoForge、Forge/NeoForge legacy、`mcmod.info`；读不到正式 metadata 时才用规范化 JAR 文件名 fallback。
- 现代资源位于 `assets/<modid>/lang/*.json` 并使用小写 locale；旧 Forge `.lang` 同时支持。
- 每个作业只把入队时所选的正式或语气风格作为一个产物 staging、重开验证并 commit；另一风格需独立入队，作业失败不覆盖输入或已存在产物。
- Dashboard 按规范化源 artifact 路径在 `InMemoryModProjectWorkspace` 中注册或复用一个模组项目，并将真实队列 job 的目标、进度、取消、终态和产物同步为项目 task；当前项目/task 只在进程内存在，重启后不恢复。

必须继续保持的边界：重压缩不是 byte-for-byte 无损；当前只记录签名证据，不完成 `jarsigner` 密码学验证/重签；修改签名 JAR 只能阻断或输出 unsigned copy。

### 2.3 翻译、缓存和工作区

- `.json`、`.lang`、`pack.txt` 与支持 schema 的 `.mcmeta` 进入稳定 `TranslationEntry`。
- 模型响应必须符合严格 EntryId 加单个所选风格字段的 JSON contract，并通过占位符验证；响应不得夹带未请求的另一风格。普通条目按目标大小分批，超过目标的单条文本原样独占请求，不拆分、不压缩；provider 的真实上下文超限会显式失败并触发事务回滚。
- `TranslationMemoryKey` v2 包含原包身份、目标语言、作业捕获的 `ModelSourceId` 和翻译契约版本；旧 cache 安全 miss，跨包/跨 provider 不复用。
- 产物先 durable commit，再写 cache；cache 写失败只产生 warning，下次安全重译，不回滚已经验证的产物。
- 每个新作业重新加载 `WorkspacePath`，输出到 `<Workspace>/LocaleSmith.Output`；设置改变无需重启。同源同语言的并发/重复任务预留不覆盖的递增输出名；根路径、reparse hierarchy、逃逸路径和目录源内部输出被拒绝。
- Provider 实际返回的 input/output/total Token usage 会跨分块聚合，并贯穿翻译结果、流水线、队列、Dashboard 与项目 task；失败/取消保留已完成轮次，在途或未返回 usage 的调用显式标记不完整/不可用，绝不按字符或条目估算。

后续：性能/大量条目基准、正式术语库治理、cache 迁移/清理 UI。

### 2.4 配置和凭据

- 首次引导持久化 UI language、target/style、WorkspacePath、SandboxPath、provider source 和完成状态。
- 非秘密配置用 AES-256-GCM；随机 256-bit master key 在 Windows Credential Manager。
- 云 source 各有独立 Key reference；保存/替换/切换 Ollama/删除把 credential 变更与配置提交视为一个补偿事务。提交失败恢复旧 credential，补偿失败返回聚合错误，临时 `char[]` 最终清零。
- source 和项目选择器动态切换；助手以 `ProjectId + ModelSourceId` 为键保存独立会话与草稿。切换时取消进行中请求并恢复对应键，既不删除其他会话，也不把旧 provider 或另一模组历史混入新请求。
- 助手不对用户消息/历史设置固定字符或条数上限，不修剪旧 turn；对应键的完整会话交给选中 provider，同时保留机器上下文、HTTP 响应及工具编排的安全包络。项目会话当前不跨应用重启持久化。

后续：真实 provider/代理/证书错误/限流/Key 轮换与恢复的人工矩阵。

## 3. 有限实现：硬编码字符串

### 3.1 当前精确 adapter

只处理结构解析证明的：

```text
ldc/ldc_w "literal"
invokestatic net/minecraft/network/chat/Component.literal
             (Ljava/lang/String;)Lnet/minecraft/network/chat/MutableComponent;
```

额外条件：没有 branch/switch/exception-table target 进入调用；constant pool、opcode、class size 和选择指纹都通过限制。重写保持指令长度并改成精确 `translatable(String)` 引用，追加新的 constant-pool 链。

每个选中 literal：

1. 生成稳定 `assets/<modid>/lang/en_us.json` 原文 fallback；
2. 作为普通条目进入增量模型翻译；
3. 只写入本作业所选风格的一套目标 locale；另一风格通过独立作业生成并复用缓存；
4. staging 中重新扫描 class、key、fallback 和目标条目；
5. 任一步失败整作业回滚。

目标 locale 为 `en_us`、未知/过大/损坏 class、stale selection、常量池上限、不同 descriptor、字符串拼接、Kotlin/Mixin/混淆/其他 sink 均不改写。

### 3.2 进入更广支持前的 Gate

- 为每个 Minecraft/loader/mapping/version adapter 建立 positive/negative corpus。
- 真实 `java -Xverify:all`、对应 loader 启动和游戏内文本语义回归。
- 多 mod JAR owner 归属审核；占位符/参数化 `translatable` 支持。
- 任何新 adapter 仍必须 fail closed，不能把扫描候选数描述为成功外部化数。

在这些 Gate 完成前，产品只能宣称“支持精确 `Component.literal(String)` 安全子集”，不能宣称自动外部化所有硬编码 UI 文本。

## 4. 有限实现：provider 工具、MCP 与 CLI

### 4.1 已实现工具循环

- OpenAI-compatible：`tools` / `tool_calls`；
- Anthropic Messages：`tools` / `input_schema` / `tool_use` / `tool_result`；
- Ollama：`tools` / `tool_calls`。

`ModelToolOrchestrator` 默认最多 8 轮、总计 32 次调用，校验重复 call id/未知工具，限制输出并把异常收敛为类型。它还产生不含内容的模型轮次、工具活动和运行终态事件；事件不携带消息、参数、结果、路径、命令、异常文本或私有 `reasoning_content`。基础模型工具为：

- `system_context`：安全化机器/Shell/allowlisted environment；
- `cli_propose`：校验和返回命令提议，不执行、不签发 token。

App 组合根具有 project backend。选中模组项目时暴露三个只读工具；写工具需要用户对当前消息给出一次性项目变更授权：

- `project_get_active` / `project.get_active`：读取当前活动项目与不透明 ID；
- `archive_inspect` / `archive.inspect`：只读检查该项目登记的源归档；
- `translation_start` / `translation.start`：启动真实 inspect/extract/translate/repack/verify/commit 事务流水线，不建立平行模拟流程；
- `task_status` / `task.status`：读取真实 task 的阶段、进度、产物与可用 usage；
- `task_cancel` / `task.cancel`：通过真实队列句柄取消并沿既有回滚路径收敛。

这些项目工具绑定本轮捕获的 `ProjectId` 和助手模型源，只接受项目/任务的不透明 GUID，不接受任意主机路径；源 artifact 仍不可变，只有验证通过的独立输出才会提交。独立 `LocaleSmith.McpHost` 没有 App backend，所以 stdio 目录仍只有 `system.context` 与 `cli.propose`。

`cli.execute` 不在 MCP stdio 或 provider bridge 中。助手把提议转成 WinUI `CliConfirmationDialog`，逐条要求勾选风险并最终确认。Provider-reported usage 随助手最终响应返回；Provider 未报告或仅部分报告时显示不完整/不可用，不做估算。DeepSeek、MiMo、GLM 与 Kimi 的 `reasoning_content`，以及 MiniMax `reasoning_split` 产生的结构化 `reasoning_details`，只在同一 Provider 工具循环内作为私有协议状态回放，不进入活动 UI或跨 Provider 发送。

### 4.2 已实现 CLI 控制

- 动态 executable allowlist；默认只发现 Program Files 下无 reparse point 的 `dotnet.exe`。
- 高风险 interpreter/LOLBins 默认拒绝；禁止 shell chaining、redirection、substitution、环境展开和 sandbox 外绝对路径。
- 绝对 blacklist 包含用户要求的 `::`、任何 `Format`、`rd /s /q`、`del /f /s`、`> nul`，并扩展到 recursive-force PowerShell、encoded command、动态执行/提权工具。
- WorkingDirectory 只允许 `%TEMP%` 或配置 Sandbox 子树；30 秒上限；elevated Host fail closed；approval token 绑定命令且单次使用；每次结果写 JSONL。
- Windows `/Windows/...` drive-root-relative path 会在 option 判断前按 rooted path 处理；relative junction escape、NUL/malformed path 和 canonicalization error 全部 fail closed。
- 参数含 `api-key`/`api_key`/`apikey`、`token`、`secret`、`password` 或 `credential` 标记时，在生成 approval 前拒绝。
- 允许执行的尝试必须先写 `Started` JSONL；审计不可用则 launcher 不运行。`Started` 与 completed/failed/timed-out terminal record 使用同一 correlation id。
- Windows launcher 使用 `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE | LUA_TOKEN)`、Low IL `S-1-16-4096`、私有 desktop、受控环境/stdio。
- 子进程 suspended 启动；Job 设定进程数/内存/CPU/UI/kill-on-close 后先 `AssignProcessToJobObject`，再 `ResumeThread`；失败不回退 unrestricted execution。

### 4.3 进入更强安全声明前的 Gate

当前实现不是 AppContainer，不能声称阻止用户 ACL 可读文件或网络；WorkingDirectory 是策略范围，不是 capability sandbox；Sandbox 是否可由 Low IL 写入取决于 MIC label。

若要发布“文件/网络隔离”的 CLI，必须增加并验证：

- AppContainer/LPAC、Win32 App Isolation 或等价独立 broker；
- per-job DACL/MIC provisioning、网络拒绝 canary、用户文件/credential/registry canary；
- 安全的 broker identity/IPC、Host crash cleanup 和 production compatibility matrix；
- typed tool 参数语义审核，而不仅是字符串 blacklist。

在此之前，UI 和文档必须持续显示“Low IL + policy + Job，不是 AppContainer”。

## 5. WinUI 3 与本地化

已实现页面：首次引导、Dashboard/队列、Assistant、模型源、设置和 CLI 确认。XAML 使用 key 对齐的 `zh-CN`/`en-US` `.resw`；业务命令由 CommunityToolkit.Mvvm 提供，View 不直接调用 Rust、HTTP 或 Credential Manager。Dashboard 的单个源 artifact 映射为一个进程内模组项目并同步助手；助手支持项目/source selector、按项目和 provider 隔离的发送/取消/清空、确定性模型/工具活动、Provider usage 完整性提示和 CLI proposal 独立确认。

视觉和交互基线来自 `design-system/LocaleSmith` 的 UI UX Pro Max 产出。该设计依据不替代人工验收。

发布前 UX Gate：

- 无鼠标完成 onboarding、导入、翻译、取消和 CLI 拒绝/确认；
- Narrator 名称/角色/状态、错误焦点和实时区域；
- 100%/200% 缩放、窄窗口、长 CJK/英文、高对比度；
- pseudo localization 和至少一次人工五语言 manifest/PRI 检查。

## 6. MSIX 与发布路线

当前 WAP source version 为 `1.2.0.0`：x64、`Windows.FullTrustApplication`、App + Rust DLL + MCP Host、五语言 PRI、四个开发 PNG。App/MCP Host 都是 self-contained、非 single-file、非 trimming 的 `win-x64` publish；不另需 .NET 10 Desktop Runtime，但仍依赖 manifest 声明的 Windows App Runtime 2.x 与 VCLibs。WAP 不在 `LocaleSmith.slnx`，必须按 `packaging/README.md` 单独 restore RID、`Rebuild`、解包审计和签名。

默认 `PackageFlavor=Development` 使用 `CRTech.LocaleSmith.Dev`，只有显式 `Store` 才选择 Partner Center Identity。正式 PFN 独占 `%LOCALAPPDATA%\LocaleSmith` / `LocaleSmith` credentials；unpackaged/Dev 使用 `%LOCALAPPDATA%\LocaleSmith.Dev` / `LocaleSmith.Dev`，且不读取生产/旧版凭据。该边界用于防止开发 schema 再次使已安装旧 Store 版无法启动。

完整重命名改变了 package payload、PRI、可执行文件和 Rust DLL 名称；此前的开发包大小、哈希、registration 与 AppsFolder 启动结果不再代表当前源码。重命名后的 MSIX 必须重新构建、解包检查、签名并执行安装/启动 smoke。

当前 Dev manifest Publisher 为 `CN=LocaleSmith Development`，未签名验证包不携带证书。未来侧载证书 Subject 必须精确匹配该值；历史 SignPath 身份与当前 Dev/Store manifest 不兼容，不能继续使用。仓库不记录个人证书 Subject、邮箱、thumbprint 或私钥材料。

生产 Gate：

- 生产 Publisher/证书 Subject 匹配，可信链和 timestamp；签名材料不入仓库/普通 runner；
- 干净 x64 Windows 的安装、首次启动、升级、修复、卸载；
- Rust DLL、MCP Host、五语言 PRI 和运行时依赖在最终 payload 验证；
- SBOM、license、secret scan、NuGet/Cargo 漏洞门；
- ARM64 只有在 .NET/Rust/MCP 对应 RID 及同等验证完成后才加入。

## 7. 当前验证门

最终交付至少重跑：

```powershell
cargo fmt --manifest-path native/localesmith_core/Cargo.toml --all -- --check
cargo clippy --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets --all-features -- -D warnings
cargo test --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets
cargo build --manifest-path native/localesmith_core/Cargo.toml --locked --release
dotnet build LocaleSmith.slnx -c Release --no-restore
dotnet test LocaleSmith.slnx -c Release --no-build --no-restore
dotnet format LocaleSmith.slnx --verify-no-changes --no-restore
dotnet list LocaleSmith.slnx package --vulnerable --include-transitive
```

本文当前源码基线：Rust **28/28**；.NET Release **855/855**（Core 84、Application 58、Presentation 178、App 216、Archive 73、Infrastructure 222、MCP 20、NativeInterop 4），build 0 warnings/0 errors，`dotnet format` clean。App 五种语言 `.resw` 各 676 个 key。当前源码复审未发现仍存在的高/中优先级问题；这不是外部 penetration test，也不消除已列 residual risk。MSIX 另按 `packaging/README.md` 验证，不包含在 solution test count 中。

## 8. 不承诺事项

- 不承诺任意 JAR 的全部硬编码文本都能自动外部化。
- 不承诺修改签名 JAR 后维持原作者签名，也不声称当前已实现重签。
- 不承诺重打包后整个 ZIP/JAR byte-for-byte 相同。
- 不把 Low IL restricted token/private desktop/Job 描述成 AppContainer、无网络或不可读用户文件。
- 不把 WorkingDirectory/Sandbox 参数校验描述为操作系统文件 capability。
- 不让模型自行执行、批准或跳过 CLI UI 确认。
- 不把进程内模组项目或项目会话描述为已跨重启持久化。
- 不展示私有 `reasoning_content`，也不估算 Provider 未报告或未完整报告的 Token usage。
- 不把开发自签 MSIX 描述成生产发布件。

## 9. 相关文档

- [当前架构](architecture.md)
- [安全威胁模型](security-threat-model.md)
- [验证记录](verification.md)
- [MSIX 打包](../packaging/README.md)
