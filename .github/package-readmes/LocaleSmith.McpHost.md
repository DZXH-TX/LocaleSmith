# LocaleSmith MCP Host

`CRTech.LocaleSmith.McpHost` is the Windows x64 stdio MCP companion for [LocaleSmith | 译匠](https://github.com/DZXH-TX/LocaleSmith). It exposes bounded, safety-gated local context to MCP clients without granting them command-execution authority.

Version 0.1.1 synchronizes protocol validation and shared security hardening with LocaleSmith 1.2.0. App-only project tools such as project inspection and translation-task control are deliberately not included in this standalone package because they require the desktop application's user-selected project workspace and UI authorization.

## Requirements

- Windows x64;
- .NET 10 SDK to install or update the tool; the corresponding .NET 10 runtime is sufficient to execute an already installed tool;
- a GitHub account and a classic personal access token with `read:packages`, because GitHub's NuGet registry requires authentication even for public packages.

Configure the `https://nuget.pkg.github.com/DZXH-TX/index.json` source by following GitHub's [NuGet registry authentication instructions](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry#installing-a-package). Keep the token in your user-level NuGet configuration or another approved secret store; never commit it to a repository.

## Install

```powershell
dotnet tool install --global CRTech.LocaleSmith.McpHost `
  --source https://nuget.pkg.github.com/DZXH-TX/index.json
```

Update or remove the tool with:

```powershell
dotnet tool update --global CRTech.LocaleSmith.McpHost `
  --source https://nuget.pkg.github.com/DZXH-TX/index.json

dotnet tool uninstall --global CRTech.LocaleSmith.McpHost
```

## MCP client configuration

After installation, configure an stdio server using the installed command:

```json
{
  "mcpServers": {
    "localesmith": {
      "type": "stdio",
      "command": "localesmith-mcp",
      "args": []
    }
  }
}
```

Adapt the surrounding keys to your MCP client. The process reserves stdout for newline-delimited JSON-RPC frames and writes diagnostics to stderr.

## Security boundary

The standalone host exposes only:

- `system.context`: bounded, allowlisted terminal and Windows context;
- `cli.propose`: validation and summary of a proposed command.

It does not expose `cli.execute`, execute processes, or issue approval tokens. A proposal never implies user approval. LocaleSmith's desktop application keeps execution behind independent policy revalidation, a command-bound single-use approval, and explicit UI confirmation. See the repository [security policy](https://github.com/DZXH-TX/LocaleSmith/security/policy) for reporting and supported-version details.

The host does not accept an arbitrary project, task, file, or directory path. File and translation project operations belong to the desktop application and remain bound to opaque IDs from user-selected projects.

This package is licensed under [Apache License 2.0](https://github.com/DZXH-TX/LocaleSmith/blob/main/LICENSE).
