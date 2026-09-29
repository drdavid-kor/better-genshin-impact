---
name: bettergi-setup
description: Install, connect, and diagnose the bettergi CLI with a compatible local BetterGI build on Windows. Use for CLI setup, unavailable-host errors, installation selection, and enabling the local automation bridge.
---

# BetterGI CLI setup

Connect a packaged or locally built `bettergi` .NET tool to a matching BetterGI installation. This skill can be copied independently into another agent's skill directory; it does not depend on a repository-relative reference or another skill.

## Establish the local prerequisites

- Select the installation containing the user's intended `BetterGI.exe`. Use `--install <absolute-directory>` or set `BGI_INSTALL_DIR` in the current process. Do not change global environment variables or the user's other installations as a side effect.
- The new CLI requires the matching BetterGI host changes. An upstream or older executable cannot be controlled by this protocol. Preserve existing `User` configuration and resources when installing or rebuilding a host.
- A packaged framework-dependent CLI requires the .NET 8 runtime; building or packing requires the .NET 8 SDK. The BetterGI WPF host also requires its normal Windows desktop runtime, native dependencies, assets, and user configuration. A working CLI alone does not prove the host is ready.
- Inspect `dotnet --info` and the installed `bettergi schema` before changing dependencies. Use an existing project SDK setup when one is present. Do not install a global tool or replace a host unless that falls within the user's setup request.

## Build and install from this source tree

From the repository root, build the matching host and pack the separate CLI. These commands require the repository's normal restored dependencies and SDK configuration:

```powershell
dotnet build BetterGenshinImpact.sln -c Debug -p:Platform=x64
dotnet pack BetterGenshinImpact.Cli/BetterGenshinImpact.Cli.csproj -c Release -o ./artifacts/cli
```

Install the generated `BetterGenshinImpact.Cli` package into a chosen local tool directory; version 0.1.0 is the initial version. For subsequent versions, use the version from the actual generated package rather than substituting a public package:

```powershell
$packageSource = (Resolve-Path './artifacts/cli').Path
dotnet tool install BetterGenshinImpact.Cli --version 0.1.0 --tool-path ./artifacts/cli-tool --add-source $packageSource
& './artifacts/cli-tool/bettergi.exe' schema
```

Invoke `bettergi.exe` by absolute path outside this repository when its directory is not in PATH. Update an existing tool deliberately instead of deleting its directory.

## Enable and diagnose the bridge

1. Run `bettergi schema` to verify CLI execution and inspect the command contract.
2. Run `bettergi doctor --install <directory>`. Parse the JSON result, including on nonzero exit; distinguish an unavailable host from a connected but mismatched installation. Doctor checks the running bridge, not every native dependency or game setting. Inspect the local path separately if necessary. `doctor` can also omit the installation to identify the current user/session host before choosing it explicitly.
3. If startup is authorized, launch the matching `BetterGI.exe --enable-cli` using the existing administrator-capable launcher or terminal. The host still requires administrator privileges; the CLI neither launches nor elevates it. Windows UAC may require user interaction. An existing main instance must exit and restart with this flag; a second launch cannot enable an existing instance. Preserve active work before restarting.
4. Re-run doctor against the exact installation and, if needed, `--session <Windows-session-id>`. The default is the caller's current session. The bridge is local, scoped to the Windows user and session, and denies network clients. It is not a TCP server or remote-control endpoint; do not open firewall ports.
5. Confirm discovery using `bettergi tasks list --install <directory>`. An empty list can mean that no tasks have been installed or configured. It is not permission to download arbitrary scripts or edit the user's configuration.

`doctor` returning `ok: true` confirms that its request was handled; also inspect `data.ready`, `data.busy`, and `data.reason`. New execution requires all of the following: BetterGI's WPF window is running, the user has enabled the capture service in the GUI start page, the task context has initialized, and the original game window remains valid. The CLI does not launch the game, enable capture, or open startup dialogs. For a readiness failure, explain the manual preparation in `reason`: enable capture in BetterGI, wait for initialization, or reconnect the game and re-enable capture if its window has changed. Discovery and querying existing runs do not require readiness to start new execution.

Run history is stored beneath `<install>/User/Automation/session-<id>`. Unreadable/corrupt history or the 4096-record limit blocks new runs to preserve deduplication. Preserve those records and investigate the reported reason; do not clear them automatically. Any authorized archival must happen with the host stopped and must retain the rule that archived request IDs are never reused.

Do not start a real game task solely to test installation. If a user authorizes an execution check, discover an appropriate exact task and retain a single request GUID through submission, status, waiting, and cancellation. Do not replay an uncertain submission with a new GUID.

## Deliver useful diagnosis

Report the selected installation, CLI/package version when available, host connection result, and the narrowest unresolved step. Separate build/test evidence, protocol connectivity, task discovery, and actual game execution. A successful build or `doctor` result does not prove that a game task completed.

When handing off to an agent, copy this skill directory and/or `bettergi-tasks` into that agent's supported skill location only if requested. Keep the host and CLI paths explicit. Installing a skill does not install the CLI, enable the host, or grant additional authorization.
