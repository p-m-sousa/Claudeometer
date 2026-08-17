# Claudeometer

Claudeometer is a small, unofficial Windows 11 utility that tracks how many tokens Claude Code has
processed on this computer — today and historically. It reads Claude Code's own session transcripts,
runs entirely offline, and installs for one Windows user without administrator rights.

- Current-day and historical token totals, split into input, output, cache read, and cache creation
- Any date or date range, with an optional per-model filter
- A formatted PDF usage report
- An optional daily token threshold with an early warning
- Automatic discovery of Claude Code's data folder, with manual override

It reports tokens, not spend, and it is not a billing statement or a view of your remaining plan
allowance.

## Where the numbers come from

Claude Code writes one JSONL transcript per session under `<data folder>\projects\`. Every assistant
entry carries a `message.usage` block with the four token counters. Those transcripts are the only
complete local record of usage, so Claudeometer reads them directly:

- **Auto-discovery** checks `CLAUDE_CONFIG_DIR` (which may list several folders), `%USERPROFILE%\.claude`,
  `%USERPROFILE%\.config\claude`, `XDG_CONFIG_HOME`, `HOME`, and `HOMEDRIVE`+`HOMEPATH`. Installed WSL
  distributions are detected and can be searched on demand. Any folder containing a `projects`
  subfolder works, and several folders can be read at once.
- **Its own archive.** Claude Code deletes transcripts once they pass its cleanup period (30 days by
  default), so transcripts alone cannot answer questions about older dates. Claudeometer keeps daily
  aggregates in `%LOCALAPPDATA%\ClaudeUsage\usage-archive.json` and merges each scan into them, so
  history keeps growing and survives that cleanup. Daily totals only ever increase, so the merge takes
  the higher of the two values and a partial read can never lower a recorded day.
- **Incremental scans.** Unchanged transcripts are served from an index instead of being reread. A
  cold scan of 13 MB across 45 transcripts takes about 120 ms; a routine refresh takes about 1 ms.

Days are local calendar days. Refresh happens every 30 seconds by default, and immediately when a
transcript changes.

### One response, several transcript lines

Claude Code writes a separate transcript line for each content block of a single model response —
text, thinking, and each tool call — and repeats that response's **identical** `usage` payload on
every one of them. Summing usage per line therefore multiplies token totals; on a 13 MB sample,
1,759 assistant lines represented only 774 real responses, a 2.3x overcount.

Claudeometer credits usage once per response, keyed on `message.id` plus `requestId`, and counts
`tool_use` blocks from every line. Version 0.1 summed per line and reported inflated totals.

### stats-cache.json is no longer used

Version 0.1 read history from `%USERPROFILE%\.claude\stats-cache.json`. Current Claude Code releases
do not write that file, which is why historical figures were empty. Nothing in Claudeometer depends
on it now.

## Install on Windows 11

Download the Windows ZIP, extract all files, and double-click `install.cmd`. The installer writes only
to `%LOCALAPPDATA%\Programs\ClaudeUsage`, adds current-user Start menu shortcuts, and registers a
current-user Apps & Features entry. It never requests elevation. You can also skip installation and
run `ClaudeUsage.exe` directly.

To uninstall, use the Start menu shortcut, Apps & Features, or the installed `uninstall.cmd`. Passing
`--purge` also deletes your preferences **and the usage archive**, which is the only copy of history
older than Claude Code's transcript cleanup window.

Command line: `ClaudeUsage.exe [--data-dir <folder>] [--self-test]`. Repeat `--data-dir` to read more
than one folder.

## Alerts

Set a daily token threshold and a warning percentage under **Alerts…**. Each level is announced at
most once per day through a notification-area balloon, and the Today tab shows progress against the
threshold. Alerts require the app to be running; by default it keeps watching from the notification
area when the window is minimised or closed.

Choose whether the threshold counts processed tokens (all four categories) or input + output only.
Cache-read tokens dominate processed totals, so the two scales are very different.

## PDF export

**Export PDF…** (or Ctrl+E) writes the current date range and model filter to a paginated report:
summary tiles, a per-day chart with the threshold marked, totals by model, and a full daily table.
The PDF is generated in-process — no print driver, no external tool, no package dependency — so it
works on a locked-down machine.

## Build and test

With a .NET 8 SDK or newer:

```powershell
dotnet build src/ClaudeUsage.WinForms/ClaudeUsage.WinForms.csproj --configuration Release
dotnet run --project tests/ClaudeUsage.Core.Tests/ClaudeUsage.Core.Tests.csproj --configuration Release
./scripts/package-release.ps1 -NoBuild -Version 0.2.0
```

`ClaudeUsage.exe --self-test` exercises the shipped binary end to end: scan, archive durability,
range filtering, threshold evaluation, and PDF structure. The GitHub Actions workflow performs the
authoritative Windows build, runs both, and uploads the ZIP. A non-Windows machine can compile
against .NET Framework reference assemblies; `src/ClaudeUsage.Core` targets `netstandard2.0` and the
application targets `net48`, which Windows 11 already includes.

`WINDOWS_TEST_CHECKLIST.md` has the manual Windows verification steps with deterministic expected
values.

Unsigned local builds may trigger Windows SmartScreen. Production downloads should be
Authenticode-signed. Signing affects trust prompts, not the app's no-admin design.

## Privacy

Transcripts contain prompts, replies, code, and tool output. Claudeometer opens them read-only, never
modifies them, and retains only aggregates: dates, model identifiers, token and activity counters.
Prompt text, assistant text, thinking, tool arguments, tool results, project paths, session
identifiers, and message identifiers are never retained or displayed.

The archive under `%LOCALAPPDATA%\ClaudeUsage` stores those aggregates plus, for change detection,
non-reversible hashes of transcript paths and response identifiers — no readable paths or identifiers.
Nothing is sent over the network.

## License

[MIT](LICENSE). Claude is a trademark of Anthropic. This project is not affiliated with or endorsed
by Anthropic.
