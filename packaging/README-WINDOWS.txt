CLAUDE USAGE FOR WINDOWS 11
===========================

Claude Usage is an unofficial, read-only viewer for local Claude Code activity.
It reads stats-cache.json for history and aggregates current-day token metadata
from the in-scope projects transcripts. It refreshes every 30 seconds by default.
It does not show remaining Claude subscription limits, weekly quota, billing
totals, or server-side usage.

INSTALL (NO ADMINISTRATOR RIGHTS)
---------------------------------

1. Extract every file from the release ZIP.
2. Double-click install.cmd.
3. Open "Claude Usage" from the Start menu.

The installer copies the app to:

  %LOCALAPPDATA%\Programs\ClaudeUsage

It creates shortcuts and an Apps & Features entry only for the current Windows
user. It uses only the current user's profile and never requests elevation.

PORTABLE USE
------------

You can skip installation and run ClaudeUsage.exe directly from the extracted
folder.

DATA LOCATION
-------------

The default native-Windows path is:

  %USERPROFILE%\.claude\stats-cache.json

If CLAUDE_CONFIG_DIR is set, the app uses stats-cache.json in that directory.
Use the app's Browse command for another file, including a WSL location.

HISTORY AND LIVE TODAY
----------------------

Historical dates come from stats-cache.json. For the current UTC day, the app
also scans usage metadata from the active Claude directory's
direct project *.jsonl transcripts and session\subagents\agent-*.jsonl files at
the configured refresh interval (30 seconds by default). Tokens, messages, and
tool calls are assigned to the UTC calendar date on each transcript entry. A
session and its start hour are assigned to that transcript's first entry. Around
local midnight, "Today" can therefore differ from the user's local calendar
date.

Current Claude Code cache format v5 stores daily "processed tokens": input +
output + cache read + cache creation. Older cache versions store input + output
only. Claude Usage detects and labels that distinction.

UNINSTALL
---------

Use "Uninstall Claude Usage" from the Start menu, Apps & Features, or run the
installed uninstall.cmd. Settings are kept by default. To remove them too, run:

  uninstall.cmd --purge

SECURITY AND PRIVACY
--------------------

The app reads the selected cache and in-scope transcript usage metadata locally
and sends nothing over the network. Current-day transcript content is never
retained, displayed, transmitted, or logged. The current-day scanner retains
only aggregates: dates, model IDs, token counters, messages, sessions, and
tool-call counts. It does not retain or display prompts, responses, thinking,
tool inputs/results, project paths, session IDs, or credentials. Source files
are never modified.
Per-user preferences store only the selected cache path and refresh interval;
usage values and transcript content are not logged.

An unsigned development build may trigger Windows SmartScreen. Follow your
organization's software policy; do not bypass a policy-controlled block.

Claude is a trademark of Anthropic. This project is not affiliated with or
endorsed by Anthropic.
