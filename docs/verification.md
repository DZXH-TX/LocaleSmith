# LocaleSmith verification record

本文记录 **2026-08-25（Asia/Shanghai，Windows x64）** 对下一版源码与未签名打包候选实际执行的验证。当前公开正式版仍是 Microsoft Store / GitHub Release `v1.1.0`；下列 `1.2.0.0` MSIX 不是已发布或已签名版本。

## 源码状态

- 联合功能基线：`02c34a4`（已进入 `origin/main`，当时远端 Build and Test 与 CodeQL 成功）。
- 打包与状态隔离补强：`c2a6f7d`。
- 最终二进制源码提交：`2d087426fc5c9c31206bb0de0798800ce298c22e`。
- App FileVersion `1.2.0.0`，ProductVersion `1.2.0+2d087426…`。
- MCP Host FileVersion `0.1.1.0`，ProductVersion `0.1.1+2d087426…`。

## 自动化验证

| 范围 | 命令 | 结果 |
| --- | --- | --- |
| .NET format | `dotnet format LocaleSmith.slnx --verify-no-changes --no-restore` | 通过，clean |
| .NET Release build | `dotnet build LocaleSmith.slnx -c Release --no-restore` | 通过，0 warnings、0 errors |
| .NET tests | `dotnet test LocaleSmith.slnx -c Release --no-build --no-restore -m:1` | **855/855** |
| NuGet vulnerability | `dotnet list LocaleSmith.slnx package --vulnerable --include-transitive` | 18 个源码/测试项目均未发现当前源已知易受攻击包 |
| Rust format | `cargo fmt --manifest-path native/localesmith_core/Cargo.toml --all -- --check` | 通过 |
| Rust lint | `cargo clippy --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets --all-features -- -D warnings` | 通过 |
| Rust tests | `cargo test --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets` | **28/28**：16 unit、12 integration |
| Rust Release | `cargo build --manifest-path native/localesmith_core/Cargo.toml --locked --release` | 成功生成 x64 `localesmith_core.dll` |
| 五语言资源 | 解析并比较 `.resw` name set | `zh-CN` / `en-US` / `ja-JP` / `fr-FR` / `ru-RU` 各 **676** keys，完全一致 |

.NET 测试分布：Core 84、Application 58、Presentation 178、App 216、Archive 73、Infrastructure 222、MCP 20、NativeInterop 4。

Rust/MSVC 在本机可能把“正在创建库”显示为 `linker_messages` warning，并出现增量缓存目录无法 finalize 的提示；`clippy -D warnings`、测试与 Release 产物均成功，不能把原始 Cargo 输出描述成完全无提示。

## 关键回归覆盖

- Dashboard 与助手共享真实 TaskId/JobId 状态；完成、失败、取消、retry、旧失败卡保留和 echo 去重。
- assistant 正文保持不可变，独立实时任务卡无需再次模型调用；模型末轮失败/取消不删除已启动任务。
- `mods` 多归档容器在入队前给出明确提示；合法展开目录仍走不可变 ZIP snapshot，MCP 不接受任意主机路径。
- 单次输出 Token 预算 256–65,536、分批字符目标 1,000–100,000；预算入队冻结，固定 16 MiB 响应字节上限不可关闭。
- DeepSeek、Xiaomi MiMo、智谱 GLM、Kimi 的 `reasoning_content` 同源私下回放；MiniMax `reasoning_split=true` 与结构化 `reasoning_details` 回放；私有推理不进入 UI或跨 Provider。
- 模型列表显式刷新 `/models`，API Key 使用后清零/清空输入；失败保留手填模型名。
- constant-pool 集合级容量规划：窄 `ldc` 无法容纳的新候选安全跳过，其余候选和普通资源继续；不做不完整的 `ldc_w`/分支/StackMap 重写。
- 对本机 PowerGrid JAR 的纯内存演练：1,037 classes、52 个安全候选、23 个容量候选跳过、0 次重写失败。
- schema 仅接受 1..4，未知未来/非正版本在字段归一化前拒绝且不保存。
- 只有正式 PFN `CRTech.LocaleSmith_pxtspj1qm7b2r` 使用 production 配置/凭据；unpackaged/Dev 隔离默认路径、Credential prefix、translation memory、audit 与安全锁，并禁用 legacy secret migration。

## MCP Host 0.1.1

执行：

```powershell
dotnet pack src/LocaleSmith.McpHost/LocaleSmith.McpHost.csproj `
  -c Release --no-restore `
  -p:Version=0.1.1 -p:PackageVersion=0.1.1

.\.github\scripts\Test-McpToolPackage.ps1 `
  -PackageVersion 0.1.1 ...
```

结果：

- `CRTech.LocaleSmith.McpHost.0.1.1.nupkg`
- 433,456 bytes
- SHA-256 `7DCFCA13D53CDB0382437057496F6B8C9C3B32237B38684D0D0C553679069346`
- 本地工具安装与 initialize/tools smoke 通过；服务版本 `0.1.1`
- 独立 Host 仍只有 `system.context`、`cli.propose`

远端发布必须由指向上述源码提交、且已可从 `main` 到达的 `mcp-v0.1.1` tag 触发；本地包验证本身不能替代 Publish GitHub Package workflow 与 Packages 页面回读。

## 未签名 MSIX 1.2.0.0

WAP 构建顺序：Rust Release → App RID restore → MCP Host RID restore → WAP `/t:Rebuild`。一次在 NuGet 漏洞扫描的无 RID restore 后直接运行 WAP，按预期触发 `NETSDK1047`；重新紧邻执行两个 `-r win-x64` restore 后成功。这证明不能把普通 solution restore 当成 WAP RID restore，也不能在 WAP 命令上追加 `/restore`。

最终产物位于 `artifacts/msix/pr12-2d087426/`：

| Flavor | Identity | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| Development | `CRTech.LocaleSmith.Dev` | 104,041,801 | `FEB127CAC070C25063E113B14357DCE6728CB0BF08A2A6CD4B00C6D82EA476B2` |
| Store candidate | `CRTech.LocaleSmith` | 104,041,820 | `6BDAB472D853031AB347F90003501BF377DE7BAFC42649C9A0CB4C4E8BEB4AA0` |

`.github/scripts/Test-MsixPackage.ps1` 对两种 flavor 均通过：

- version `1.2.0.0`、x64、各自 Name/Publisher 正确；
- `AppxSignature.p7x` 不存在，Authenticode 状态 `NotSigned`；
- root `resources.pri` 分别为 340,152 / 340,136 bytes；`makepri dump` 找到 App、MainWindow、2 Controls、Dialog、7 Pages 与 Theme 共 13 个 XBF；
- App、Application、Archive、Core、Infrastructure、Mcp、NativeInterop、Presentation、Rust DLL 与 MCP Host 的文件集合和 SHA-256 与当前 publish 输入一致；
- 两种包的 App DLL SHA-256 均为 `EA59A0B3413A06A54FD70BF3FBB097A7EDE40C66BF71827D85F4B55CB952F5C8`；
- MCP Host EXE SHA-256 均为 `E54B5BFF3FB85EE03441A01A6DE3C7CA229B713C41F2C074453CBA2B5CA89F2F`；
- 四个 PNG、Windows App Runtime/VCLibs 依赖存在，payload 无证书或私钥材料。

## Development MSIX 启动 smoke

- 使用 loose layout 注册 `CRTech.LocaleSmith.Dev_1.2.0.0_x64__4rqmcnsyrpbqt`；未替换 Store `CRTech.LocaleSmith 1.1.0.0`。
- AUMID：`CRTech.LocaleSmith.Dev_4rqmcnsyrpbqt!App`。
- AppsFolder 启动后的进程来自最终 Development MSIX 的独立解包目录，窗口标题“译匠”，响应正常，ProductVersion 为 `1.2.0+2d087426…`。
- production `%LOCALAPPDATA%\LocaleSmith\settings.localesmithcfg` 的时间戳与 SHA-256 在 smoke 前后不变。
- Windows 对 registered desktop package 的 AppData 写入会执行 MSIX virtualization；Dev 逻辑根 `%LOCALAPPDATA%\LocaleSmith.Dev` 的物理文件位于 Dev PFN 的 `LocalCache\Local\LocaleSmith.Dev`，与 production 隔离。未声明 `unvirtualizedResources`，避免为测试包扩大 restricted capability。

## 尚未证明

- Store `1.2.0.0` 的 Microsoft 签名、可信时间戳、实际升级与 Partner Center 认证；
- 干净 x64 Windows 的安装、首次启动、修复、卸载与依赖获取；
- 真实 Microsoft Store 购买/试用/续费/退款/跨设备恢复与后端 entitlement；
- 真实 DeepSeek/MiMo/MiniMax/GLM/Kimi/OpenAI/Anthropic/Ollama 端到端调用；
- 大规模第三方模组 corpus、真实 Minecraft/Loader 启动矩阵与游戏内语义；
- x86/ARM64；
- 外部渗透测试、SBOM/可重现构建与受保护生产签名环境；
- 完整 Narrator、键盘、高对比度、缩放和 pseudo-locale 人工验收。

结论：当前源码、MCP 0.1.1 包与两种未签名 MSIX payload 可进入 PR/远端 CI；不得把这些证据描述为 1.2.0 已正式发布。
