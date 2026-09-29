---
name: bettergi-tasks
description: Discover and run installed BetterGI scripts, configuration groups, and one-dragon routines through the bettergi CLI, including durable run tracking and cancellation. Use for requests to perform tasks with BetterGI on Windows.
---

# BetterGI tasks

Use the installed `bettergi` CLI to operate the user's existing BetterGI tasks. This skill is self-contained and can be copied into another agent's skill directory. The CLI and a matching BetterGI build are separate prerequisites.

## Connect and discover

- Use the user's intended installation directory, containing `BetterGI.exe`, with `--install <directory>` or the `BGI_INSTALL_DIR` environment variable. Do not guess between multiple installations. `--session <id>` selects a Windows session; omit it for the current session.
- Run `bettergi doctor --install <directory>` and `bettergi schema` to inspect connectivity and the installed contract. Commands write a single camelCase JSON value to stdout. Parse JSON even when the process exits nonzero.
- The host must have been launched with `BetterGI.exe --enable-cli`. It still requires its usual administrator privileges. The CLI never starts or elevates the host. An already-running host must exit and restart to enable the bridge; older binaries do not gain support just by adding the flag. Do not interrupt an active task to restart the host without authorization.
- Before submitting a new run, inspect doctor's `data.ready`, `data.busy`, and `data.reason`. Readiness requires a running BetterGI WPF window, the capture service already enabled by the user in BetterGI's start page, initialized task context, and a still-valid game window. The CLI does not start the game/capture service or open startup dialogs. If not ready, explain the manual preparation indicated by `reason`; do not keep submitting. Existing runs can still be queried, waited on, or cancelled when execution readiness is false.
- Run `bettergi tasks list [--kind script|group|one-dragon] --install <directory>`, then `bettergi tasks show <kind> <exactName> --install <directory>` for the chosen task. The responses contain `data.tasks` and `data.task` respectively. Check `canRun` and `unavailableReason`. Use the exact discovered `name` and `kind`; a script's name is its directory name, which can differ from `details.displayName`. Do not substitute a similarly named task when lookup fails.
- Scripts, configuration groups, and one-dragon routines execute using the user's local BetterGI configuration. Discovery does not establish that a task is appropriate for the user's requested objective. Inspect its details and resolve material ambiguity before submitting.
- `canRun` covers catalog checks, not every execution prerequisite. Before a one-dragon run starts task actions, its enabled custom group references must resolve to `User/ScriptGroup/<name>.json` with an exactly matching internal group name; invalid/missing/mismatched references fail the run. Read `data.result.reportedErrors` and report the configuration issue instead of falling back to another group or silently editing it.

## Submit once and track the same run

For each newly authorized execution, generate one GUID and preserve it with the installation directory, Windows session, task kind, exact name, and parameters. The GUID is both the execution's `data.requestId` and `data.runId`. Top-level `requestId` identifies the individual RPC and can differ for get/wait/cancel; never replace the saved run ID with that value.

```powershell
$install = 'C:\Games\BetterGI'
$runId = [Guid]::NewGuid().ToString()
bettergi run script 'Exact installed script name' --request-id $runId --install $install
bettergi runs get $runId --install $install
bettergi runs wait $runId --timeout 300 --install $install
```

- Follow the user's existing authorization; the skill does not require repeated confirmation of an already authorized task. It does not authorize installing scripts, editing configuration, switching accounts, or executing additional tasks beyond that request.
- Only `script` accepts `--parameters <JSON_FILE>`. Write a UTF-8 JSON object using the keys exposed by the script; the serialized object must not exceed 16 KiB. Use an absolute file path. `tasks show` exposes `details.settingsFile`, not a complete parameter schema; when parameters are needed, read that script's existing settings definition beneath `<install>/User/JsScript/<exactName>` and its documentation. If unspecified, the script receives an empty settings object and uses its own defaults. For `group` and `one-dragon`, use their existing local settings; do not silently rewrite them.
- `run ... --wait` submits and waits. For long tasks, separate submission from `runs wait` so the saved GUID remains available if the agent is interrupted.
- A successful command response (`ok`) means the request was handled, not that the game task succeeded. Inspect `data.state`: `accepted`, `running`, and `cancelRequested` are nonterminal; `succeeded`, `failed`, `cancelled`, and `interrupted` are terminal. Run errors are in `data.error`; request errors are in top-level `error`. Available progress messages are in `data.logs`.
- If submission disconnects or times out, first query `runs get` using the same GUID. Do not generate a replacement GUID to retry an uncertain submission. If resubmission is needed, retain the original GUID and identical task and parameters. A conflicting payload with the same GUID is an error.
- A `runs wait` timeout stops waiting only; the run continues. Resume querying or waiting for the same run. Do not report timeout as cancellation or task failure.
- Use `bettergi runs cancel <GUID> --install <directory>` when the user cancels that run, then wait/query until terminal. `cancelRequested` alone does not prove that execution or input has stopped. Never cancel a different UI or CLI task to make room for a run.
- After a host interruption, inspect the recorded state of the original run. An `interrupted` run is not automatically resumed. A fresh execution requires a deliberate decision within the user's authorization, particularly if the previous run may have already performed game actions.

The default command timeout is 30 seconds; `--timeout` accepts 0.05 to 86400 seconds. Ordinary `run`, `runs get`, and `runs cancel` exit 0 when the request succeeds, even if the returned run has failed. `run --wait` and `runs wait` exit 0 for success, 5 for task failure, 6 for cancellation, 7 for timeout, and 8 for interruption. Other exit codes are 2 for invalid arguments, 3 for connection/protocol problems, and 4 for a rejected request. Ctrl+C only stops the client; it does not send cancellation.

## Report observable results

Report the selected task, run ID, final state, and any returned error or incomplete waiting. `succeeded` means the existing BetterGI workflow returned without a reported error; it is not independent verification of rewards, inventory, or other game goals. Use further game-state evidence when claiming those outcomes.

Inspect `data.result.executedCount`, `skippedCount`, and `reportedErrors` when present. Skipped work does not count as executed. If the workflow returns without another reported error but every task was skipped or no work ran, it fails with `data.error.code=no_tasks_executed`. Do not describe that result as successful completion or automatically bypass the user's skip rules. Execution counts also do not independently prove game outcomes.

If the host is unavailable, report the connection/setup issue rather than falling back to blind UI input. The bridge rejects one-dragon configurations with end actions such as shutting down Windows, closing BetterGI, or closing the game. Do not edit those actions merely to bypass the rejection; use a suitable existing configuration or make an authorized configuration change.

Run records live under `<install>/User/Automation/session-<id>`. Do not delete them to resolve an error: they preserve request deduplication. At 4096 records, or if history is corrupt/unreadable, the host stops accepting new runs. Report the maintenance issue instead of bypassing it. Returned logs contain at most the latest 100 entries, each truncated to 1000 characters; they are not a complete execution transcript.
