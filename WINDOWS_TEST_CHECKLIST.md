# Windows 11 Verification Checklist

This checklist verifies the Claude Code usage viewer as a standard Windows 11 user. It covers deterministic historical-cache calculations, cumulative current-day transcript calculations, read-only behavior, file replacement and append behavior, accessibility, display scaling, and installation without administrator rights.

## Test artifacts

The checked-in fixtures are deliberately synthetic and contain no personal usage data:

- `tests\fixtures\stats-cache-basic.json`: current-shape deterministic calculations and a partially computed latest day.
- `tests\fixtures\stats-cache-empty.json`: valid cache with no usage.
- `tests\fixtures\stats-cache-v1-minimal.json`: older cache with newer optional fields absent.
- `tests\fixtures\stats-cache-future-schema.json`: unknown schema version, unknown fields, an unknown model, and a missing calendar day.
- `tests\fixtures\current-day-projects`: a synthetic Claude `projects` tree with a main session, nested subagent transcript, streamed same-ID assistant records, one malformed line, and a session spanning two UTC dates.

Run calculation tests against a file selected with the app's file picker. Do not replace a real `%USERPROFILE%\.claude\stats-cache.json`.

For deterministic current-day tests, inject or otherwise supply `2026-08-12` as the collector's UTC `YYYY-MM-DD` date. Do not change the Windows system clock. For a manual test performed on another date, use an isolated copy and mechanically replace `2026-08-12` with the current UTC date and `2026-08-11` with the preceding UTC date.

## Deterministic expected results

### Cumulative current day from live transcripts

Treat `tests\fixtures\current-day-projects` as the effective Claude Code `projects` directory and set the collector day to `2026-08-12`. The reference-compatible behavior is:

- Discover main transcripts directly below each project directory.
- Discover subagents at `<project>\<session-id>\subagents\agent-*.jsonl`.
- Bucket each included transcript entry's message, tool-call, and token usage by that entry's own UTC `YYYY-MM-DD` date.
- Assign only session count and start-hour to the transcript's first included entry date.
- A file can therefore contribute current-day messages, tools, and tokens even when its session began on the preceding UTC date; it does not contribute a current-day session in that case.
- Sum all four non-negative numeric fields from every valid assistant `message.usage`: `input_tokens`, `output_tokens`, `cache_creation_input_tokens`, and `cache_read_input_tokens`.
- Count every `tool_use` block in assistant `message.content`.
- Let subagent files contribute token and tool-call totals, but not separate session or message totals.
- Ignore `toolUseResult.usage`, `toolUseResult.totalTokens`, and `toolUseResult.totalToolUseCount`; the nested subagent transcript is the token/tool source and counting its parent summary would double-count it.
- Skip a malformed JSONL line and continue parsing later complete lines.

Expected current-day totals:

| Source | Input | Output | Cache creation | Cache read | I/O tokens | All processed tokens | Tool calls | Sessions | Main messages |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Main transcript | 18 | 28 | 44 | 59 | 46 | 149 | 4 | 1 | 6 |
| Nested subagent transcript | 5 | 6 | 7 | 8 | 11 | 26 | 2 | 0 | 0 |
| Overnight transcript's August 12 entry | 1,000 | 2,000 | 3,000 | 4,000 | 3,000 | 10,000 | 1 | 0 | 1 |
| **Current-day total** | **1,023** | **2,034** | **3,051** | **4,067** | **3,057** | **10,175** | **7** | **1** | **7** |

`I/O tokens` means input plus output. `All processed tokens` means input plus output plus cache creation plus cache read. The primary cumulative daily presentation must make these scopes unmistakable; 3,057 must not be labeled as total processed usage, and 10,175 must not be labeled as I/O only.

Expected current-day model breakdown:

| Model | Input | Output | Cache creation | Cache read | I/O tokens | All processed tokens | Tool calls |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `claude-opus-4-1-20250805` | 1,016 | 2,025 | 3,040 | 4,054 | 3,041 | 10,135 | 4 |
| `claude-sonnet-4-20250514` | 2 | 3 | 4 | 5 | 5 | 14 | 1 |
| `claude-haiku-3-5-20241022` | 5 | 6 | 7 | 8 | 11 | 26 | 2 |

The two Opus entries with `message.id = msg_stream_1` and `requestId = req_stream_1` deliberately resemble successive streamed/cumulative records. The observed Claude Code stats aggregation processes each valid assistant transcript line independently and does not apply an identity-based deduplication step. Therefore both records and both repeated `tool_use` blocks are included in the expected values above. This fixture is not authorization to invent a custom deduplication rule; changing that behavior requires an explicit, separately verified format decision.

The main fixture contains one truncated JSON line immediately before the final Sonnet record. Assertions:

- [ ] The malformed line contributes nothing and does not prevent the following Sonnet usage from being counted.
- [ ] The parent `toolUseResult` summary contributes nothing; Haiku usage and its two tools are counted exactly once from `agent-fixture-subagent.jsonl`.
- [ ] The same-ID streamed pair contributes input 6, output 5, cache creation 10, cache read 14, I/O 11, all processed 35, and 2 tool calls.
- [ ] All three fixture files match the August 12 scan because each has at least one included entry on that UTC date.
- [ ] The overnight fixture's August 12 assistant entry contributes exactly 1,000 input, 2,000 output, 3,000 cache-creation, 4,000 cache-read tokens, one tool call, and one main-message count to August 12.
- [ ] The overnight fixture contributes no August 12 session count or start-hour because that session's first included entry is on August 11.
- [ ] Refreshing without any file change is idempotent and keeps 1,023/2,034/3,051/4,067, 3,057 I/O, 10,175 all processed, 7 tool calls, 1 session, and 7 main messages.

These figures describe locally recorded transcript usage, not authoritative plan quota, billing, remaining allowance, or a five-hour reset window.

### Current-day transcript privacy

Transcript files are plaintext and can contain prompts, assistant text, thinking, source code, filesystem paths, tool arguments, tool results, and secrets. The fixture embeds obvious `PRIVATE_*_CANARY_*` values in each of those content locations.

- [ ] Parse and retain only aggregation metadata: file classification, `type`, timestamp/date, `isSidechain`, model, the four numeric usage fields, and content-block type needed to count `tool_use`.
- [ ] Prompt text, assistant text/thinking, tool names/inputs, tool-result content, `toolUseResult.content`, `cwd`, branch, UUIDs, request IDs, message IDs, and session/agent IDs are never retained after the current aggregation pass.
- [ ] No `PRIVATE_*_CANARY_*` value appears in any application window, accessible name, tooltip, chart/table export, clipboard payload, preferences file, diagnostic log, crash report, or telemetry/network payload.
- [ ] The UI displays aggregate counts and model identifiers only; it never offers transcript search, prompt previews, project names, or content drill-down.
- [ ] Parser errors identify a file only to the minimum extent required for recovery and never echo the malformed line or adjacent transcript content.
- [ ] Current-day scanning is read-only and does not change access time where avoidable, modification time, attributes, contents, or directory structure.

### Basic fixture: all dates and models

Given `stats-cache-basic.json`, select the entire `2026-08-01` through `2026-08-03` range and all models.

| Result | Expected |
| --- | ---: |
| All processed tokens | 460 |
| Messages | 15 |
| Sessions | 4 |
| Tool calls | 19 |
| Active dates | 3 |
| Opus all processed tokens | 300 |
| Sonnet all processed tokens | 120 |
| Haiku all processed tokens | 40 |

This fixture declares `dailyModelTokensVersion: 5`. Its `dailyModelTokens` values therefore include input, output, cache-creation, and cache-read tokens and must be labeled “all processed tokens” (or an equally explicit term). They must not be labeled “I/O tokens.” Version 1–4 fixtures retain the legacy input-plus-output meaning.

The all-time detailed model data is deliberately different in scale. If the app displays it, expected cache-read tokens are 1,200 Opus, 700 Sonnet, and 80 Haiku; expected cache-creation tokens are 400, 200, and 20. These values must be labeled all-time and must not change with the date range.

### Basic fixture: date and model filters

Select the inclusive `2026-08-02` through `2026-08-03` range.

| Model selection | All processed tokens | Messages | Sessions | Tool calls |
| --- | ---: | ---: | ---: | ---: |
| All | 310 | 11 | 3 | 13 |
| Opus only | 200 | 11 | 3 | 13 |
| Sonnet only | 70 | 11 | 3 | 13 |
| Haiku only | 40 | 11 | 3 | 13 |
| Opus + Haiku | 240 | 11 | 3 | 13 |

Messages, sessions, and tool calls are date-filtered but all-model because the cache does not attribute activity to a model. When any model filter is active, the UI must not imply that those three activity values were model-filtered.

Additional assertions:

- [ ] Both date boundaries are inclusive.
- [ ] Selecting only `2026-08-02` produces 230 all processed tokens, 8 messages, 2 sessions, and 12 tool calls.
- [ ] Selecting only `2026-08-03` and Haiku produces 10 all processed tokens, 3 messages, 1 session, and 1 tool call.
- [ ] Reversed dates are prevented or produce an inline validation message without changing the last valid result.
- [ ] Reset restores the documented default range and all models.
- [ ] Exact values remain available in a table or tooltip even when the main display abbreviates large values.
- [ ] Model order remains stable after refresh and filter changes.
- [ ] The raw model identifier remains discoverable even if the UI supplies a friendly model name.

`lastComputedDate` is `2026-08-02`, while the fixture also contains an August 3 entry. The UI should identify August 3 as potentially partial or otherwise make the cache freshness boundary visible; it must not present `lastComputedDate` as the file modification time.

### Empty, old, future, and sparse fixtures

- [ ] `stats-cache-empty.json` shows a valid empty-data state, not a missing-file or invalid-JSON error.
- [ ] `stats-cache-v1-minimal.json` renders 25 legacy I/O tokens, 2 messages, 1 session, and 3 tool calls despite missing modern optional fields.
- [ ] Missing optional cost, context-window, speculation, or web-search fields render as unavailable rather than fabricated zero values unless zero is semantically established by the schema.
- [ ] `stats-cache-future-schema.json` produces a visible compatibility warning but renders validated known fields best-effort.
- [ ] For the future fixture (`dailyModelTokensVersion: 5`), all dates produce 110 all processed tokens, 6 messages, 3 sessions, and 7 tool calls.
- [ ] The unknown `claude-future-9-20990101` model appears automatically and contributes 100 all processed tokens.
- [ ] Unknown top-level, daily, token, and model-usage fields do not crash or displace known data.
- [ ] August 2 is displayed as a zero day when a continuous August 1–4 chart or table range is rendered.
- [ ] `2099-08-01` remains August 1 in every Windows time zone; parsing `YYYY-MM-DD` must not shift a calendar date through UTC/local conversion.

## Source discovery and error handling

- [ ] With no saved override and no `CLAUDE_CONFIG_DIR`, the app resolves `%USERPROFILE%\.claude\stats-cache.json`.
- [ ] When `CLAUDE_CONFIG_DIR` is set before the app launches, the app resolves `%CLAUDE_CONFIG_DIR%\stats-cache.json`.
- [ ] Current-day discovery uses `%USERPROFILE%\.claude\projects` by default and `%CLAUDE_CONFIG_DIR%\projects` when the override is set.
- [ ] Current-day discovery includes direct project `*.jsonl` files and nested `<session>\subagents\agent-*.jsonl` files, but not arbitrary JSONL elsewhere on the machine.
- [ ] A user-selected file takes precedence and remains selected according to the documented preference behavior.
- [ ] The effective source path is visible and available to assistive technology without overflowing the window.
- [ ] A missing file gives a concise explanation plus Choose cache and Refresh now actions.
- [ ] An empty file and malformed JSON give a recoverable invalid-data state and do not close the app.
- [ ] Wrong JSON types, negative counts, and non-finite or overflowing values are rejected or isolated without producing misleading totals.
- [ ] A zero-byte-to-valid transition recovers automatically or through Refresh now.
- [ ] A valid-to-invalid refresh keeps the last-known-good dashboard visibly marked stale rather than replacing it with zero usage.
- [ ] Unknown fields and model names do not require an application update.
- [ ] A single unreadable transcript, disappearing file, malformed line, or partially appended line does not discard totals from other readable transcripts.

## Current-day append and watcher verification

Use a copy of `tests\fixtures\current-day-projects`; never point destructive test commands at the user's real Claude directory.

- [ ] Appending a complete assistant record dated on the current UTC day updates all four token categories and its `tool_use` count exactly once, regardless of when that session began.
- [ ] A partial trailing JSON line does not crash the app, clear last-known-good totals, or expose partial content.
- [ ] Completing that trailing line causes it to contribute on the next stable read.
- [ ] Creating `subagents\agent-*.jsonl` after the parent session is already open adds the subagent's token and tool usage without adding a session or main-message count.
- [ ] Appending an entry dated outside the current UTC day does not change current-day message, tool, or token totals.
- [ ] A burst of main and subagent appends settles on the exact on-disk aggregate without a reload loop or sustained CPU usage.
- [ ] Refreshing during a write either reads a consistent snapshot or retains last-known-good totals until a consistent pass succeeds.
- [ ] File handles are short-lived and do not prevent Claude Code from appending, atomically replacing, renaming, or cleaning up transcripts.

## Read-only and file-watcher verification

Use an isolated scratch copy. In PowerShell from the repository root:

```powershell
$FixtureRoot = Join-Path (Get-Location) 'tests\fixtures'
$ScratchRoot = Join-Path $env:TEMP 'claude-usage-viewer-watch-test'
New-Item -ItemType Directory -Force -Path $ScratchRoot | Out-Null
$WatchedFile = Join-Path $ScratchRoot 'stats-cache.json'
Copy-Item (Join-Path $FixtureRoot 'stats-cache-basic.json') $WatchedFile -Force
$HashBefore = (Get-FileHash $WatchedFile -Algorithm SHA256).Hash
```

Select `$WatchedFile` in the app.

### Direct rewrite

```powershell
Copy-Item (Join-Path $FixtureRoot 'stats-cache-empty.json') $WatchedFile -Force
```

- [ ] The app changes to the valid empty-data state once.
- [ ] It does not require restart or show repeated notifications.

### Atomic replacement, matching Claude Code's safe-write pattern

```powershell
$Replacement = Join-Path $ScratchRoot 'stats-cache.replacement.json'
Copy-Item (Join-Path $FixtureRoot 'stats-cache-basic.json') $Replacement -Force
Move-Item $Replacement $WatchedFile -Force
```

- [ ] The watcher survives replacement of the original file identity and reloads the basic fixture.
- [ ] The app does not retain a stale handle that prevents the move.

### Transient invalid content followed by valid content

```powershell
Set-Content -Path $WatchedFile -Value '{"version":3,"dailyActivity":[' -Encoding UTF8
Copy-Item (Join-Path $FixtureRoot 'stats-cache-basic.json') $WatchedFile -Force
```

- [ ] Debounce/retry behavior prevents a crash and avoids a misleading zero-data flash.
- [ ] The last-known-good dashboard is retained until a complete valid file is available.

### Burst changes

```powershell
1..10 | ForEach-Object {
  Copy-Item (Join-Path $FixtureRoot 'stats-cache-empty.json') $WatchedFile -Force
  Copy-Item (Join-Path $FixtureRoot 'stats-cache-basic.json') $WatchedFile -Force
}
```

- [ ] The app settles on the final basic fixture without a reload loop, duplicate windows, or sustained CPU use.

### Delete and recreate

```powershell
Remove-Item $WatchedFile
Copy-Item (Join-Path $FixtureRoot 'stats-cache-basic.json') $WatchedFile
```

- [ ] Deletion produces a non-destructive source-missing or stale state.
- [ ] Recreation is detected without reselecting the path.

### Read-only proof

After the viewer has loaded and refreshed the basic fixture several times:

```powershell
$HashAfter = (Get-FileHash $WatchedFile -Algorithm SHA256).Hash
$HashBefore = (Get-FileHash (Join-Path $FixtureRoot 'stats-cache-basic.json') -Algorithm SHA256).Hash
$HashBefore -eq $HashAfter
```

- [ ] The command prints `True`.
- [ ] App use does not change the file modification time.
- [ ] The app never creates temporary files beside the cache.
- [ ] The app reads only the selected stats cache plus usage metadata from in-scope `projects` transcripts; it does not read `history.jsonl`, credentials, memories, tool-result sidecars, file-history snapshots, or unrelated Claude files.

Remove only the isolated scratch directory when finished:

```powershell
Remove-Item -Recurse -Force $ScratchRoot
```

## No-admin installation and portability

Perform these checks while signed in to a Windows 11 standard-user account. Do not approve an elevation prompt.

- [ ] The portable archive extracts into Downloads, Desktop, or another user-writable folder.
- [ ] The portable executable starts directly with no installer and no UAC prompt.
- [ ] Any installer is explicitly per-user, requests no elevation, and installs under `%LOCALAPPDATA%\Programs\...`.
- [ ] Installation works while offline and does not require Node.js, Python, a developer SDK, or a separately installed application runtime.
- [ ] The executable manifest requests `asInvoker`, not `highestAvailable` or `requireAdministrator`.
- [ ] The app creates no Windows service, scheduled task, machine-wide environment variable, or `HKLM` registry key.
- [ ] Start-menu and uninstall entries, if provided, are user-scoped.
- [ ] A second standard user cannot see the first user's saved source path or preferences.
- [ ] Uninstalling/removing the app does not alter `%USERPROFILE%\.claude` or any selected cache file.
- [ ] Preferences remain under the current user's profile. They may contain the user-selected cache path and refresh interval, but contain no usage values, transcript content, model totals, session IDs, or credentials. The app creates no diagnostic log.
- [ ] The app remains functional with network access disabled and makes no telemetry, update-check, font, CDN, or analytics requests.
- [ ] Windows SmartScreen reputation messaging, if present for an unsigned download, is recorded separately from UAC/admin behavior.

## Keyboard and assistive-technology checks

Use only the keyboard for the first pass, then repeat key screens with Windows Narrator.

- [ ] Tab order follows visual reading order: source controls, date controls, model controls, reset, summary, charts/tables.
- [ ] Every interactive control has a visible focus indicator.
- [ ] Date presets, date fields, model selection, refresh, Choose cache, reset, and table sorting work without a mouse.
- [ ] Focus is never trapped in a chart, popup, multi-select, or date picker; Escape closes transient UI.
- [ ] Changing filters does not unexpectedly move focus to the top of the window.
- [ ] Narrator announces each control's name, role, value, state, and validation error.
- [ ] Refresh, stale, missing-file, invalid-file, and filtered-empty status changes are announced without repeatedly interrupting the user.
- [ ] Charts have meaningful accessible names and the same values are available in an accessible table.
- [ ] Model identity and selected state are not communicated by color alone.
- [ ] Tooltips are not the only way to obtain exact numbers or explanatory text.
- [ ] Normal text meets 4.5:1 contrast; large text and graphical control boundaries meet 3:1.
- [ ] Windows High Contrast themes preserve text, focus, selection, chart-series distinction, and error/status visibility.
- [ ] At Windows Text size 200%, content reflows or scrolls without clipping controls or values.
- [ ] The chart uses no nonessential animation.

## DPI, resizing, and multi-monitor checks

Repeat the main dashboard, model picker, error states, and tables at 100%, 125%, 150%, and 200% Windows display scaling.

- [ ] Text and icons remain sharp; no bitmap-blurred controls appear.
- [ ] KPI values, legends, axis labels, tooltips, menus, and dialogs do not overlap or clip.
- [ ] Long model IDs and long paths truncate or wrap safely and expose their complete value accessibly.
- [ ] At 1366×768 and 150% scaling, all filters remain reachable by reflow or scrolling.
- [ ] At 1920×1080 and 100% scaling, the dashboard does not stretch tables or charts into unreadably wide layouts.
- [ ] At the minimum supported window size, resizing does not place controls outside the client area.
- [ ] Maximizing, restoring, and reopening the app preserve a visible on-screen window.
- [ ] Moving the window between monitors with different scaling recalculates layout without clipping, stale hit targets, or a restart.
- [ ] Menus and tooltips stay on the active monitor and within its work area.
- [ ] Light mode and Windows High Contrast themes remain legible.

## Performance and soak checks

- [ ] A typical cache reaches an interactive dashboard within two seconds on a representative Windows 11 machine after warm launch.
- [ ] Idle CPU returns near zero after initial rendering and after watcher bursts.
- [ ] Idle memory remains within the project's documented lightweight budget.
- [ ] Repeating filter changes and refreshes for ten minutes does not continually increase memory or file handles.
- [ ] A large but valid synthetic cache does not block window painting; unsupported size is reported safely rather than crashing.
- [ ] Leaving the app open while Claude Code updates the cache for at least one hour produces correct final values and no recurring error dialog.

## Pass criteria

A release candidate passes only when all applicable boxes above are checked on native Windows 11 under a standard-user account. Record the Windows build, CPU architecture, app version, package type, display scaling, and any skipped cases with the reason. A Windows compatibility layer can supplement these checks but does not replace the native no-admin, file-watcher, accessibility, and DPI passes.
