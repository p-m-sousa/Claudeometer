CLAUDE USAGE (CLAUDEOMETER) FOR WINDOWS 11
==========================================

An unofficial, read-only utility that tracks how many tokens Claude Code has
processed on this computer, today and historically. It reads Claude Code's own
session transcripts, works offline, and refreshes every 30 seconds by default.

It reports tokens, not spend. It does not show your remaining Claude
subscription limit, weekly quota, or any billing total.

INSTALL (NO ADMINISTRATOR RIGHTS)
---------------------------------

1. Extract every file from the release ZIP.
2. Double-click install.cmd.
3. Open "Claude Usage" from the Start menu.

The installer copies the app to:

  %LOCALAPPDATA%\Programs\ClaudeUsage

It creates shortcuts and an Apps & Features entry for the current Windows user
only, uses only the current user's profile, and never requests elevation.

PORTABLE USE
------------

You can skip installation and run ClaudeUsage.exe directly from the extracted
folder.

  ClaudeUsage.exe [--data-dir <folder>] [--self-test]

Repeat --data-dir to read more than one folder.

WHERE THE DATA COMES FROM
-------------------------

Claude Code writes one JSONL transcript per session under:

  <data folder>\projects\

The app finds that data folder on its own. It checks CLAUDE_CONFIG_DIR (which
may list several folders, separated by ; or ,), %USERPROFILE%\.claude,
%USERPROFILE%\.config\claude, XDG_CONFIG_HOME, HOME, and HOMEDRIVE+HOMEPATH.
Any folder containing a "projects" subfolder works, and several can be read at
once.

Use "Data sources..." to see exactly which folders are being read, to add one by
hand (including a WSL path such as \\wsl.localhost\Ubuntu\home\you\.claude), or
to search installed WSL distributions.

Older versions of this app read stats-cache.json. Current Claude Code releases
do not write that file, so it is no longer used for anything.

HISTORY THAT OUTLIVES CLAUDE CODE'S CLEANUP
-------------------------------------------

Claude Code deletes transcripts once they age past its cleanup period, which
defaults to 30 days. The app therefore keeps its own daily totals in:

  %LOCALAPPDATA%\ClaudeUsage\usage-archive.json

Each scan is merged into that archive, so history keeps accumulating and older
days remain visible after their transcripts are gone. Daily totals only ever
grow, so the merge keeps the higher value and a partly written transcript can
never reduce a recorded day. "Rebuild archive..." discards the stored totals and
recounts from the transcripts still on disk; days whose transcripts Claude Code
already deleted cannot be recovered.

Days are local calendar days.

WHAT THE NUMBERS MEAN
---------------------

Processed tokens = input + output + cache read + cache creation. Cache-read
tokens usually dominate that figure. "Input + output" is shown separately.

A single model response is written to the transcript as several lines, one per
content block, each repeating the same usage numbers. The app counts each
response once, so totals are not inflated. Tool calls are counted from every
line.

ALERTS
------

"Alerts..." sets a daily token threshold and a warning percentage. Each level is
announced at most once per day in the notification area, and the Today tab shows
progress against the threshold. Alerts need the app to be running; by default it
keeps watching from the notification area when the window is minimised or
closed. Right-click the notification icon to exit.

PDF REPORT
----------

"Export PDF..." (or Ctrl+E) saves the selected date range and model filter as a
paginated report: summary tiles, a per-day chart with the threshold marked,
totals by model, and a full daily table. No print driver or extra software is
involved.

UNINSTALL
---------

Use "Uninstall Claude Usage" from the Start menu, Apps & Features, or run the
installed uninstall.cmd. Preferences and the usage archive are kept by default.
To remove them too, run:

  uninstall.cmd --purge

Purging deletes the usage archive, which is the only copy of history older than
Claude Code's transcript cleanup window.

SECURITY AND PRIVACY
--------------------

Transcripts are plaintext and can contain prompts, replies, thinking, source
code, file paths, tool arguments, tool results, and secrets. The app opens them
read-only, never modifies them, and keeps only aggregates: dates, model
identifiers, and token and activity counters. Prompt text, assistant text,
thinking, tool arguments, tool results, project paths, session identifiers, and
message identifiers are never retained or displayed.

Files under %LOCALAPPDATA%\ClaudeUsage hold those aggregates plus, for change
detection, non-reversible hashes of transcript paths and response identifiers.
No readable path or identifier is stored, and nothing is sent over the network.

An unsigned development build may trigger Windows SmartScreen. Follow your
organization's software policy; do not bypass a policy-controlled block.

Claude is a trademark of Anthropic. This project is not affiliated with or
endorsed by Anthropic.
