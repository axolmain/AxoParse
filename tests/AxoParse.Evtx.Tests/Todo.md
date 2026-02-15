# Test TODOs

- Add exact-value assertions for chunk 0 of security.evtx (pin records 1-91, free space 65376, checksums, etc. like the
  Rust parser does)
- Add corrupted header resilience test once EvtxChunk iteration exists (inflate last_event_record_id and
  free_space_offset, verify iteration still completes cleanly)

## EvtxChunk.ValidateChunk()

Add a validation method that returns a list of warnings. Key checks:

- Record count matches expected (lastEventRecordId - firstEventRecordId + 1)
- Record IDs are sequential (no gaps)
- Size == SizeCopy for each record
- BinXml payload starts with 0x0F (FragmentHeader token)
- BinXml payload is non-empty and not suspiciously small (< 4 bytes)
- WrittenTime is non-zero for each record
- Dirty flag noted as warning (benign, e.g. unclean shutdown)

---
Full parse of 27 files:
[2-system-Microsoft-Windows-LiveId%4Operational.evtx]     1.35ms | 16 chunks | 399 records
[new-user-security.evtx]     0.01ms | 1 chunks | 4 records
[E_ShadowCopy6_windows_system32_winevt_logs_Microsoft-Windows-CAPI2%4Operational.evtx]     0.20ms | 16 chunks | 459
records
[security_big_sample.evtx]    33.15ms | 481 chunks | 62031 records
[sample-with-irregular-bool-values.evtx]     0.27ms | 20 chunks | 3028 records
[2-system-Security-dirty.evtx]    17.27ms | 180 chunks | 14430 records
[security_bad_string_cache.evtx]     2.19ms | 26 chunks | 2261 records
[Security_with_size_t.evtx]     0.09ms | 6 chunks | 636 records
[post-Security.evtx]     0.02ms | 2 chunks | 126 records
[2-vss_7-System.evtx]     0.05ms | 4 chunks | 457 records
[sysmon.evtx]     0.01ms | 1 chunks | 41 records
[Microsoft-Windows-HelloForBusiness%4Operational.evtx]     0.00ms | 1 chunks | 6 records
[Application_no_crc32.evtx]     0.01ms | 1 chunks | 17 records
[E_Windows_system32_winevt_logs_Microsoft-Windows-CAPI2%4Operational.evtx]     0.13ms | 13 chunks | 340 records
[E_Windows_system32_winevt_logs_Microsoft-Windows-Shell-Core%4Operational.evtx]     0.06ms | 5 chunks | 714 records
[Microsoft-Windows-LanguagePackSetup%4Operational.evtx]     0.00ms | 1 chunks | 17 records
[security.evtx]     3.76ms | 26 chunks | 2261 records
[2-vss_0-Microsoft-Windows-RemoteDesktopServices-RdpCoreTS%4Operational.evtx]     0.18ms | 16 chunks | 1912 records
[sample_with_a_bad_chunk_magic.evtx]     0.03ms | 4 chunks | 270 records
[issue_201.evtx]     0.00ms | 1 chunks | 1 records
[2-vss_0-Microsoft-Windows-TerminalServices-RemoteConnectionManager%4Operational.evtx]     0.18ms | 16 chunks | 1775
records
[Application.evtx]     1.61ms | 50 chunks | 6250 records
[Archive-ForwardedEvents-test.evtx]     1.09ms | 66 chunks | 653 records
[sample-with-zero-data-size-event.evtx]     0.50ms | 4 chunks | 335 records
[system.evtx]     0.05ms | 3 chunks | 326 records
[Security_short_selected.evtx]     0.01ms | 1 chunks | 7 records
[MSExchange_Management_wec.evtx]     0.00ms | 1 chunks | 1 records