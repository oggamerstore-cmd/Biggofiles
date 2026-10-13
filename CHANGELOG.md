# Changelog

## v1.0 — October 12, 2026

- Standardized the application name to Biggofiles across the interface, executable, source, and documentation.
- Clarified listed file sizes versus drive usage, with an in-app explanation. Drive figures refresh after scanning.
- Disabled recycling when no completed scan result exists, cleared previews after scan failures, and added file-identity and final Shell callback checks before recycling.
- Made long confirmation and information messages scroll so their action buttons stay reachable.
- Removed unused color and invalidation code, duplicate journal work, and repeated clock reads during cleanup filtering. Moved the independent comparison scanner into test-only sources.
- Added replacement-file, final callback, incomplete-result, and dialog checks; compiler warnings now fail the build.
- Added a clean GitHub export with MIT source, documentation, portable download, checksums, and a Windows build/test workflow.

## v0.2 — October 12, 2026

- Fixed repeated whole-index copying and sorting during scan progress, which could make a large drive scan take many minutes.
- Added bounded progress previews, incremental counters, stage labels, and a running elapsed-time display. Removed the confusing progress footer label.
- Permission changes now rebuild affected folder trees. Unrelated file-permission updates no longer force a full drive scan; ancestor changes still rebuild safely.
- Added a light-theme switch; dark remains the startup default. Both themes cover drive cards, controls, file categories, and dialogs.
- Updated the title bar, About, documentation, and packages to use v0.2.
- Kept sidebar actions visible at the minimum window height.
- Added million-record progress, real permission-refresh, and theme-switch coverage, plus actual-interface drive benchmarks.

Earlier version numbers below identify unpublished local builds.

## 1.1.0 — October 12, 2026

- Added batched directory reads with up to four workers and an in-memory NTFS index.
- Added journal-based repeat scans, including an unprivileged Windows 10+ operation where available.
- Added checkpoint validation, automatic full-scan fallbacks, open-writer tracking, and manual fresh scans.
- Corrected double-click dispatch to use the clicked row; native Shell folder selection works during scans.
- Expanded Windows/boot/recovery/profile/app protection, with live Windows Resource Protection and hard-link checks before recycling.
- Added backup-first cleanup suggestions with reasons and backup acknowledgment.
- Moved OG STUDIO branding into About and added protected-file labels.
- Expanded testing from 15 to 21 integration groups, including independent full-scan comparisons.

## 1.0.0 — October 12, 2026

Initial native Windows release: largest-first scans, search/filtering, CSV export, coverage notes, Explorer navigation, and confirmed recycling.
