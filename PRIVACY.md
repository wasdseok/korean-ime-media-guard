# Privacy and publication scope

The utility installs a low-level keyboard hook. All keyboard events are inspected transiently so that the narrow suppression policy can decide whether to forward an event. It remembers the latest T, O, D, Y and Hangul release timestamps and Q/Backspace press timestamps in memory. It does not retain ordinary key histories, reconstruct or save typed sentences, read the clipboard, or make network requests.

The bounded local status log contains at most 256 Media Next events, virtual key and scan code, event flags, timing relative to the trigger keys, suppression status, counters, process ID and update time. Status schema version 3 adds timing relative to Y release and Q/Backspace press; these timing values accompany Media Next events and are not a history of ordinary key events. The installed copy writes this to `%LOCALAPPDATA%\KoreanInputGuard\status.json`. `installation.json` contains the local installation and shortcut paths. These files stay on the user's computer.

The optional offline diagnostic page records its test-field text and keyboard/composition events, and focus/pointer events on that page, in memory. It does not collect typing in other apps and has no upload endpoint. A user can explicitly export diagnostic JSON. That export may contain the entire test text and detailed input events. Use non-sensitive sample text and review any export before sharing it.

## Public repository

This repository contains source code, installation tools, an executable, documentation, sanitized aggregate verification reports and release packages. It excludes original diagnostic logs, personal filesystem paths, screenshots, registry backups, device identifiers and credentials. Specific test-machine model and exact test timestamps were removed from the public copy. The original local maintenance material was not uploaded.

`.gitignore` helps prevent accidental additions but is not an access-control mechanism. Anything committed or attached to a public release is public. Before submitting an issue, remove local paths, typed text, usernames, tokens and device identifiers from logs.

The project has no telemetry or analytics service. Public GitHub interactions and downloads are handled by GitHub under its own policies.
