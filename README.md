# Claude Usage

Claude Usage is a small, unofficial Windows 11 viewer for the local activity data that Claude Code writes under `~/.claude`. It combines historical aggregates from `stats-cache.json` with a read-only scan of current-day usage metadata in direct project transcripts and their `subagents/agent-*.jsonl` files. It works offline and can be installed for one Windows user without administrator rights.

It presents date and model filters over the dimensions that Claude's local data actually contains. In current stats-cache format v5, daily model totals include input, output, cache-creation, and cache-read tokens. Older cache versions may contain input plus output only, and the app labels that distinction. These local figures are not an authoritative billing report and do not expose remaining Claude plan, session, or weekly limits.

Current-day totals refresh every 30 seconds by default. Token, message, and tool-call activity is assigned to each transcript entry's UTC calendar date, matching current Claude Code behavior; session count and start-hour remain assigned to the transcript's first entry.

## Install on Windows 11

Download the Windows ZIP, extract all files, and double-click `install.cmd`. The installer writes only to `%LOCALAPPDATA%\Programs\ClaudeUsage`, adds current-user Start menu shortcuts, and registers a current-user Apps & Features entry. It never requests elevation.

You can also skip installation and run `ClaudeUsage.exe` directly. To uninstall, use the Start menu shortcut, Apps & Features, or the installed `uninstall.cmd`. Pass `--purge` to the script only if you also want to remove per-user settings.

The default data file is `%USERPROFILE%\.claude\stats-cache.json`. A `CLAUDE_CONFIG_DIR` environment variable takes precedence. A manually selected file supports alternate profiles and WSL-hosted data.

See [the packaged Windows guide](packaging/README-WINDOWS.txt) for end-user details.

## Build and package

The Windows application project is expected at `src/ClaudeUsage.WinForms/ClaudeUsage.WinForms.csproj`, targets .NET Framework 4.8, and emits `ClaudeUsage.exe`. Windows 11 includes .NET Framework 4.8 or 4.8.1, so users do not install a separate runtime.

With a .NET 8 SDK or newer:

```powershell
dotnet build src/ClaudeUsage.WinForms/ClaudeUsage.WinForms.csproj --configuration Release
./scripts/package-release.ps1 -NoBuild -Version 0.1.0
```

The package script creates a portable ZIP and SHA-256 checksum under `artifacts/`. It also supports building first by omitting `-NoBuild`.

A non-Windows machine can compile against .NET Framework reference assemblies when the project enables Windows targeting. The GitHub Actions workflow performs the authoritative Windows build, runs test projects, invokes `ClaudeUsage.exe --self-test`, and uploads the ZIP.

Unsigned local builds may trigger Windows SmartScreen. Production downloads should be Authenticode-signed with a trusted certificate. Signing affects trust prompts, not the app's no-admin design.

## Privacy

Claude Usage reads the selected stats cache and the in-scope Claude Code transcript files locally and does not transmit them. The current-day scanner retains only aggregate dates, model identifiers, token counters, session/message counts, and tool-call counts. It does not retain or display prompt text, assistant text, thinking, tool arguments/results, project paths, session identifiers, or credentials. Source files are opened read-only and are never modified. Its small per-user preference files store only the selected cache path and automatic-refresh interval; no usage values or transcript content are logged.

## License

[MIT](LICENSE). Claude is a trademark of Anthropic. This project is not affiliated with or endorsed by Anthropic.
