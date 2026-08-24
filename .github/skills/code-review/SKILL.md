---
name: code-review
description: Review LocaleSmith pull requests and diffs for correctness, security-boundary regressions, architecture violations, and missing tests. Use for every code review and pull request review task in this repository.
---

# LocaleSmith code review

Review changes as an independent verifier. Treat the diff as an entry point, then inspect affected callers, tests, contracts, and documentation before deciding whether an issue is real. Do not modify code unless the user explicitly asks for a fix.

Read `.github/SECURITY.md`, the Security Boundaries section of `README.md`, and `.github/workflows/build-and-test.yml` when the change touches their domains. Prefer current source and tests over stale numeric claims in documentation.

## Review workflow

1. Determine the intended behavior from the pull request, linked issue, tests, and surrounding code.
2. Inspect the complete diff plus enough unchanged code to verify control flow, data flow, ownership, cancellation, error handling, and compatibility.
3. Check whether each changed behavior has a focused regression test and whether existing validation covers the relevant failure path.
4. Report only issues introduced or exposed by the change that are reproducible, actionable, and significant to correctness, security, reliability, or maintainability.

## Repository-specific checks

### Untrusted input and transactions

- Treat JAR, ZIP, directory content, model output, paths, provider responses, tool arguments, and CLI arguments as hostile.
- Reject path traversal, rooted or device paths, alternate data streams, case-colliding entries, symlink/junction/reparse traversal, TOCTOU races, malformed metadata, and resource-limit bypasses.
- The original input must never become a write target. Verify staging, validation, atomic commit, cancellation, and rollback on every failure path.
- Never imply that a rebuilt archive is byte-for-byte identical or that an altered signed JAR retains its original signature.

### Credentials, models, MCP, and CLI

- Do not expose secrets, credential material, private paths, full provider bodies, user archives, translated content, or private model reasoning in logs, UI, errors, tools, or tests.
- Preserve Credential Manager and encrypted-configuration boundaries, compensation behavior, redaction, buffer clearing, and cross-provider/session isolation.
- Models and MCP tools may read only explicitly bounded context or propose actions. They must not authorize execution, accept arbitrary host paths, mint approvals, or expose a `cli.execute` capability.
- CLI execution must retain policy revalidation, sensitive-argument rejection, a command-bound single-use approval, pre-launch auditing, explicit user confirmation, and fail-closed restricted-process setup. Do not describe Low IL as AppContainer or network isolation.

### Architecture and platform code

- Keep WinUI presentation and event handling in the App layer, testable state and commands in Presentation, orchestration in Application, external integrations in Infrastructure, and managed C ABI projection in NativeInterop.
- For C#, check nullable flow, async cancellation, UI-thread access, event unsubscription, disposal, deterministic ordering, and exception redaction.
- For Rust and FFI, check integer and allocation bounds, parser progress, panic containment, `unsafe` invariants, ABI layout, buffer ownership, and paired release functions.
- For localization changes, require matching keys in `zh-CN`, `en-US`, `ja-JP`, `fr-FR`, and `ru-RU`. For packaging changes, verify manifest identity, supported architecture, payload inclusion, signing assumptions, and version consistency.

## Validation expectations

Use the workflow file as the source of truth for CI commands. Relevant changes normally require Rust formatting, Clippy with warnings denied, Rust tests, .NET formatting, Release build, and .NET tests. CLI restricted-process changes also require local interactive-Windows coverage for tests excluded from hosted runners.

Do not claim a command passed unless review evidence shows it was run. A passing test suite does not excuse missing coverage for a newly introduced failure mode.

## Findings

- Attach each finding to the smallest useful changed line range.
- State the concrete trigger, observable impact, and why existing checks do not prevent it. Include a concise fix direction when useful.
- Use `P0` for release-blocking catastrophic impact, `P1` for high-impact correctness or security failures, `P2` for ordinary actionable defects, and `P3` only for low-impact issues worth fixing.
- Avoid praise, restating the diff, speculative warnings, style-only preferences, and issues that predate the change without being worsened by it.
- If there are no actionable findings, say so and mention any material validation gap without inventing a defect.
