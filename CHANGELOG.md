# Changelog

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
