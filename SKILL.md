---
name: windbg-bridge
description: Connect to a live WinDbg session via a named-pipe bridge. Execute commands, read output, and see the user's command history. Use when the user says the bridge is enabled, or ask them to enable it if you need to run WinDbg commands.
argument-hint: '[pipe path] [debugging goal]'
license: MIT
---

# WinDbg Bridge

This skill connects you to a live WinDbg debugging session through a named-pipe bridge. The bridge is a WinDbg UI extension that the user activates from inside WinDbg.

With the bridge active, you can:
- **Execute WinDbg commands** — they appear in the WinDbg UI so the user sees what you're doing
- **Read the user's command history** — see what the user ran manually, including output
- **Collaborate in real time** — you and the user share the same debugging session

## Bridge workflow

1. Make sure the WinDbg extension is installed and the user has started the bridge from the WinDbg tool window.
2. Obtain the pipe path from the WinDbg bridge panel.
3. Use the CLI to talk to the pipe:
   - `E:\git\windbg-bridge\artifacts\publish\WinDbgBridge.Cli\Release\WinDbgBridge.Cli.exe`
4. Start with `status` to confirm the bridge is running and the pipe is correct.
5. Use `history` for lightweight command discovery.
6. Use `output` to retrieve the captured output for a specific history id, optionally capped with `--max-chars`.
7. Use `execute` to send exactly one WinDbg command per CLI invocation.
8. The CLI waits indefinitely unless you pass `--timeout <seconds>`.

## Commands

### Status

Use this first to confirm the bridge is live.

```powershell
WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 status
```

### History

`history` returns lightweight metadata only:

- `id`
- `source`
- `command`
- `thread` when available

It intentionally does **not** include full command output.

```powershell
WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 history --count 20
```

### Output

Use `output` when you need the text for a specific history entry. Prefer `--max-chars` unless you truly need the full output.

```powershell
WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 output --id 42 --max-chars 4000
```

### Execute

`execute` sends one WinDbg command. Agent-triggered commands are submitted as typed commands so they should appear in WinDbg like normal user input.

```powershell
WinDbgBridge.Cli.exe --pipe \\.\pipe\windbg-bridge-123 execute "!clrstack"
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
