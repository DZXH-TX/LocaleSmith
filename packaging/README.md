# LocaleSmith | 译匠 MSIX packaging

`src/LocaleSmith.App` 是 unpackaged WinUI 3 调试宿主；`packaging/LocaleSmith.Package` 是独立的 x64 Windows Application Packaging Project（WAP）。它不在 `LocaleSmith.slnx` 内，普通 `dotnet build` / `dotnet test` 不会生成或验证 MSIX。

当前公开正式版仍是 Microsoft Store `v1.1.0`。本目录描述的是下一版源码打包合同：App/MSIX `1.2.0.0`，独立 MCP .NET 工具 `0.1.1`。未签名构建不是正式发布件。

## 两种打包身份

WAP 使用 `PackageFlavor` 明确区分开发验证与 Store 提交：

| Flavor | Manifest | Identity | 用途 |
| --- | --- | --- | --- |
| `Development`（默认） | `Package.dev.appxmanifest` | `CRTech.LocaleSmith.Dev` | 本地/CI 未签名 payload 审计；不会覆盖 Store 包 |
| `Store`（必须显式指定） | `Package.appxmanifest` | `CRTech.LocaleSmith` | Partner Center 正式身份；未签名产物只用于提交前审计 |

默认值故意是 `Development`。任何普通 Release/CI 构建都不能在未明确 `/p:PackageFlavor=Store` 时产出正式 Identity。

应用运行时也按完整 Package Family Name 隔离状态：

- 正式 `CRTech.LocaleSmith_pxtspj1qm7b2r` 使用 `%LOCALAPPDATA%\LocaleSmith` 与 Credential Manager 前缀 `LocaleSmith`；
- unpackaged 与所有 Dev/Test Identity 使用 `%LOCALAPPDATA%\LocaleSmith.Dev` 与前缀 `LocaleSmith.Dev`；
- Dev 不读取或迁移正式/旧版配置与凭据；生产迁移仍是只读导入且不删除旧数据。

这里描述的是应用选择的逻辑 LocalAppData 根。Windows 对 registered desktop package 可能把 AppData 物理写入该 PFN 的 `LocalCache\Local`；这仍由独立 PFN 隔离。项目不声明 `unvirtualizedResources`，不会为了开发测试关闭系统虚拟化或扩大 restricted capability。unpackaged 运行则直接使用用户级 `%LOCALAPPDATA%\LocaleSmith.Dev`。

这条隔离避免新开发版 schema 改写已安装旧 Store 版的共享设置。截图中的 `Unsupported settings schema 4` 正是旧 `1.1.0.0`（schema 3）读取了开发版写入的 schema 4 所致；正式 `1.2.0.0` 支持 schema 4。

## Payload 合同

- 仅支持 Windows x64；Rust payload 为 `LocaleSmith.App\localesmith_core.dll`。
- App 与 MCP Host 均为 self-contained、非 single-file、非 trimming 的 `win-x64` publish；目标机不另需 .NET 10 Desktop Runtime。
- Manifest 仍声明 `Microsoft.WindowsAppRuntime.2 >= 2.3.1.0` 与 `Microsoft.VCLibs.140.00.UWPDesktop`；self-contained .NET 不等于 Windows App SDK self-contained。
- Full-trust 入口为 `LocaleSmith.App\LocaleSmith.App.exe`，使用 `Windows.FullTrustApplication` 与 `runFullTrust`。
- `LocaleSmith.McpHost.exe` 作为 stdio companion 进入独立 payload 子目录，不是第二个可启动 Application，也不会由 UI 自动启动。
- 五种语言为 `zh-CN`、`en-US`、`ja-JP`、`fr-FR`、`ru-RU`。
- WAP 将 WinUI XBF/MRT 合并进包根 `resources.pri`；包内 App 子目录不保留 loose `.xbf` / `LocaleSmith.App.pri`。因此不得把包内 EXE 抽出后当作 unpackaged 程序运行。
- 四个必需 PNG 为 `Square44x44Logo.png`、`Square150x150Logo.png`、`StoreLogo.png`、`Wide310x150Logo.png`；`AppIconMaster.png` 不进入 payload。

## 构建未签名包

在安装 Windows SDK、MSIX Packaging Tools 与 Desktop Bridge/WAP targets 的 Visual Studio Developer PowerShell 中执行。Rust Release DLL必须先存在；随后分别还原两个 RID 输入，再使用 WAP `Rebuild`，不能复用旧 `bin/obj` payload：

```powershell
cargo build --manifest-path .\native\localesmith_core\Cargo.toml --locked --release
dotnet restore .\src\LocaleSmith.App\LocaleSmith.App.csproj -r win-x64
dotnet restore .\src\LocaleSmith.McpHost\LocaleSmith.McpHost.csproj -r win-x64

msbuild .\packaging\LocaleSmith.Package\LocaleSmith.Package.wapproj `
  /t:Rebuild `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:PackageFlavor=Development `
  /p:AppxBundle=Never `
  /p:AppxPackageSigningEnabled=false
```

只有准备 Partner Center payload 时才把 flavor 改为：

```powershell
/p:PackageFlavor=Store
```

不要给 WAP 命令追加 `/restore`；DesktopBridge 可能把共享项目资产重新还原为无 RID 版本并触发 `NETSDK1047`。也不要把 `Build` 替代 `Rebuild`，否则可能再次封装历史 publish 目录。

## 解包审计

`.github/scripts/Test-MsixPackage.ps1` 会：

- 解包并核对 Identity、Publisher、`1.2.0.0`、x64；
- 断言 `AppxSignature.p7x` 不存在且签名状态为 `NotSigned`；
- 使用 `makepri dump` 回读根 `resources.pri`，确认 App、MainWindow、Pages、Controls、Dialog 与 Theme XBF；
- 核对 App、全部 LocaleSmith 项目程序集、Rust DLL、MCP Host、deps/runtimeconfig 与刚生成的 publish 输入逐文件 SHA-256 一致；
- 检查四个图标、Windows App SDK/VCLibs 依赖和 payload 中无证书/私钥材料。

示例：

```powershell
.\.github\scripts\Test-MsixPackage.ps1 `
  -PackagePath <path-to-msix> `
  -ExpectedAppPublishDirectory .\src\LocaleSmith.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish `
  -ExpectedMcpPublishDirectory .\src\LocaleSmith.McpHost\bin\x64\Release\net10.0\win-x64\publish
```

默认审计 Development Identity。审计 Store flavor 时必须显式传入正式 Name 与 Publisher。CI 同样只上传文件名含 `CRTech.LocaleSmith.Dev` 与 `unsigned` 的开发验证包。

## 签名与发布

- `Package.appxmanifest` 使用 Partner Center 正式 Name `CRTech.LocaleSmith`、Publisher `CN=33E83C71-5BAE-4CB2-A70A-1F0545DACFB1` 与 Publisher display name `DZXH CR Tech`。
- `Package.dev.appxmanifest` 的 Publisher 为 `CN=LocaleSmith Development`；若以后侧载签名，证书 Subject 必须精确匹配该值。
- 仓库不保存 `.pfx`、证书密码或任何私钥。Store 包由 Microsoft Store 认证后重签；旧 SignPath 自签证书不能用于正式 Store Identity。
- 未签名 Development MSIX 只证明构建与 payload，不证明安装/升级、Store 购买、订阅、干净机依赖或生产签名链。
- `CRTech.LocaleSmith.Dev` 无 Microsoft Store 关联，不能用于验证真实购买与订阅流程。

生产提交仍需复核 Partner Center Identity/品牌，完成可信签名与时间戳、首次安装、同 Identity 升级、卸载、五语言、Rust/MCP 加载、真实 Store 购买/恢复，以及干净 x64 Windows 验收。x86/ARM64 必须具备对应 .NET、MCP 与 Rust 原生产物后才能启用。

## 安全边界

MSIX 是 full-trust desktop package，不是 AppContainer。CLI 的 Low IL restricted token、private desktop、Job Object 和 Host 路径策略也不构成完整文件系统或网络隔离。模型仍不能直接执行命令；所有 CLI 执行必须经过策略复核、命令绑定的一次性 approval 与用户明确确认。

当前源码的实际测试数量、包大小、SHA-256 与 PRI 回读证据记录在 [`docs/verification.md`](../docs/verification.md)。
