<!-- markdownlint-disable MD013 MD033 MD041 -->

<div align="center">
  <img
    src="./packaging/LocaleSmith.Package/Assets/Square150x150Logo.png"
    width="132"
    alt="LocaleSmith logo"
  />

  <h1>LocaleSmith | 译匠</h1>

  <p><a href="./README.md">简体中文</a> · <strong>English</strong></p>

  <p><strong>A native Windows AI localization workbench for Minecraft: Java Edition content</strong></p>
  <p>Safely scan mods and resource packs, connect to local or cloud models, and generate localized artifacts through a verifiable, rollback-capable pipeline.</p>

  <p>
    <a href="./LICENSE"><img alt="Apache License 2.0" src="https://img.shields.io/badge/License-Apache%202.0-D22128?style=flat-square&logo=apache&logoColor=white" /></a>
    <a href="./global.json"><img alt=".NET 10.0" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" /></a>
    <a href="./rust-toolchain.toml"><img alt="Rust 1.97.1" src="https://img.shields.io/badge/Rust-1.97.1-000000?style=flat-square&logo=rust&logoColor=white" /></a>
    <img alt="Windows 10 1809+" src="https://img.shields.io/badge/Windows-10%201809%2B-0078D4?style=flat-square" />
    <img alt="WinUI 3" src="https://img.shields.io/badge/UI-WinUI%203-0078D4?style=flat-square" />
    <a href="https://apps.microsoft.com/detail/9NP8V6WQNGT0"><img alt="Microsoft Store v1.1.0" src="https://img.shields.io/badge/Microsoft%20Store-v1.1.0-0078D4?style=flat-square&logo=microsoft&logoColor=white" /></a>
  </p>

  <p>
    <img alt="Fabric" src="https://img.shields.io/badge/Loader-Fabric-8B7355?style=flat-square" />
    <img alt="NeoForge" src="https://img.shields.io/badge/Loader-NeoForge-D9634C?style=flat-square" />
    <img alt="Quilt" src="https://img.shields.io/badge/Loader-Quilt-6B57A5?style=flat-square" />
    <img alt="Forge" src="https://img.shields.io/badge/Loader-Forge-F16436?style=flat-square" />
    <img alt="Legacy Forge" src="https://img.shields.io/badge/Loader-Legacy%20Forge-6B7280?style=flat-square" />
  </p>

  <p>
    <a href="https://apps.microsoft.com/detail/9NP8V6WQNGT0"><strong>Get it free from Microsoft Store</strong></a>
    ·
    <a href="https://github.com/DZXH-TX/LocaleSmith/releases/tag/v1.1.0">GitHub Release v1.1.0</a>
  </p>

  <p>
    <a href="#project-overview">Project Overview</a> ·
    <a href="#core-capabilities">Core Capabilities</a> ·
    <a href="#supported-scope">Supported Scope</a> ·
    <a href="#processing-pipeline">Processing Pipeline</a> ·
    <a href="#quick-start">Quick Start</a> ·
    <a href="#security-boundaries">Security Boundaries</a> ·
    <a href="#contributing">Contributing</a>
  </p>
</div>

> [!IMPORTANT]
> **LocaleSmith v1.1.0 is officially available from [Microsoft Store](https://apps.microsoft.com/detail/9NP8V6WQNGT0).** Installing from the Store is recommended so that dependencies and future updates are handled automatically; the [GitHub Release](https://github.com/DZXH-TX/LocaleSmith/releases/tag/v1.1.0) also provides the production MSIX signed by Microsoft Marketplace, with no development test certificate required.

## Project Overview

**LocaleSmith | 译匠** is designed for Minecraft: Java Edition mods, resource packs, and shader packs, integrating **secure scanning, incremental translation, structural validation, and transactional rebuilding** into a native Windows desktop workbench.

The project combines a native Rust scanning core with a .NET 10 / WinUI 3 desktop application. Rust handles parsing for JARs, ZIPs, Loader metadata, and supported bytecode patterns, while .NET handles archive transactions, model integration, secure storage, translation queues, and the user interface.

## Core Capabilities

| Capability | Description |
| --- | --- |
| **Secure archive scanning** | Detects path traversal, Loader metadata, language resources, signature evidence, and supported Java string references. |
| **Incremental translation pipeline** | Reuses translations by content hash, validates placeholders and structure, and rolls back the entire job on failure. |
| **Mod project synchronization** | The Dashboard treats the same normalized source artifact as one process-local mod project and synchronizes its task objective, progress, status, and artifacts with the assistant; projects are not currently persisted across application restarts. |
| **Specialized prompts and terminology** | Automatically distinguishes mods, resource packs, and shader packs and applies dedicated domain prompts; Simplified Chinese jobs include a specialized terminology glossary for each content type. |
| **Multiple target languages** | Initially supports Simplified Chinese, English, Japanese, French, and Russian; the language catalog is centrally defined and can be extended. |
| **Multiple model integrations** | Provides unified support for Ollama, OpenAI-compatible Chat Completions, and Anthropic Messages. |
| **Provider presets** | Presets for DeepSeek, Qwen, Xiaomi MiMo, MiniMax, OpenAI, Doubao, Zhipu GLM, Kimi, and others fill in the endpoint, model name, and completion-token parameter. Ollama and OpenAI-compatible services that expose `/models` support explicit catalog refresh with manual fallback. Each source can also set response Tokens and the translation batch character target; providers that require cross-tool reasoning continuity replay private protocol state without displaying it in the UI. |
| **Online mod community** | Searches and paginates public mods and discussions; a PAT stored in Windows Credential Manager enables posting, replies, and reports, with direct access to the terms and community guidelines. |
| **Microsoft subscription and safe acceleration** | Uses native Microsoft Store purchase UI, authoritative MCTX backend entitlements, and one-time download grants; the default source always remains available and is used as a safe fallback. |
| **Persistent diagnostic logs** | When the log directory is writable, each translation attempts to persist a pair of Debug and All levels `.log` files through a bounded background writer; logs can be viewed from the left-hand “Logs” page, and the directory can be changed during onboarding or in Settings. |
| **Native desktop experience** | Provides first-run onboarding, a processing queue, a model assistant, model source management, logs, settings, and CLI risk confirmation. |
| **Model activity and real usage** | The assistant shows program-generated model-round and tool activity rather than private reasoning. Provider-reported token usage flows through assistant responses and translation tasks, including completed calls before failure/cancellation; missing or partial usage is marked explicitly and is never estimated. |
| **Credential and configuration protection** | Stores API keys in Windows Credential Manager and encrypts other configuration with AES-256-GCM. |
| **Controlled MCP / CLI** | The in-app assistant adds bounded project tools only when a mod project is active. The standalone stdio Host can still only read safe context and propose commands; CLI execution requires policy revalidation and explicit user confirmation. |

## Supported Scope

| Category | Current Support |
| --- | --- |
| Input | JAR, ZIP, or one extracted mod/resource-pack/shader-pack directory; container folders with multiple JAR/ZIP files must be selected through Add package |
| Loader metadata | Fabric, Forge, NeoForge, Quilt, Legacy Forge |
| Text resources | Minecraft language JSON, Legacy `.lang`, shader-pack `shaders/lang/*.lang`, `pack.txt`, and supported display text in `pack.mcmeta` |
| Bytecode | Exact `Component.literal(String)` patterns proven by structural analysis; other candidates are reported but not rewritten |
| Model APIs | Ollama, OpenAI-compatible Chat Completions, Anthropic Messages |
| Model presets | DeepSeek, Qwen, Xiaomi MiMo, MiniMax, OpenAI, Doubao, Zhipu GLM, Kimi, and a custom entry point |
| Target languages | `zh_CN`, `en_US`, `ja_JP`, `fr_FR`, `ru_RU` |
| Output | One target language and one translation style selected for the current job; resource names inside the package use lowercase Minecraft locales such as `ja_jp` |
| Platform | Windows x64, minimum Windows 10 1809 |

## Processing Pipeline

```mermaid
flowchart LR
    A["Import<br/>JAR / ZIP / Folder"] --> B["Secure scan<br/>Paths / Metadata / Resources"]
    B --> C["Extraction and planning<br/>Incremental cache"]
    C --> D["Model translation<br/>Target language + Formal / Tone"]
    D --> E["Validation and rebuilding<br/>Transactional rollback"]
    E --> F["Output<br/>LocaleSmith.Output"]
```

Each job captures the user's selected target language, model source, one translation style, response Token budget, and source-character batch target when it is queued. The character target controls batching for large packages and never truncates one value; HTTP responses remain protected by a fixed, non-configurable 16 MiB safety limit. Language resources prefer `en_us`, `en_gb`, or another existing locale that differs from the target language as source text; for example, if English is the target but the package contains only Japanese, LocaleSmith generates `en_us` from the Japanese source. The other style can be queued separately and can reuse corresponding translations already cached under the same source-text hash.

When a source artifact is added from the Dashboard, LocaleSmith registers or reuses a process-local mod project by normalized source path and synchronizes the active project and its translation tasks with the assistant. The assistant keeps a separate conversation and draft for every `ProjectId + ModelSourceId` pair. Switching projects or model sources restores only the matching session: one mod's history is not mixed into another mod, and history from one provider is not sent to a different provider. The project workspace is currently memory-only and is not restored after the application restarts.

The assistant's processing view contains only deterministic lifecycle events for model-round start/completion, tool start/completion/failure, and the final run state. Events exclude message content, tool arguments/results, paths, commands, exception text, and private `reasoning_content`. Provider-reported token usage is aggregated into assistant responses and translation tasks; completed provider rounds remain visible when a task later fails or is cancelled, while an in-flight call without usage is marked partial or unavailable. A total is shown only when the provider reports it or when both provider-reported input and output counts are present, with no character-count or heuristic estimate.

## Microsoft Store Subscription and Domestic Acceleration

LocaleSmith uses `Windows.Services.Store.StoreContext` to read the hidden parent-app-only subscription, display Microsoft purchase UI, and bind desktop modal UI to the main-window HWND. Partner Center is configured for monthly auto-renewal, an eligible new subscriber's seven-day free trial, a US$4.99/month global base tier localized by the Store, and CNY 30.00/month in China. The client UI displays only the actual renewal price returned by the Store for the current region; it does not hard-code the USD base tier for Chinese customers. Microsoft handles billing, and the subscription can be managed or cancelled under [Microsoft Services & subscriptions](https://account.microsoft.com/services); the [privacy policy](https://dow.dzxh-tx.cn/privacy) remains discoverable. Microsoft Store does not support a native “CNY 24 first month, then CNY 30” introductory price, and the client does not simulate one.

Purchase, restore, and refresh first require the existing LocaleSmith/MCTX account and a PAT with the `downloads:accelerated` scope. `Succeeded` and `AlreadyPurchased` only start `service-ticket → Store ID key → backend verify → entitlements`; they never unlock locally. Only an exact, usable `domestic_download_acceleration` backend entitlement can proceed. Missing `microsoft_store_billing_v1` / `accelerated_downloads_v1`, PAT, scope, entitlement, or fresh backend verification fails closed and hides or disables the paid entry.

Source discovery accepts only the relative default source and `additional_source` decision returned by the API. The client never hard-codes an object-storage host, bucket, object key, or long-lived URL. One-time signed GET/HEAD URLs exist briefly only in memory and their HTTPS requests; they are not written to logs, configuration, diagnostics, clipboard, toasts, telemetry, or resume sidecars. Object-storage requests carry no PAT, Cookie, Authorization, Referer, or proxy credential and do not follow redirects. The transport uses a separate HEAD request for a strong ETag, up to four Range + If-Range requests, complete re-authorization and re-signing on grant expiry, and final API size/SHA-256 verification. Authorization, storage, or integrity failure falls back safely to the existing same-origin downloader.

Local automation covers the capability/PAT/scope/entitlement refusal matrix, purchase state machine, expiry/cancellation/refund/trial end, cross-device restore, suspended accounts, stale verification, secret request bodies, separate GET/HEAD signatures, exact HTTPS origin, four-way ranges, re-sign/resume, credential-free sidecars, SHA-256, and default-source fallback. The website source contract and replica worker now use the single `domestic_download_acceleration` entitlement. Real Partner Center products, purchase/renewal/refund/cross-device restore, Microsoft recurrence/service tickets, live PostgreSQL/Redis entitlement integration, and private RainS3 E2E remain unverified, and this work did not enable or deploy production acceleration.

## Translation Logs and Persistent Settings

The “Logs” page in the left navigation lists persistent records by translation job and displays the Debug view by default; switch to All levels to inspect records across all log levels, including fine-grained progress. Logging is a best-effort background diagnostic feature: when the directory is writable and the writer has capacity, a job creates a pair of `.debug.log` / `.all.log` files and incrementally flushes them to disk. On slow devices or when the queue is full, files or individual diagnostic entries may be skipped, but translation is never blocked. After an abnormal process exit, content that was successfully flushed remains available for identifying the last recorded stage.

The production Store package defaults to `%LOCALAPPDATA%\LocaleSmith\logs\translations`; unpackaged and Dev packages use the isolated `%LOCALAPPDATA%\LocaleSmith.Dev\logs\translations` root, with separate settings, credentials, Sandbox, and security locks. During first-run onboarding and from the “Settings” page, you can browse for or manually enter a local directory. Once saved, a change takes effect with the next translation and is written to the encrypted configuration when the application closes, together with the last valid settings for language, theme, workspace, and other options. The application retains and lists only the latest 500 sessions; cleanup matches only LocaleSmith's own naming format and does not delete other files in the directory. Logs record only the task ID, package file name, stage, progress, result, and error type. They do not record API keys, full prompts, or the parent directory of a user-selected path. Common bearer, token, and API key patterns are redacted again before being written to disk.

## Quick Start

### Install the release

| Channel | Description |
| --- | --- |
| [Microsoft Store](https://apps.microsoft.com/detail/9NP8V6WQNGT0) | Recommended; get it free with installation, framework dependencies, and future updates handled by the Store. Product ID: `9NP8V6WQNGT0`. |
| [GitHub Release v1.1.0](https://github.com/DZXH-TX/LocaleSmith/releases/tag/v1.1.0) | Provides the Microsoft Marketplace-signed `CRTech.LocaleSmith_1.1.0.0_x64.Msix` for users who need a direct installer download. |

The GitHub MSIX SHA-256 is `A2F24B73D4B20C9255DE32F3A6949251067ADFC53A24A4732C50B96FBBA84F64`. The production release supports Windows x64 and requires Windows 10 1809 (build 17763) or later.

The standalone stdio MCP Host is maintained as the `CRTech.LocaleSmith.McpHost` .NET tool; the current source package version is `0.1.1`. It still exposes only `system.context` and `cli.propose`, with no App-only project or file tools. See the [package README](./.github/package-readmes/LocaleSmith.McpHost.md) for GitHub Packages authentication, installation, and client configuration.

### Development prerequisites

| Dependency | Version or Notes |
| --- | --- |
| Operating system | Windows 10 1809 or later; Windows 11 is recommended for WinUI development |
| .NET SDK | `10.0.302`, pinned by `global.json` |
| Rust | Repository toolchain `1.97.1` (MSVC), including `rustfmt` and `clippy` |
| Windows SDK | `10.0.26100`, with MSVC / C++ build tools installed |
| UI dependencies | Windows App SDK `2.3.1` and CommunityToolkit.Mvvm `8.4.2`, restored through NuGet |
| MSIX build | Requires Visual Studio Developer PowerShell with Desktop Bridge / WAP targets |

### Build from source

Build the Rust release DLL first, then restore and build the .NET solution:

```powershell
git clone https://github.com/DZXH-TX/LocaleSmith.git
Set-Location LocaleSmith

cargo build --manifest-path native/localesmith_core/Cargo.toml --release
dotnet restore LocaleSmith.slnx
dotnet build LocaleSmith.slnx -c Release
```

> [!NOTE]
> `dotnet build LocaleSmith.slnx` does not generate the WAP / MSIX package. The packaging project is located at `packaging/LocaleSmith.Package` and must be built separately in a development environment with the corresponding Visual Studio targets.

<details>
<summary><strong>Run the full validation gate</strong></summary>

```powershell
cargo fmt --manifest-path native/localesmith_core/Cargo.toml --all -- --check
cargo clippy --manifest-path native/localesmith_core/Cargo.toml --all-targets --all-features -- -D warnings
cargo test --manifest-path native/localesmith_core/Cargo.toml --all-targets

dotnet test LocaleSmith.slnx -c Release
dotnet format LocaleSmith.slnx --verify-no-changes --no-restore
```

</details>

## Source Layout

```text
native/localesmith_core/       Rust ZIP/JAR, metadata, and classfile scanning core
src/LocaleSmith.Core/           Domain models and unified service contracts
src/LocaleSmith.NativeInterop/  C ABI projection, DLL resolution, and typed manifests
src/LocaleSmith.Application/    Translation orchestration, incremental planning, queues, and transaction boundaries
src/LocaleSmith.Archive/        Secure snapshots, extraction, rebuilding, validation, and rollback
src/LocaleSmith.Infrastructure/ Model adapters, credentials, encryption, CLI, and environment detection
src/LocaleSmith.Mcp/            MCP JSON-RPC / stdio protocol and tool catalog
src/LocaleSmith.McpHost/        Standalone MCP console host
src/LocaleSmith.Presentation/   Testable MVVM ViewModels and UI contracts
src/LocaleSmith.App/            WinUI 3 views, composition root, and local application services
tests/                      Eight .NET test projects and a restricted CLI probe
packaging/                  x64 WAP / MSIX manifest, five-language resources, and icons
```

## Security Boundaries

The following principles are part of product behavior, not optional configuration:

- **Models cannot authorize command execution.** Provider tool loops may use only the safe-context, project, and command-proposal tools explicitly exposed for the current session. The user must still review the complete command, acknowledge the risk, and explicitly approve it.
- **Signed JARs produce an explicit unsigned copy by default.** The original JAR / ZIP always remains unchanged. The application translation queue removes signature blocks, `SIG-*`, and stale manifest digest claims only from an independent output; low-level callers may still choose complete blocking, and the project never impersonates the original signature or hashes.
- **The CLI does not search the process PATH.** No process executable is trusted by default; any future explicit allowlist must use approved absolute paths. The private CLI sandbox defaults to `%LOCALAPPDATA%\LocaleSmith\CliSandbox`, with reparse points checked both before and after creation.
- **The client carries no Cloudflare origin key.** `api.dzxh-tx.cn` uses ordinary server TLS validated by the Windows trust store. Authenticated Origin Pulls authenticates only the Cloudflare-to-origin connection; its certificate and private key must not enter the application, MSIX, or repository.
- **Store and download grants are secrets.** PATs, Entra service tickets, Microsoft Store ID keys, and signed GET/HEAD URLs are not logged, persisted, added to telemetry, or included in diagnostics or resume metadata. The client contains no Entra client secret, and object-storage requests never carry MCTX Authorization or cookies.
- **Low IL is not the same as AppContainer.** A restricted token, private desktop, and Job Object reduce the execution surface, but do not automatically block network access or prevent access to files permitted by the current user's ACLs.

<details>
<summary><strong>Exact scope of bytecode externalization</strong></summary>

Currently, LocaleSmith rewrites only structurally proven, immediately adjacent `ldc` / `ldc_w` strings and Mojang `Component.literal(String):MutableComponent` static calls, converting them into exact `translatable(String)` references. The implementation preserves instruction length and rescans for validation before committing. Patterns that cross branch or exception boundaries, unknown opcodes, obfuscated code, and all other inexact patterns are never rewritten. This is not a general-purpose Java bytecode rewriter, and coverage of a real-world Minecraft / Loader compatibility matrix is not yet available.

</details>

<details>
<summary><strong>Archive rebuilding and signatures</strong></summary>

The original input always remains unchanged, and every structural or behavioral change occurs only in a transactional working copy and independent output. JSON / lang / manifest content, Java classes, Loader metadata, services, and resource references are checked before an atomic commit; failures do not publish a partial artifact. ZIP / JAR files are still recompressed, so byte-for-byte preservation of streams, extra fields, comments, ordering, or Loader behavior compatibility is not guaranteed. A precompiled JAR reports only static bytecode and resource validation and is never described as “source compilation passed.” When source plus a Gradle / build entry is present, the current pipeline fails closed because no genuinely isolated build executor exists, and it never launches scripts from the archive directly.

</details>

<details>
<summary><strong>Model tool and CLI isolation</strong></summary>

The in-app assistant always retains `system.context` and `cli.propose`. Selecting a mod project adds `project.get_active`, `archive.inspect`, and `task.status`; `translation.start` and `task.cancel` are exposed only after the user checks the one-turn “allow this message to change the project” authorization. Every project tool is bound to the `ProjectId` captured for that turn, accepts only opaque project/task IDs, and never accepts an arbitrary host path. `translation.start` is also forced to use the model source selected for the assistant turn and reuses the real inspect, safe-extract, translate, repack, verify, and commit transaction pipeline. The standalone `LocaleSmith.McpHost` has no App project backend, so its stdio catalog still contains only `system.context` and `cli.propose`. No entry point exposes `cli.execute`; executable commands still require policy revalidation, a one-time confirmation token, and explicit user approval. Kimi's private `reasoning_content` is replayed within bounds only inside the same Kimi tool loop; it never enters the activity timeline or user-visible content and is never sent to another provider.

</details>

<details>
<summary><strong>MSIX package status</strong></summary>

The current public release is `v1.1.0`; its Store package version is `1.1.0.0`, its product ID is `9NP8V6WQNGT0`, and it uses the Partner Center identity `CRTech.LocaleSmith`. Microsoft Store provides the production distribution and automatic updates. The x64 MSIX attached to the GitHub Release has been verified for its Microsoft Marketplace signature chain, trusted timestamp, package identity, architecture, and SHA-256, and it does not require the self-signed test certificate used by historical development packages. The public package declares the `runFullTrust` desktop capability; commands proposed by a model still require policy revalidation and explicit user confirmation.

The current source prepares the next `1.2.0.0` package but does not present it as a public release. WAP defaults to an isolated, unsigned `CRTech.LocaleSmith.Dev` validation package; an unsigned Store-identity submission candidate is produced only with explicit `PackageFlavor=Store`. Both flavors must pass unpacking, PRI, version, and complete payload-hash audits. An unsigned package is not a Store release.

The official `CRTech.LocaleSmith` identity cannot update the earlier `LocaleSmith.Desktop` / `JaxI18n.Desktop` development packages in place, so Windows temporarily installs them side by side. Close the older application during the transition. The new application continues to use the per-user `%LOCALAPPDATA%\LocaleSmith` root and performs read-only discovery of redirected data belonging to any still-registered legacy package. Uninstall the development package only after confirming that the official-identity build works correctly.

</details>

## Validation Snapshot

The following figures are the validation baseline recorded in the current source, not live CI status:

| Check | Baseline |
| --- | --- |
| .NET Release | `854 / 854` tests, `0` warnings, `0` errors |
| Rust | `28 / 28` tests; `rustfmt` and `clippy -D warnings` passed |
| Five-language resources | `676` keys each for `zh-CN` / `en-US` / `ja-JP` / `fr-FR` / `ru-RU`, fully aligned |
| Source security audit | Regression gates for local paths, archives, CLI, credentials, and migrations passed; GitHub CodeQL results depend on a fresh remote scan of the current commit, and the README does not claim zero alerts |

These results demonstrate the source behavior covered by the current automation. They do not replace external penetration testing, real provider validation, or Minecraft / Loader runtime compatibility testing.

## Contributing

Pull requests are welcome. Before submitting code, run at least the Rust / .NET validation gates relevant to your changes, and clearly state the target Minecraft version, Loader, input type, and model source.

## License

This project is open source under the [Apache License 2.0](./LICENSE).

## Artificial Intelligence Use Statement

This project permits the use of generative artificial intelligence tools for requirements analysis, code and documentation drafting, refactoring suggestions, test design, localization, and similar activities. All AI-assisted output must undergo human review, necessary testing, and security and license verification before submission. Maintainers and contributors remain fully responsible for the correctness, security, compliance, and maintainability of their submissions; AI output does not constitute a factual, legal, or professional guarantee.

When using AI tools, do not upload secrets, credentials, personal information, unpublished source code, or restricted third-party content to unauthorized external services, and comply with the applicable terms of service and third-party licenses. Contributors should accurately disclose AI-assisted content with a material impact on the project in their pull requests. This statement does not alter the licensing, copyright, or contribution ownership established under the Apache License 2.0.

Copyright © 2026 **DZXH-TX（道泽星河-天仙）** (copyright holder and licensor).
