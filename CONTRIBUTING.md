# Contributing

Biggofiles is a native C# / WPF application targeting the Windows-installed .NET Framework 4.8. Keep the app small, local, and dependency-free. C# syntax must remain compatible with the bundled Framework compiler (C# 5).

## Structure

- `src/FastScanner.cs`: batched metadata enumeration, four-worker scheduling, identities, and the in-memory index.
- `src/UsnJournal.cs`: validated journal reading and tracking of unfinished writes.
- `src/ScanPreview.cs`: incremental counters and bounded progress previews.
- `src/ThemePalette.cs`: shared dark/light colors, including code-created controls and dialogs.
- `src/NativeFiles.cs`: shared native metadata and path helpers.
- `tests/ReferenceScanner.cs`: the independent comparison scanner, excluded from the application executable.
- `src/CleanupAdvisor.cs`: backup-first review suggestions based on age and size.
- `src/Models.cs`: compact file records, formatting, categories, query operations, and CSV export.
- `src/MainWindow.xaml`: visual layout, styles, and virtualized file grid.
- `src/MainWindow.cs`: asynchronous UI coordination, filters, selection, exports, and dialogs.
- `src/ShellServices.cs`: folder picker and Recycle Bin COM integration, plus file-action checks.
- `tests/Tests.cs`: standalone integration harness with real Windows fixtures and WPF rendering.

## Change guidelines

- Keep I/O and large queries away from the UI dispatcher. Invalidate stale searches and keep scan lifetimes isolated.
- Preserve metadata-only scans, explicit coverage notes, and uncapped final results.
- Validate the root, volume, security context, journal ID, and checkpoint before reuse. Rebuild after journal gaps, ancestor permission changes, or unsupported formats. Discard affected directory subtrees on permission changes; never leave their descendants cached.
- Keep progress work bounded: do not copy or sort the entire index for progress reporting. Test large scans with progress enabled and through the WPF controller.
- Keep the pre-scan checkpoint so changes during directory reads are replayed next time. Track unfinished writers, including retained history preceding the baseline.
- Never silently follow directory links or represent logical bytes as reclaimed disk space.
- Never add automatic deletion or empty the Recycle Bin. Preserve confirmation, current-file revalidation, and the permanent-delete veto.
- Include tests for scanner, file-action, or query behavior that changes.
- Run `build.ps1 -Test -Package` in a normal Windows session. Review both themes and minimum-size screenshots in `docs`.
- Do not commit generated executables, fixture directories, scan exports, personal paths, or private data.

Release packaging uses the MIT License. Update assembly version, title/About copy, tests, download links, and release notes together. Package names are read from AssemblyInfo.cs. Code signing is a separate release step and is not configured in this repository.
