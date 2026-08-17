# Windows 11 Verification Checklist

Manual verification of Claude Usage (Claudeometer) as a standard Windows 11 user, without
administrator rights. Automated coverage lives in `tests/ClaudeUsage.Core.Tests` and in
`ClaudeUsage.exe --self-test`; this checklist covers what only a real Windows session can show:
discovery, the window, alerts, printing, installation, and behaviour against live Claude Code data.

## What changed in 0.2

Re-verify these first — they are the behaviours that changed.

- History no longer comes from `stats-cache.json`. Current Claude Code releases do not write that
  file, which is why 0.1 showed no history. All figures now come from the session transcripts under
  `<data folder>\projects\`.
- A single model response occupies several transcript lines, each repeating the same `usage` payload.
  0.1 summed every line and overcounted tokens by roughly 2.3x on real data. 0.2 credits usage once
  per response (`message.id` + `requestId`) and still counts `tool_use` blocks from every line.
- Days are **local** calendar days. 0.1 used UTC days.
- Subagent and sidechain usage is now included. 0.1 discarded sidechain entries, losing the tokens
  spent by Task-tool subagents.
- New: multi-folder discovery and configuration, a durable daily archive, PDF export, and daily
  threshold alerts.

## Setup

- [ ] `dotnet build src/ClaudeUsage.WinForms/ClaudeUsage.WinForms.csproj --configuration Release`
      succeeds on Windows.
- [ ] `dotnet run --project tests/ClaudeUsage.Core.Tests/ClaudeUsage.Core.Tests.csproj --configuration Release`
      prints `PASS`.
- [ ] `ClaudeUsage.exe --self-test` exits with code 0 (`$LASTEXITCODE` is 0).

Create the deterministic fixture. It anchors its timestamps to your own local *today* and
*yesterday*, so the expected values below hold in any time zone without touching the system clock:

```powershell
./scripts/new-test-fixture.ps1 -Force
```

Then launch against it, so no real Claude data is involved:

```powershell
./ClaudeUsage.exe --data-dir "$env:TEMP\claude-usage-fixture\.claude"
```

- [ ] The header shows the fixture folder, and the badge reads `LIVE`.

## Deterministic expected values

The fixture contains two sessions. Session one starts yesterday at 20:00 local and continues today;
one of its responses is split across three lines with the same message id and the same usage payload.
Session two starts today at 11:00 local. A `<synthetic>` response and a `tool-results` sidecar are
included as traps.

**Today**

| Field | Expected |
| --- | ---: |
| Input | 115 |
| Output | 226 |
| Cache read | 337 |
| Cache creation | 448 |
| Processed | 1,126 |
| Input + output | 341 |
| Responses | 3 |
| Tool calls | 3 |
| Messages | 5 |
| Sessions | 1 |

**Today by model**: `claude-opus-5` processed 1,100 (2 responses), `claude-sonnet-5` processed 26
(1 response).

**Yesterday** (History tab, range `Yesterday`): processed 10,000, responses 1, tool calls 1,
messages 2, sessions 1.

**All time**: processed 11,126 — `claude-opus-5` 11,100 and `claude-sonnet-5` 26.

- [ ] Today's six cards match the table exactly. Hovering a card shows the exact number.
- [ ] Processed equals input + output + cache read + cache creation, and is labelled as such.
- [ ] **1,126 is the value shown, not 2,326.** 2,326 means per-line summation regressed: the split
      response would have been counted three times.
- [ ] Tool calls are 3, not 1. Each line of the split response contributes its own `tool_use` block.
- [ ] The `<synthetic>` response contributes no tokens, and `<synthetic>` is not listed as a model.
      Its 999,999 input tokens must appear nowhere.
- [ ] `must-not-appear-model` and its 500,000 tokens appear nowhere: the `tool-results` sidecar is
      never opened.
- [ ] Sessions today is 1, from session two. Session one counts against yesterday, even though it
      contributed most of today's tokens.
- [ ] The History tab's `All time` range totals 11,126 and lists both models on the Models tab.

## Discovery and configuration

- [ ] Launched with no arguments on a machine that uses Claude Code, the app finds
      `%USERPROFILE%\.claude` by itself and shows non-zero usage — no configuration needed.
- [ ] **Data sources…** lists each folder with how it was found and its transcript count.
- [ ] `setx CLAUDE_CONFIG_DIR "<fixture path>"`, then relaunch: the fixture folder is listed as found
      via `CLAUDE_CONFIG_DIR`. (Remove it afterwards with `setx CLAUDE_CONFIG_DIR ""`.)
- [ ] Two folders in `CLAUDE_CONFIG_DIR`, separated by `;`, are both listed and their usage is summed.
- [ ] **Add folder…** accepts the fixture folder. Selecting its `projects` subfolder by mistake is
      silently corrected to the parent.
- [ ] Adding a folder with no `projects` subfolder warns first and is accepted only on confirmation.
- [ ] **Remove** deletes a folder you added; auto-discovered rows cannot be removed.
- [ ] Clearing *Also look in the usual locations automatically* leaves only your own folders.
- [ ] With no folder found at all, the Today tab explains where the app looked and points at
      **Data sources…**. If WSL is installed, the distribution names are mentioned.
- [ ] **Search WSL…** on a machine with WSL either finds `\\wsl.localhost\<distro>\home\<user>\.claude`
      or reports that nothing was found, within about 12 seconds, without freezing the window.
- [ ] A pinned folder that has gone away (unplugged drive, disconnected network home) is marked
      `unavailable` and the app keeps working.
- [ ] Choices survive a restart.

## Date ranges

- [ ] Every preset sets sensible From/To dates: Today, Yesterday, Last 7/30/90 days, This month,
      Last month, All time.
- [ ] Editing From or To switches the preset to `Custom` and refilters immediately.
- [ ] Both bounds are inclusive: `Yesterday`–`Today` totals 11,126.
- [ ] A From date after the To date shows a message and changes no totals.
- [ ] The model filter narrows tokens, responses, and tool calls, while messages and sessions stay
      whole-day — and the notice says so.
- [ ] The chart covers the whole selected span, including zero days, and the daily table matches it.
- [ ] **Reset** returns to Last 30 days and All models.

## PDF export

- [ ] **Export PDF…** and Ctrl+E both open a Save dialog under Documents with a dated filename.
- [ ] The saved file opens in Edge, Acrobat, and Windows Photos/Print preview without a repair prompt.
- [ ] Page 1 shows the title, the range and model labels, the generation time, the local time-zone
      name, six summary tiles, the activity line, the chart, and totals by model.
- [ ] Numbers in the PDF match the window exactly for the same range and filter.
- [ ] Continuation pages repeat the daily table header, and no row is clipped by the footer.
- [ ] Every page is numbered `Page n of m` with the correct final total.
- [ ] With a threshold set, the chart shows the threshold line, days at or above it are a different
      colour, and the summary reports how many days reached it.
- [ ] The footer names the folders the data came from and states that this is not a bill.
- [ ] Exporting a range with no data produces a valid one-page-plus report that says so.
- [ ] A 90-day range from real data produces a multi-page report whose daily rows stay aligned.
- [ ] Answering **Yes** to "Open it now?" opens the file in the default PDF viewer.
- [ ] Exporting to a read-only location shows a plain error and does not crash.

## Alerts

- [ ] With no threshold set, the Today tab invites you to set one and no alert appears.
- [ ] Set the threshold to 1,000 processed tokens with warning at 80%. Today's fixture usage of 1,126
      raises a notification-area balloon within one refresh, and the Today bar reads 112%.
- [ ] The balloon appears once. Later refreshes on the same day stay quiet.
- [ ] Set the threshold to 2,000: the warning level (1,600) is not reached by 1,126, and the bar reads
      56% with no balloon.
- [ ] Set the threshold to 1,200 and warning to 80% (960): a warning-level balloon appears, worded as
      approaching rather than reached.
- [ ] Changing any alert setting re-arms alerts immediately.
- [ ] Switching the metric to *Input + output* changes today's measured value to 341, and the preview
      text in the dialog updates.
- [ ] Restarting the app does not re-announce a level already announced today.
- [ ] With alerts on, minimising hides the window to the notification area; double-clicking the icon
      restores it.
- [ ] With alerts on, closing the window keeps the app watching from the notification area and says so
      once. **Exit** from the icon's menu really exits.
- [ ] With alerts off, minimising behaves like a normal window and closing exits.
- [ ] Clearing *Keep watching from the notification area* in **Data sources…** makes minimise and close
      behave like a normal window even with alerts on.
- [ ] The threshold accepts values up to 100,000,000,000 and rejects negatives.
- [ ] **The alerts dialog shows its whole content, including Save and Cancel, with no clipped bottom
      edge.** Check at 100%, 150%, and 200% display scaling, and with Windows' "Make text bigger"
      raised — the explanatory text reflows, and the dialog must grow to match it.
- [ ] Changing the threshold or the metric does not make the dialog's text outgrow the window.
- [ ] The dialog can be resized, and shrinking it produces a vertical scrollbar rather than
      unreachable controls.
- [ ] On a short screen (or with the taskbar enlarged) the dialog still opens fully on screen.

## Archive durability

- [ ] `%LOCALAPPDATA%\ClaudeUsage\usage-archive.json` exists after the first refresh and contains no
      readable file path, project name, prompt, session id, or message id.
- [ ] Restart the app: history is unchanged and the status bar reports 0 transcripts read this pass.
- [ ] Move the fixture's session-one transcript out of the folder and refresh. Yesterday and today
      still show their archived totals, and the status bar reports days served from the archive.
- [ ] Put it back and refresh: totals are unchanged, not doubled.
- [ ] Truncate a transcript to one response and refresh: the recorded day does not drop.
- [ ] **Rebuild archive…** warns that unrecoverable days will be lost, and on confirmation recounts
      only what is on disk.
- [ ] Deleting `%LOCALAPPDATA%\ClaudeUsage` entirely lets the app start clean without error.

## Read-only behaviour and live updates

Point the app at a copy of a real `.claude` folder for this section.

- [ ] Record `Get-FileHash` for every `projects\**\*.jsonl` before and after several refreshes: all
      hashes and last-write times are unchanged. Nothing is created or deleted under `.claude`.
- [ ] With Claude Code actively working in another window, totals rise within a couple of seconds of
      each response, without a visible flicker or a dropped value.
- [ ] Totals never decrease during a live session.
- [ ] Copy a large transcript in over the top of an existing one (atomic replace): the app picks it up
      and does not show an error.
- [ ] Append a partial line to a transcript (no trailing newline), refresh, then complete the line:
      the incomplete state does not lower any total, and the completed state is counted once.
- [ ] Twenty rapid changes in a burst cause a small number of coalesced refreshes, not twenty.
- [ ] Delete and recreate a transcript while the app runs: no unhandled error appears.
- [ ] Setting the whole `projects` tree read-only changes nothing about the displayed numbers.
- [ ] Auto-refresh `Off` stops timed refreshes; **Refresh now** and F5 still work.

## Installation without administrator rights

- [ ] `install.cmd` run as a standard user completes without any UAC prompt.
- [ ] The app is in `%LOCALAPPDATA%\Programs\ClaudeUsage`; nothing is written to `%ProgramFiles%`,
      `%WINDIR%`, or `HKLM`.
- [ ] Start menu shortcuts and the Apps & Features entry exist for this user only.
- [ ] The app launches from the Start menu shortcut and shows its own icon in the taskbar.
- [ ] Reinstalling over an existing install succeeds while the app is closed, and reports a clear
      error if it is running.
- [ ] Upgrading from 0.1 keeps the folder you had chosen there: a 0.1 `settings.txt` pointing at
      `<folder>\stats-cache.json` results in `<folder>` being read.
- [ ] `uninstall.cmd` removes the app, shortcuts, and the registry entry, and keeps preferences.
- [ ] `uninstall.cmd --purge` also removes `%LOCALAPPDATA%\ClaudeUsage`.
- [ ] Running `ClaudeUsage.exe` from the extracted ZIP with no install works.
- [ ] The manifest requests `asInvoker`; the exe never triggers an elevation prompt.

## Keyboard and assistive technology

- [ ] Tab reaches every control in a sensible order, and focus is always visible.
- [ ] F5 refreshes; Ctrl+E exports; Esc closes each dialog; Enter activates its default button.
- [ ] Narrator announces each card's label and exact value, each grid's purpose, and the state badge.
- [ ] The threshold bar exposes an accessible name and its percentage.
- [ ] Grid columns can be sorted from the keyboard, and values stay right-aligned and thousand-grouped.
- [ ] Windows high-contrast themes keep every label, badge, chart, and grid readable.
- [ ] No information is conveyed by colour alone: the threshold state is also in the text.

## Display scaling and window sizing

- [ ] At 100%, 150%, and 200% scaling, text is crisp and nothing is clipped.
- [ ] Moving between monitors with different scaling re-renders correctly.
- [ ] At the 560x400 minimum size, each tab scrolls and no control is unreachable.
- [ ] Maximised on a wide monitor, cards and grids stretch without distortion.

## Performance

- [ ] First scan of a real `.claude` folder reports its progress and stays responsive.
- [ ] Routine refreshes are visually instant, and the status bar shows few or no transcripts reread.
- [ ] Memory settles and does not climb over an hour of 30-second refreshes with Claude Code active.
- [ ] A 90-day range redraws its chart and tables without a perceptible pause.

## Privacy

The fixture embeds `PRIVATE_PROMPT_CANARY`, `PRIVATE_REPLY_CANARY`, `PRIVATE_TOOL_CANARY`,
`PRIVATE_PATH_CANARY`, and `PRIVATE_SESSION_CANARY`.

- [ ] No canary appears in any window, tooltip, accessible name, status message, or error message.
- [ ] No canary appears in the exported PDF (search the file, including its raw bytes).
- [ ] No canary appears in `%LOCALAPPDATA%\ClaudeUsage\*`.
- [ ] The app makes no network connection (verify with Resource Monitor or a firewall prompt).
- [ ] The UI offers no transcript search, prompt preview, project name, or content drill-down.

## Pass criteria

Every box above is checked, with these treated as blocking:

1. Today and history match the deterministic values, in particular 1,126 rather than 2,326.
2. No Claude Code file is modified, and no transcript content leaves the aggregate layer.
3. Installation, use, and uninstallation never require administrator rights.
4. The exported PDF opens cleanly and agrees with the window.
5. Threshold alerts fire once per level per day and survive a restart.
6. History remains visible after the transcripts it came from are gone.
