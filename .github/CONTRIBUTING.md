# 参与贡献（Contributing）

感谢你帮助改进 LocaleSmith（译匠）。参与本项目即表示你同意遵守[社区行为准则](CODE_OF_CONDUCT.md)。请保持改动聚焦、证据可复现，并将归档、模型响应、路径和命令参数一律视为不可信输入。

## 选择正确的入口

- 可复现的软件错误：使用 [Bug report](https://github.com/DZXH-TX/LocaleSmith/issues/new/choose)。
- 建议与功能请求：使用 [Feature request](https://github.com/DZXH-TX/LocaleSmith/issues/new/choose)。
- 使用帮助、方案讨论或尚未成形的想法：前往 [Discussions](https://github.com/DZXH-TX/LocaleSmith/discussions)。
- 安全漏洞：不要创建公开 Issue；先阅读[安全政策](SECURITY.md)，再通过其中的私密报告入口提交。

提交前请搜索现有 Issue、Discussion 和 Pull Request，确认问题尚未被报告或解决。缺陷报告只应包含脱敏的最小复现资料；不要上传真实用户归档、完整日志、API Key、访问令牌、私人路径、签名材料或受限制的第三方内容。

## 开发环境

主要开发与验证环境是 Windows x64：

- Windows 10 1809 或更高版本；WinUI 开发推荐 Windows 11；
- `.NET SDK 10.0.302`，由根目录 `global.json` 固定；
- Rust `1.97.1` MSVC 工具链，包含 `rustfmt` 与 `clippy`；
- Windows SDK `10.0.26100`、MSVC/C++ 构建工具；
- Windows App SDK Runtime `2.3.1`。

从最新 `main` 创建短生命周期分支。先构建 Rust release DLL，再还原和构建 .NET solution：

```powershell
cargo build --manifest-path native/localesmith_core/Cargo.toml --locked --release
dotnet restore LocaleSmith.slnx
dotnet build LocaleSmith.slnx --configuration Release --no-restore
```

`dotnet build LocaleSmith.slnx` 不生成 WAP/MSIX。只有修改 `packaging/` 或发布配置时，才需要在安装了 Desktop Bridge/WAP targets 的 Visual Studio Developer PowerShell 中额外验证 `packaging/LocaleSmith.Package/LocaleSmith.Package.wapproj`，并在 Pull Request 中记录实际命令和结果。

## 实现约定

- 遵守 `.editorconfig` 和 `.gitattributes`。C# 使用 4 空格与 file-scoped namespace；YAML、JSON、TOML、XML 和 XAML 使用 2 空格；Rust 使用 4 空格。
- .NET 启用了 nullable、最新推荐分析器、确定性构建和 warnings-as-errors。不要通过降低分析级别或屏蔽警告来绕过问题。
- 保持依赖方向：WinUI View 负责呈现与输入，Presentation 保持可测试，Application 编排事务，Infrastructure 实现外部服务，NativeInterop 是托管 C ABI 的唯一入口。
- 不得原地修改用户输入。归档、缓存和输出变更必须保持现有的安全路径检查、暂存、验证、原子提交、取消与回滚边界。
- 模型和 MCP 工具不得授权命令执行、扩大到任意主机路径，或暴露凭据和 Provider 私有数据。CLI 仍需独立策略复核、命令绑定的一次性批准和用户明确确认。
- 修改 Rust FFI 时，保持 panic 不越过 ABI、所有权与释放函数配对，并为 `unsafe` 假设提供可验证依据。
- 修改界面文案时，同步 `zh-CN`、`en-US`、`ja-JP`、`fr-FR`、`ru-RU` 五套 `.resw` key。面向用户的长期文档变更应同步中文与英文 README。
- 不提交生成的构建产物、测试结果、真实用户样本、私钥、证书或秘密配置。

架构和风险边界的权威说明见 `README.md` 的“源码结构”和“安全边界”以及[安全政策](SECURITY.md)。

## 测试与格式检查

至少运行与改动相关的测试。提交前建议执行与 CI 对齐的完整验证门：

```powershell
cargo fmt --manifest-path native/localesmith_core/Cargo.toml --all -- --check
cargo clippy --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets --all-features -- -D warnings
cargo test --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets
cargo build --manifest-path native/localesmith_core/Cargo.toml --locked --release

dotnet restore LocaleSmith.slnx
dotnet format LocaleSmith.slnx --verify-no-changes --no-restore
dotnet build LocaleSmith.slnx --configuration Release --no-restore
dotnet test LocaleSmith.slnx --configuration Release --no-build --no-restore
```

新增或改变行为时，在对应的 xUnit 或 Rust 测试项目中补充回归测试。修改 CLI 审批、受限进程或私有 desktop 逻辑时，应在本地交互式 Windows 会话运行完整测试；GitHub 托管 runner 会排除依赖交互式 window station/private desktop 的少数集成测试。

如果无法运行某项检查，请在 Pull Request 中明确列出未运行的命令、原因和剩余风险，不要把未执行的检查写成已通过。

## Pull Request 要求

Pull Request 应保持单一目的，并包含：

- 变更目的、用户影响和关联 Issue；
- 主要实现选择，以及兼容性或安全边界是否变化；
- 实际运行的验证命令与结果；
- UI 变更的前后截图和键盘/高对比度等相关验收；
- 涉及 Minecraft 内容时的游戏版本、Loader、输入类型与模型来源；
- 需要维护者重点复核的已知限制、迁移或后续工作。

提交信息应简短、使用祈使语气并说明实际改动。除非维护者明确要求，不要在同一 Pull Request 中夹带无关重构、生成文件或大规模格式化。

## AI 辅助与许可

可以使用生成式 AI 辅助分析、草拟、重构或测试，但贡献者必须人工复核其正确性、安全性、许可证和可维护性。对项目有实质影响的 AI 辅助内容应在 Pull Request 中如实说明，且不得向未经授权的服务上传秘密、个人信息、未公开源码或受限制内容。

除非另有明确声明，提交到本项目的贡献将依据项目的 [Apache License 2.0](../LICENSE) 提供。
