# How scanning works

Biggofiles v1.0 uses parallel directory metadata enumeration followed by validated NTFS journal refreshes. See the measured results and limits in [testing notes](TESTING.md).

## First scan

The app requests directory entries in 64 KB batches through `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)`. Entries include names, sizes, timestamps, attributes, reparse tags, and identities. Up to four workers read separate folders. Unsupported providers fall back to compatible Win32 enumeration. Hidden files are included; directory links are reported rather than followed.

One folder/file index is held in memory for the current location and app session. Restarting, switching locations, or selecting **Start a fresh scan** rebuilds it. There is no on-disk index.

Progress counters are updated once per new directory record. A bounded heap retains at most 100 preview entries, and only those entries are copied and sorted for an update. The complete index is assembled and sorted once at completion. Earlier builds copied and sorted the entire growing index during progress reporting; on large drives that repeated work could dominate scan time. Scanner-only benchmarks with progress disabled failed to expose that problem. The v0.2 benchmarks exercise both progress reporting and the actual WPF interface.

Microsoft documents the returned fields in [FILE_ID_EXTD_DIR_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_extd_dir_info).

## Scan again

1. Verify the resolved root, root file identity, volume identity, and user/elevation context.
2. Verify the journal ID and that the saved checkpoint remains within retained history.
3. Read changes since the checkpoint. The standard operation is tried first, then the Windows 10+ unprivileged journal operation. Identities and reasons are sufficient even when names are redacted.
4. Re-enumerate changed directories, reconcile additions/removals, rebuild moved or permission-changed subtrees, and revisit indexed hard-link parents. Retry previously inaccessible folders. File-permission updates dirty their containing folder; unrelated permission updates no longer trigger a drive-wide rebuild.
5. Reuse unchanged records and sort the complete discovered list. Save the pre-scan checkpoint so changes during reads are replayed next time.

Journal gaps, resets, permission changes to ancestors of the selected root, unsupported records, root replacement, changed access context, failures, or cancellation prevent index reuse or force a full scan. Failure to identify an ancestor also forces a full scan. Directory-permission changes discard all cached descendants before rereading so newly hidden or newly accessible trees are handled correctly. This does not rely on FileSystemWatcher events, and it never creates, resizes, or deletes the volume journal.

Windows combines repeated writes while a handle stays open. The baseline replays retained history to seed unfinished writes, and refreshes revisit their parent folders until close records arrive. See [change-journal behavior](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records) and [journal identity validation](https://learn.microsoft.com/en-us/windows/win32/fileio/using-the-change-journal-identifier).

## Comparison with MFT scanning

| Mechanism | Decision |
| --- | --- |
| FindFirstFileEx / FindNextFile | Compatibility path and independent comparison scanner. |
| Batched directory metadata | Returns names and sizes, preserves hard-link paths and access checks, and supports bounded concurrency. Used for first scans. |
| FSCTL_ENUM_USN_DATA | Enumerates NTFS records, but USN records lack file length. A name/ID count alone cannot produce accurate size ordering or every hard-link path. |
| Raw $MFT parsing | Potentially fast, but adds privileged raw access and complex on-disk format parsing. Not implemented or benchmarked here. |
| USN journal refresh | Identifies changed objects; current directory metadata supplies sizes and names. Used for repeat scans. |

See the fields available in [USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2).

## Accuracy boundary

A live drive is not a frozen snapshot. Permission gaps and skipped locations are reported. Internal NTFS metadata and alternate streams are not measured. Logical bytes do not equal recoverable disk space; hard-link paths are separate.

An exceptionally long-lived writer whose opening change has already aged out of retained journal history cannot be reconstructed from that history. A fresh scan may be needed for its latest size. A true point-in-time whole-volume inventory would require snapshot infrastructure and different privileges/costs; this version makes no such guarantee.

The app cannot prove every unprotected file is dispensable. It blocks conservative Windows/app/profile/boot paths and system components, checks resolved paths, recorded file identity, and multiple hard links, and consults [Windows Resource Protection](https://learn.microsoft.com/en-us/windows/win32/api/sfc/nf-sfc-sfcisfileprotected) before recycling. These checks repeat at the Shell's pre-delete callback; permanent deletion is vetoed. Filesystem changes cannot be made atomic with a path-based Shell operation, so the app does not promise protection against every concurrent change. Backup suggestions are review prompts, not evidence that a file is unused or already backed up.
