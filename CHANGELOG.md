# Changelog

## 2.0.4 — TypingTune

- Replace the legacy portable guard with a Windows per-user installer and a desktop shortcut that opens the diagnosis screen.
- Add Korean two-set keyboard samples covering all 26 letter keys, seven Shift combinations, and initial, medial, final and complex-final Hangul categories.
- Export a self-describing diagnostic JSON with a 3,000-event ring, IME and low-level hook observations, completed trials, capture health, correction settings and analysis limitations.
- Automatically save to the Windows Downloads known folder when **검사 완료 · 다음** is clicked. Save before advancing, never overwrite an existing file, and retain input and records if saving fails.
- Keep manual JSON export and add orderly exit/restart with local recovery data, plus tray status, suppression counters, pause/resume and exit.
- Make correction trigger keys and time windows configurable. New installations start with correction disabled; the historical preset is opt-in and is not device-specific.
- Include the 2.0.1 capture fix: the default diagnosis window uses the low-level hook and IME/UI events without simultaneous Raw Input registration.
- Include the 2.0.2 sample-height and mouse-wheel improvements and the 2.0.3 removal of the environment memo field from the UI and new exports.
- Publish installers and source ZIPs as release assets; remove superseded v1 executable, setup scripts and standalone HTML from the current source tree. Historical tags and releases remain available.

Validation: 58 core and 37 diagnostic tests passed, including a build from a fresh source extraction. Current-user installation and synthetic-input UI auto-save checks passed. These checks do not establish effectiveness on every physical keyboard or every Korean IME fault.

## 1.1.1

- Increase the offline diagnostic page's retained event limit from 1,000 to 3,000, including the on-screen limit and export retention notice.
- Diagnostic-page and package update only. The v1.1.0 guard executable and suppression policy are unchanged.

## 1.1.0

- Add Y release to the existing T/O/D/Hangul release window of 150 ms.
- Add a separate 30 ms window after an initial Q or Backspace press while that key remains held. Releasing the key ends the window, and auto-repeat does not extend it. Their original key events are forwarded unchanged.
- Keep all existing Media Next signature requirements: `VK_MEDIA_NEXT_TRACK`, scan code zero and `LLKHF_INJECTED`. Continue handling repeats and release of a suppressed Media Next pair.
- Extend local status schema to version 3 with timing relative to Y release and Q/Backspace press. Retain the 256-event Media Next log limit and no typed-sentence collection.
- Do not add K as a trigger: the observed case also fell inside the existing T-release window.

Y-associated Media Next events were observed in two separate browser diagnostic recordings. Q and Backspace each had one associated observation. These observations establish event timing, but do not establish Windows injection flags or scan codes. The expanded policy and its effect on physical typing are separate checks: the latter has not yet been performed for this version. Previous successful T/O typing checks remain historical evidence for the earlier policy, not validation of the new rules.

## 1.0.1

- Add D release to the existing T/O/Hangul release window. This was a preventive extension, without a captured D-related fault or physical-typing validation.
