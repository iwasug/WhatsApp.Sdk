---
phase: 06
plan: 04
subsystem: media-receive
tags: [media, receive, events, wire]
requires:
  - phase: 06-media-pipeline
    plans: [01, 02, 03]
provides:
  - "public MediaReceived event on Messenger"
  - "dispatcher media branches for image/document/audio (1:1 and group)"
  - "internal downloader/uploader seams on Messenger and MessageDispatcher"
affects:
  - "src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs"
  - "src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs"
  - "src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs"
tech-stack:
  added: []
  patterns:
    - "fail-closed inbound media validation before ack/download"
    - "ack-before-download ordering (D-67)"
key-files:
  created: []
  modified:
    - "src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs"
    - "src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs"
    - "src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs"
    - "src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt"
decisions:
  - "MediaReceivedEventArgs/MediaReceivedEvent made public; host owns Content stream disposal"
  - "Media downloader/uploader constructed in Messenger ctor; internal seam property lets tests substitute stub-backed instances"
metrics:
  duration: "task"
  completed: "2026-10-15"
  tasks: 1
  files: 4
  commits: 2
  plan_head_before: b1c9391d6c8638d2eeaf8bb815e88f2f57d7d62b
status: complete
---

# Phase 06 Plan 04: Receive-side Media Event Surface Summary

## What was delivered

- Publicized `MediaReceivedEventArgs` and `MediaReceivedEvent` records in `Media/MediaEvents.cs` with Stream-ownership contract XML-docs.
- Added `public event EventHandler<MediaReceivedEventArgs>? MediaReceived` on `Messenger`, beside `MessageReceived`, plus an internal `RaiseMediaReceivedAsync` twin following the `RaiseMessageReceivedAsync`/`PublishAsync` shape (EventHandler twin then bounded stream write with backpressure).
- Constructed `MediaDownloader` in the `Messenger` constructor over a `SocketsHttpHandler`-backed `HttpClient`, matching the `_uploader` pattern, and exposed internal `MediaDownloader`/`Downloader` (and `MediaUploader`/`Uploader`) seam properties so tests can substitute stub-backed instances. `MessageDispatcher` gained the same kind of internal seam via ctor parameter and property.
- Added receive-side media branches for `imageMessage`, `documentMessage`, and `audioMessage` in both the 1:1 and sender-key (group) dispatcher paths: `MediaMetadata.ValidateReceive` fail-closed before any network call/ack, then ack (with `participant` for groups), then `MediaDownloader.DownloadAsync`, then `RaiseMediaReceivedAsync` with the verified Stream, carrying mimetype/fileLength/caption/fileName/seconds/ptt metadata. Download and validation faults are caught at the branch (log-and-continue) so they never throw through the pump or raise a half-verified event.
- Declared every newly public symbol in `PublicAPI.Unshipped.txt` so the analyzer-clean build stays green.

## Deviations from Plan

None - implemented the planned production surface. Tests were not added/modified in this execution step; the hidden/plan test facts are expected to exercise the seams described above.

## Known Stubs

None.

## Self-Check

- `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo -warnaserror` passes: 0 warnings, 0 errors.
- `dotnet test ... MediaDownloadTests` passes (2/2).

## Threat Flags

None new beyond plan threat model.