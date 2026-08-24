# Repository Guidelines

## Project Structure & Module Organization

LocaleSmith is a Windows x64 .NET 10/WinUI 3 app backed by Rust. `src/` is layered: `Core` contracts; `Application` orchestration; `Archive` safe JAR/ZIP transactions; `Infrastructure` providers, credentials, and CLI; `NativeInterop` C ABI; `Mcp`/`McpHost` tools; `Presentation` MVVM; and `App` WinUI. The Rust crate is `native/localesmith_core/`. Eight xUnit projects cover managed layers; `tests/LocaleSmith.CliProbe/` supports restricted-process tests. Rust integration tests are under the crate's `tests/`. `packaging/` holds MSIX assets.

## Build, Test, and Development Commands

Use the SDKs pinned by `global.json` and `rust-toolchain.toml`. Build the native DLL before the app:

```powershell
cargo build --manifest-path native/localesmith_core/Cargo.toml --locked --release
dotnet restore LocaleSmith.slnx
dotnet build LocaleSmith.slnx -c Release --no-restore
```

For UI debugging, build the native DLL, open `LocaleSmith.slnx` in Visual Studio, and start unpackaged `LocaleSmith.App`.

Run the repository validation gate before submitting:

```powershell
cargo fmt --manifest-path native/localesmith_core/Cargo.toml --all -- --check
cargo clippy --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets --all-features -- -D warnings
cargo test --manifest-path native/localesmith_core/Cargo.toml --locked --all-targets
dotnet format LocaleSmith.slnx --verify-no-changes --no-restore
dotnet test LocaleSmith.slnx -c Release
```

MSIX packaging is separate and requires Visual Studio Developer PowerShell with WAP targets.

## Coding Style & Naming Conventions

Follow `.editorconfig`; respect `.gitattributes` for line endings (Markdown/Rust/TOML/JSON use LF). Use UTF-8, a final newline, no trailing whitespace, four spaces for C#/Rust, and two for XAML/XML/JSON/YAML/TOML. Prefer file-scoped C# namespaces. Use `PascalCase` for types/members, `camelCase` for locals/parameters, and `Async` suffixes; Rust uses `snake_case`. Nullable analysis and latest-recommended analyzers are enabled, with warnings as errors.

## Testing Guidelines

Use xUnit v3 with `SubjectTests.cs`/`SubjectTests` and descriptive behavior names; use `[Theory]` for data variants. Keep Rust unit tests beside modules and integration tests in the crate's `tests/` directory. Add regression coverage for changed behavior; no numeric threshold is enforced. Local .NET tests include private-desktop CLI cases excluded by hosted CI.

## Commit & Pull Request Guidelines

History commonly uses `feat:`, `fix:`, `docs:`, `ci:`, and `refactor:`; prefer `<type>: <imperative summary>` and one concern per commit. PRs should explain scope, link issues, list validation results, and include screenshots for WinUI changes. State the target Minecraft version, Loader, input type, and model source when relevant. Disclose material AI assistance and confirm human review.

## Security & Configuration

Treat archives, model output, paths, and CLI proposals as hostile input. Never commit credentials, signing material, decrypted settings, logs, or user artifacts. Preserve explicit user approval and fail-closed behavior at security boundaries; follow `.github/SECURITY.md` for reporting.
