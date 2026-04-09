---
name: windbg-bridge
description: Guide for working with a live WinDbg session through this repository's named-pipe bridge. Use this when asked to inspect, diagnose, or run debugger commands through the WinDbg bridge instead of typing directly in WinDbg.
argument-hint: [pipe path] [debugging goal]
license: MIT
---

# WinDbg Bridge

Use this skill when a task should be performed through the WinDbg bridge exposed by this repository's WinDbg extension and CLI.

## When to use this skill

Use this skill when you need to:

- inspect a live WinDbg session through the bridge
- run one or more WinDbg commands from an agent
- read recent debugger history without flooding context
- fetch the output of a specific prior debugger command

Do not use this skill for ordinary repository editing tasks that do not require a live WinDbg session.

## Bridge workflow

1. Make sure the WinDbg extension is installed and the user has started the bridge from the WinDbg tool window.
2. Obtain the pipe path from the WinDbg bridge panel.
3. Use the published CLI from this repository to talk to the pipe:
   - `artifacts\publish\WinDbgBridge.Cli\Release\WinDbgBridge.Cli.exe`
4. Start with `status` to confirm the bridge is running and the pipe is correct.
5. Use `history` for lightweight command discovery.
6. Use `output` to retrieve the captured output for a specific history id, optionally capped with `--max-chars`.
7. Use `execute` to send exactly one WinDbg command per CLI invocation.

If the published CLI is missing or stale, run `install.ps1` from the repository root to publish the extension and CLI artifacts.

## Commands

### Status

Use this first to confirm the bridge is live.

```powershell
artifacts\publish\WinDbgBridge.Cli\Release\WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 status
```

### History

`history` returns lightweight metadata only:

- `id`
- `source`
- `command`
- `thread` when available

It intentionally does **not** include full command output.

```powershell
artifacts\publish\WinDbgBridge.Cli\Release\WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 history --count 20
```

### Output

Use `output` when you need the text for a specific history entry. Prefer `--max-chars` unless you truly need the full output.

```powershell
artifacts\publish\WinDbgBridge.Cli\Release\WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 output --id 42 --max-chars 4000
```

### Execute

`execute` sends one WinDbg command. Agent-triggered commands are submitted as typed commands so they should appear in WinDbg like normal user input.

```powershell
artifacts\publish\WinDbgBridge.Cli\Release\WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 execute "!clrstack"
```

## Important behaviors

- If WinDbg is not attached to a target, `execute` can fail with: `Invalid request for current state (NoSession)`.
- History is sourced from WinDbg command and output events, so it includes both user commands and agent commands.
- Thread decoration is derived from the prompt prefix embedded in captured output, for example `0:001>` from `0:001> k`.

## Recommended agent behavior

1. Call `status` before assuming the bridge is available.
2. Prefer `history` plus `output --id ... --max-chars ...` over repeatedly executing broad commands just to rediscover prior output.
3. Keep output requests bounded when exploring large dumps or verbose commands.
4. Treat `NoSession` as a debugger-state issue, not a transport failure.
5. If history already contains the command you need, prefer reading its output over rerunning it.
