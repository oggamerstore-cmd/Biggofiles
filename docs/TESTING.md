# Validation for Biggofiles v1.0

Tested on 64-bit Windows with .NET Framework 4.8 on October 12, 2026. Results describe this machine and workload; they are not guarantees for every drive.

## Regression and cause

A user reported a C: scan still running after 15 minutes 30 seconds. Earlier progress updates copied the complete growing index, summed it, and sorted it for a small preview. The reporting timer was reset before that expensive work, allowing it to run again immediately once the work itself exceeded the reporting interval. The earlier D: benchmark disabled progress, so it did not test the affected path.

v0.2 updates counters incrementally, keeps a bounded preview heap, reports after batches, and assembles/sorts the full result only at completion. The full interface benchmark also exposed permission updates anywhere on C: triggering complete repeat scans. Permission-changed subtrees are now discarded and rebuilt individually; changed ancestors still force a full scan.

## Actual interface benchmark

Two consecutive C: scans ran through the actual WPF controller in an off-screen native window, including progress rendering, final sorting, filtering, and binding all results. Both used the same process and normal Windows user context. A 50 ms dispatcher timer checked responsiveness. No user files were modified.

| Measurement | First scan | Scan again |
| --- | --- | --- |
| Files bound in the interface | 1,549,104 | 1,549,104 |
| Successfully scanned folders | 532,085 | 532,085 |
| Scan notes | 155 | 155 |
| End-to-end elapsed | **34.4 seconds** | **2.0 seconds** |
| Scanner elapsed | 34.1 seconds | 1.8 seconds |
| Method | Full scan | Journal refresh |
| Folders read | 532,179 | 189 |
| Folders reused | 0 | 531,990 |
| Largest observed dispatcher heartbeat gap | 411 ms | 77 ms |
| Process working set after completion | 834.22 MB | 876.60 MB |

This is a warm-cache test on an active system drive, not a cold-start disk benchmark. Retaining about 1.55 million files and 532,000 folder records requires substantial memory despite the small executable. File activity, hardware, access rights, security changes, and journal availability affect timing and counts. The 155 coverage notes remain visible; faster completion does not imply access to protected or skipped locations.

An earlier progress-enabled scanner-only run in the same session completed C: in 31.8 seconds. An independent traversal of the live drive had 13 differing metadata entries on each side; it was not an exact snapshot match. Controlled fixture comparisons, including edits, moves, deletion, and permission changes, match exactly. No claim of snapshot consistency is made for an actively changing drive.

## Automated coverage

All **24 integration groups** pass with compiler warnings treated as errors:

1. Hidden, system, zero-byte, Unicode, extensionless, and locked files.
2. File sizes above 5 GB.
3. Unicode paths over 300 characters.
4. Real directory junction loop detection.
5. Real access-denied ACL handling and restoration.
6. Separate hard-link paths and logical totals.
7. Cancellation, partial results, and released handles.
8. Empty, missing, and invalid roots.
9. Search, category/size filtering, and four sorts.
10. CSV escaping, Unicode, and formula-prefix protection.
11. Changed/missing files and protected Windows directory aliases.
12. Permanent-delete callback rejection.
13. Actual Recycle Bin operation on an 11-byte disposable file.
14. One million synthetic records without a result cap.
15. One million progress records with 1,000 bounded snapshots, exact counts/totals, and a preview matching independent full sorting (about 1.2 seconds, including fixture generation and final comparison).
16. Real journal refreshes compared with independent scans after edits, additions, deletions, moves, folder renames, replacements, and hard-link changes.
17. Open writers, including handles already open before the baseline.
18. Changes during a baseline scan replayed on refresh.
19. Journal reset, expired checkpoint, forced invalidation, missing-journal, and failure fallbacks.
20. Real directory permission changes hide and restore subtrees without reusing stale descendants. Unrelated permission changes preserve the index; ancestor changes force a full scan.
21. Backup-first suggestions excluding recent/protected/irrelevant files.
22. Boot, recovery, profile, app, and shared-hard-link protection.
23. WPF binding, clicked-row double-click dispatch, ignored background double-clicks, cleanup mode, filtering, sorting, virtualization, default dark theme, light/dark switching without lost results or selection, version display, and theme control visibility at minimum window size.
24. File identity detects a replacement with the same name, size, timestamps, and attributes. Final Shell callbacks reject replaced or modified files. The WPF checks also cover disabling recycling without a completed result and rendering the light-theme About and storage-explanation dialogs.

Dark, light, cleanup, empty, and minimum-size screens are rendered from actual WPF controls. Screenshots use generated sparse files, not personal file inventories. A 100,000-record list realized only four rows at the tested size.

## Reproduce

Build and run fixture tests with `build.ps1 -Test`. Then use the test executable with `--benchmark C:\` for progress-enabled scanner/comparison measurements, or `--benchmark-ui C:\` for two actual-interface scans. Replace C: with the intended drive. Benchmark output contains counts and timing, not personal filenames. The interface benchmark window is off-screen and closes when finished.

Tests create fixtures under `.test-work`, restore temporary access rules, and remove fixtures. A tiny generated recycle-test file may remain in the Recycle Bin. Run in a normal Windows session because a restricted sandbox can block ACL and Shell operations.

## Limits and release status

See [scanning architecture](SCANNING.md) for retained history, long-lived writers, live-volume consistency, logical sizes, and permission limits. No 100% access or zero-error guarantee is made.

Local NTFS and Shell integration were tested. Network shares, removable file systems, cloud providers, mixed-DPI transitions, and separate Windows 10 installations need field testing. Raw-MFT scanning has not been benchmarked. GitHub automation is prepared but has not run remotely. The local build is unsigned; no public release has been published.
