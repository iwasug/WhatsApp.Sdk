---
phase: 06-media-pipeline
verified: 2026-10-15T03:43:23Z
status: gaps_found
score: 3/10 must-haves verified
covered_files:
  - .planning/phases/06-media-pipeline/06-01-PLAN.md
  - .planning/phases/06-media-pipeline/06-01-SUMMARY.md
  - .planning/phases/06-media-pipeline/06-02-PLAN.md
  - .planning/phases/06-media-pipeline/06-02-SUMMARY.md
  - .planning/phases/06-media-pipeline/06-03-PLAN.md
  - .planning/phases/06-media-pipeline/06-03-SUMMARY.md
  - NOTICE.md
  - README.md
  - src/Iwasug.WhatsApp.Sdk/Media/MediaConn.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaCrypto.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaKind.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaMetadata.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaUploader.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/message.proto
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaCryptoFixtureTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaDownloadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryCryptoTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSafetyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSchemaTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSendTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaTracerTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaUploadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/media-crypto-fixtures.json
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/media-retry-fixtures.json
covered_digest: "v3:sha256:2b7777ea12b6a98fb5e0110a9eb7d79658ce5729b588218425dc9b0588478c00"
behavior_unverified: 2
overrides_applied: 0
gaps:
  - truth: "Host downloads inbound media as a verified Stream; hostile media fails closed (MED-02 receive path)"
    status: failed
    reason: "MediaDownloader and MediaMetadata exist as internal classes but are never invoked from the receive pipeline. MessageDispatcher parses only Message.Conversation (and SKDM) after E2EE decrypt; there is no imageMessage/documentMessage/audioMessage branch, MediaDownloader is never constructed by the dispatcher or Messenger, and the MediaReceivedEvent record is defined but never raised anywhere in src/. A host has no code path to receive or download inbound media."
    artifacts:
      - path: "src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs"
        issue: "Lines 190-229: after proto parse, only the SKDM branch and Conversation text are handled; no media branch exists. MediaDownloader/MediaReceived references: zero."
      - path: "src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs"
        issue: "ORPHANED: only referenced by its own unit test; no production caller."
      - path: "src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs"
        issue: "MediaReceivedEventArgs/MediaReceivedEvent defined but never constructed or raised in src/ (confirmed by 06-REVIEW.md finding at MediaDownloader.cs:51-77 follow-up note)."
    missing:
      - "MessageDispatcher media branch: parse imageMessage/documentMessage/audioMessage, validate via MediaMetadata, ack-before-download, verify-then-decrypt via MediaDownloader, raise MediaReceived event with Stream handle (order: persist → ack → download → event → cache)."
      - "Public/host-visible surface for inbound media (event or stream-twin subscription wired to the dispatcher)."
      - "MediaDownloadTests facts exercising the dispatcher branch over the loopback harness (currently only a downloader URL-construction unit test)."
  - truth: "Expired download URLs recover host-invisibly through capped server-error plus rmr retry with fresh-path re-download (MED-02 retry flow)"
    status: failed
    reason: "MediaRetryTracker exists (capped at 3, hourly eviction) but has no production consumer. No server-error receipt sender exists (grep 'server-error|rmr|ServerError' in ReceiptHandler.cs: zero matches); no rmr-presence check or media-retry routing exists in MessageDispatcher's receipt path (only ReceiptKind.Retry → Pattern-5 MSG-06 at lines 605-612). The server-error/rmr exchange and fresh-path recovery are entirely unimplemented; MediaRetryNotification/ServerErrorReceipt proto messages exist but are never sent or parsed."
    artifacts:
      - path: "src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs"
        issue: "ORPHANED: referenced only by MediaRetryTests/MediaSafetyTests; never constructed by dispatcher, receipt handler, or messenger."
      - path: "src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs"
        issue: "No SendServerErrorReceiptAsync / EmitServerErrorReceiptAsync / SendMediaRetryReceiptAsync implementation, despite SendPathAuditTests.cs:56-58 whitelisting those method names."
      - path: "src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs"
        issue: "Receipt dispatch (lines 605-612) has no rmr-child presence test before type=retry; no media-retry handler exists."
    missing:
      - "server-error receipt construction (enc_p/enc_iv GCM payload with retry key, rmr child with from_me/participant)."
      - "rmr-presence routing in HandleReceipt before Pattern-5 retry handling."
      - "Fresh directPath re-download wired to MediaRetryTracker cap with typed expiry-exhausted terminal."
      - "MediaRetryTests facts driving the loopback with a fake rmr peer (current file only tests the counter constants)."
  - truth: "Send-path audit proves every new public send is gated (06-02 must-have)"
    status: failed
    reason: "The plan required a gated-caller reflection fact enumerating the seven public media send methods and asserting each references ISafetyGate.ExecuteSendAsync. SendPathAuditTests.cs contains only the pre-existing SendNodeAsync egress inventory; no gated-caller reflection fact exists. Additionally MediaSafetyTests contains no refusal zero-HTTP / zero-consumed-id facts for the six media sends (only a mimetype rejection and the MaxRequestsPerId constant). The structural gating is present in code (all six sends wrap upload inside ExecuteSendAsync), but the audit proof the plan promised is absent, so a future ungated edit would not fail loudly."
    artifacts:
      - path: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs"
        issue: "Single fact (egress inventory); the required 'every public media send calls ExecuteSendAsync' reflection fact was never added."
      - path: "tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSafetyTests.cs"
        issue: "15 lines; no refusal facts for the six media sends; no oversize SafetyRuleViolationException facts per kind at the Messenger level."
    missing:
      - "Reflection fact asserting each of SendImageAsync/SendDocumentAsync/SendVoiceNoteAsync/SendGroupImageAsync/SendGroupDocumentAsync/SendGroupVoiceNoteAsync IL-references ISafetyGate.ExecuteSendAsync."
      - "Refusal facts proving zero stub-HTTP calls and zero consumed message ids per media send API."
  - truth: "One image travels host Stream -> gated E2EE send -> receive branch -> MediaReceived bytes equal original, through the public SendImageAsync API (06-01 tracer truth)"
    status: failed
    reason: "The send half exists and is gated, but the receive half of the tracer round-trip was never implemented: there is no dispatcher media branch and MediaReceived is never raised, so the asserted end-to-end equality cannot occur. MediaTracerTests.cs (17 lines) tests only an in-process MediaCrypto.Encrypt/VerifyAndDecrypt round-trip — no stubbed-HTTP loopback, no gate, no node-attr assertions."
    artifacts:
      - path: "tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaTracerTests.cs"
        issue: "Contains a single crypto round-trip fact; the planned gated send/receive loopback fact with byte-equality on MediaReceived does not exist."
      - path: "src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs"
        issue: "Event records are internal and unreferenced by the dispatcher; the plan's public event surface was not delivered."
    missing:
      - "Loopback tracer fact: SendImageAsync over stubbed mmg handler → E2EE send → scripted peer → stub GET → MediaReceived event content equality."
      - "Dispatcher media receive branch (see gap 1) as the receiving half of the tracer."
deferred: []
behavior_unverified_items:
  - truth: "Document and voice-note sends carry correct node attrs; group image fanout rides the sender-key path with mediatype on the skmsg (06-02 truth 1)"
    test: "Send a document and voice note over the loopback harness; send a group image and inspect the signed skmsg."
    expected: "Document node decrypts to DocumentMessage with fileName; voice note carries ptt=true + seconds + mediatype audio; skmsg enc child carries mediatype image; SKDM distributed once per device."
    why_human: "The code paths exist and are wired (Messenger.cs:251-341 → MessageSender.SendMediaAsync/SendGroupMediaAsync with mediatype on enc/skmsg children), but MediaSendTests only asserts via reflection that the six method names exist — no behavioral fact exercises attr mapping or fanout, so correct wire attrs are present-but-unproven."
  - truth: "Upload resolves media-conn over the IQ seam with TTL refresh and host rotation; refused sends never reach the network (06-02 truth 2)"
    test: "Force a transport failure during upload with a fake conn provider; observe host rotation and second-attempt byte identity. Refuse a send through a refusing gate and assert zero HTTP."
    expected: "Force-refresh returns rotated hosts; retry re-uploads identical bytes; refusing gate yields no HTTP call and no consumed id."
    why_human: "MediaConn TTL cache, force-refresh, and uploader rotation logic are present and wired (MediaConn.cs:27-88, MediaUploader.cs:80-153), and the gate-wrap structurally guarantees upload-after-admission, but no test exercises rotation/TTL-expiry/refusal-zero-HTTP — the canned-snapshot MediaUploadTests only cover request mapping and oversize."
coincidental_reliance_items: []
human_verification:
  - test: "MVP mode goal-format check: Phase 6 is marked Mode: mvp in ROADMAP.md but the goal 'Host exchanges image, document, and voice-note media through the same gated send path.' is NOT in 'As a…, I want to…, so that…' form (user-story.validate returned invalid). Plan objectives carry proper user stories; the roadmap goal does not."
    expected: "Either run /gsd-mvp-phase 6 to reformat the roadmap goal as a user story, or explicitly accept the outcome-form goal and rely on plan-objective stories."
    why_human: "Verifier policy forbids verifying against a non-User-Story goal under MVP mode; the reformat-vs-accept decision is a human call. This did not block the goal-backward verification below, which used the roadmap success criteria and plan-objective user stories."
  - test: "Live send trio: link a real device, send an image, a document, and a voice note via SendImageAsync/SendDocumentAsync/SendVoiceNoteAsync."
    expected: "All three open intact on the peer phone with caption/fileName/ptt metadata."
    why_human: "06-UAT.md marks this NOT VERIFIED (no device available); loopback tests cannot substitute for live wire behavior."
  - test: "Expire and recover live: allow a download URL to expire (or simulate via proxy), then trigger an inbound media download."
    expected: "One server-error receipt goes out, an rmr with a fresh direct_path arrives, media re-downloads and delivers with no host code involved."
    why_human: "Requires a live peer and the (currently unimplemented — see gap 2) retry exchange; cannot be verified programmatically."
  - test: "Wire-attr correctness of media nodes against a real peer: send each media kind and confirm the receiving WhatsApp client renders the right type label (image/document/ptt)."
    expected: "Peers render each media kind with correct type; voice notes show as push-to-talk."
    why_human: "Node attr semantics (type=media, mediatype tokens, ptt) can only be confirmed against the real WhatsApp client; in-repo tests only check builder output."
---

# Phase 6: Media Pipeline Verification Report

**Phase Goal:** Host exchanges image, document, and voice-note media through the same gated send path.
**Verified:** 2026-10-15T03:43:23Z
**Status:** gaps_found
**Re-verification:** No — initial verification

## MVP Mode Note

Phase 6 is flagged `Mode: mvp` in ROADMAP.md, but the roadmap goal is not in user-story form (`user-story.validate` → invalid). Per MVP policy the verifier surfaces this rather than refusing all verification: the User Flow Coverage section below is derived from the three plan-objective user stories (which are correctly formatted) and the roadmap success criteria. Reformat decision routed to human verification.

## User Flow Coverage

User stories (plan objectives): «As a host, I want to send and receive images through the same safety-gated send path as text» (06-01), «…send documents and voice notes (and images to groups) through the same gated path» (06-02), «…receive images, documents, and voice notes (including recovery from expired download URLs with no code on my side)» (06-03).

| Step | Expected | Evidence | Status |
|------|----------|----------|--------|
| Send image via public API | `Messenger.SendImageAsync` uploads inside the gate and sends a `type=media` node with `mediatype=image` | Messenger.cs:230-248 (gate-wrap, upload inside delegate); MessageSender.cs:227-270 (`SendMediaAsync`, mediatype on enc children) | ✓ |
| Send document / voice note | Gated sends with fileName / ptt+seconds metadata | Messenger.cs:251-286; MessageBuilder.cs:200-297 (three builders) | ✓ (attrs present, behavior-unverified) |
| Group media fanout | One signed skmsg with mediatype, pairwise SKDM once per device | MessageSender.cs:275-287 + skmsgAttrs mediatype at line 468 | ✓ (present, behavior-unverified) |
| Refused send touches nothing | Refusing gate → zero HTTP, zero consumed ids | Gate-wrap structurally guarantees it; no refusal fact exists (see gap 3) | ⚠️ |
| Receive inbound image/document/voice | Dispatcher media branch → verified download → MediaReceived event with equal bytes | No media branch in MessageDispatcher (lines 190-229 handle only SKDM + Conversation); MediaReceivedEvent never raised | ✗ |
| Expire-and-recover without host code | 403/404/410 → server-error receipt → rmr fresh path → re-download → event | No server-error sender, no rmr routing, tracker orphaned (see gap 2) | ✗ |
| Crypto fixtures gate CI | Golden hash/MAC fixtures assert byte-exact expansions + tamper fail-closed | TestData/media-crypto-fixtures.json + MediaCryptoFixtureTests (ran green) | ✓ |
| **Outcome** — "rich customer conversations without risking the account" | Full send+receive exchange through the gated path | Send half delivered; receive half absent — goal NOT achieved | ✗ |

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Media encrypt/decrypt round-trips match golden hash/MAC fixtures; tampered blobs fail closed on all three verifiers | ✓ VERIFIED | `dotnet test --filter Media*` → 23/23 pass; MediaCryptoFixtureTests asserts expansion/blob/hash hex equality plus MAC-flip/ciphertext-flip/wrong-info/short-blob fail-closed; MediaCrypto.VerifyAndDecrypt enforces enc-hash → FixedTimeEquals MAC → plain-hash order |
| 2 | message.proto carries imageMessage=3, documentMessage=7, audioMessage=8 with text numbers 1/2/6/35 unchanged; no video/sticker/link-preview branches | ✓ VERIFIED | message.proto:132-135 (branches), :9-10 (pinned baselines); repo-wide grep for videoMessage/stickerMessage/linkPreview/viewOnce in src/ → zero matches |
| 3 | One image travels host Stream → gated E2EE send → ack → receive branch → MediaReceived bytes equal original via SendImageAsync | ✗ FAILED | Send half wired (Messenger.cs:230-248); receive half does not exist (no dispatcher media branch, MediaReceived never raised); MediaTracerTests.cs is a 17-line crypto-only fact — see gap 4 |
| 4 | Every new public media symbol declared in PublicAPI.Unshipped.txt; build warning-free | ✓ VERIFIED | PublicAPI.Unshipped.txt:297-302 (six send methods), :927-930 (MediaKind); `dotnet build` → 0 warnings, 0 errors |
| 5 | Host sends document + voice note with correct attrs; group fanout rides sender-key path with mediatype on skmsg | ⚠️ PRESENT_BEHAVIOR_UNVERIFIED | Code wired (Messenger.cs:251-341, MessageSender.cs:275+); MediaSendTests asserts only method existence via reflection — no behavioral fact exercises attrs/fanout |
| 6 | Upload resolves media-conn over IQ seam with TTL refresh + host rotation; refused sends never reach network | ⚠️ PRESENT_BEHAVIOR_UNVERIFIED | MediaConn TTL/single-flight/force-refresh present and wired into Messenger's uploader (Messenger.cs:77-81); rotation loop in MediaUploader.cs:84-153; no test exercises rotation/TTL/refusal |
| 7 | Byte caps enforced before encryption; send-path audit proves every public send gated | ✗ FAILED | Caps verified (Uploader ReadAllAsync cap check pre-encrypt; MediaUploadTests.OversizeContentThrowsBeforeUpload green; SafetyOptions.cs:67-76 defaults). Audit reflection fact absent — see gap 3 |
| 8 | Host downloads inbound media as verified Stream; tampered/oversize/maliciously-named media fails closed | ✗ FAILED | MediaDownloader + MediaMetadata exist and unit-test sanely, but are orphaned: no production caller, no dispatcher branch — see gap 1 |
| 9 | Expired download URLs recover host-invisibly via capped server-error + rmr retry | ✗ FAILED | MediaRetryTracker orphaned; no server-error sender in ReceiptHandler; no rmr routing in MessageDispatcher — see gap 2 |
| 10 | Every media send refuses through the gate with zero network; phase docs close the phase | ✗ FAILED | Docs half verified (06-UAT.md exists with honest NOT VERIFIED live scenarios; NOTICE.md:65 Phase 6 Baileys provenance row; README.md:126-130 four media ledger additions). Refusal proof half absent — MediaSafetyTests has no refusal facts (see gap 3) |

**Score:** 3/10 truths verified (2 present, behavior-unverified)

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
| ----------- | ---------- | ----------- | ------ | -------- |
| MED-01 | 06-01, 06-02 | Host can send image, document, and voice note | ✓ SATISFIED | Six public gated send APIs (Messenger.cs:230-341) with upload inside ExecuteSendAsync; MediaUploader → MediaConn IQ seam; proto branches + builders; caps. Caveat: wire-attr correctness behaviorally unproven (truth 5/6 ⚠️) |
| MED-02 | 06-03 | Host can receive/download media incl. download-expiry handling and media-retry flow | ✗ BLOCKED | No dispatcher media branch, MediaReceived never raised, MediaDownloader/MediaRetryTracker orphaned, no server-error/rmr exchange (gaps 1, 2, 4) |

No orphaned requirements: MED-01 and MED-02 are both claimed by phase-6 plans.

### Deferred Items

None. Step 9b: no later phase (7 Multi-Account, 8 Broadcast/Extended, 9 Ship) covers media receive wiring, the media-retry exchange, or the gated-send audit; these are real phase-6 gaps, not scheduled future work.

### Advisory (New Scope, Unevidenced)

Not applicable — initial verification, re-verification-only section.

### Required Artifacts

| Artifact | Expected | Status | Details |
| -------- | -------- | ------ | ------- |
| `src/.../Media/MediaCrypto.cs` | HKDF-112 envelope, triple-gate verify | ✓ VERIFIED | Exists, substantive, wired (uploader + downloader + tests) |
| `src/.../Media/MediaKind.cs` | Public enum + MIME allow-lists | ✓ VERIFIED | Wired into Messenger/uploader/builder paths |
| `src/.../Proto/message.proto` media branches | image/document/audio + retry messages | ✓ VERIFIED | Lines 70-135, 119-126; additive only |
| `TestData/media-crypto-fixtures.json`, `media-retry-fixtures.json` | Frozen golden vectors | ✓ VERIFIED | Exist, copied to output, asserted by green tests |
| `src/.../Media/MediaConn.cs` | IQ TTL cache + host rotation | ✓ VERIFIED | Wired via Messenger.cs:77-81; behavior-unverified (truth 6) |
| `src/.../Media/MediaUploader.cs` | Conn-backed upload inside gate | ✓ VERIFIED | Constructed by Messenger, invoked only inside gate delegates |
| `src/.../Media/MediaDownloader.cs` | Verified streaming download | ⚠️ ORPHANED | Exists + unit-tested, zero production callers |
| `src/.../Media/MediaMetadata.cs` | Receive-side fail-closed validation | ⚠️ ORPHANED | Only consumer is the orphaned downloader |
| `src/.../Media/MediaRetryTracker.cs` | Capped per-msg-id retry counter | ⚠️ ORPHANED | Zero production consumers |
| `src/.../Media/MediaEvents.cs` | MediaReceived event surface | ⚠️ HOLLOW | Records defined, never raised, internal-only (plan promised public declarations) |
| `tests/.../SendPathAuditTests.cs` | Egress inventory + gated-caller fact | ✗ STUB (relative to plan) | Only the pre-existing inventory fact; promised reflection fact missing |
| `.planning/phases/06-media-pipeline/06-UAT.md` | Live scenarios, honest NOT VERIFIED | ✓ VERIFIED | Present, honest about device availability |

### Key Link Verification

| From | To | Via | Status | Details |
| ---- | --- | --- | ------ | ------- |
| Messenger media APIs | ISafetyGate.ExecuteSendAsync → MediaUploader → MessageSender | gate-wrapped delegate, upload inside | ✓ WIRED | Messenger.cs:239-341; all six sends wrap upload inside ExecuteSendAsync |
| MediaConn IQ seam | TTL cache → uploader host selection | Func snapshot provider + force-refresh | ✓ WIRED | Messenger.cs:77-81 constructs MediaConn-backed uploader; MediaUploader.cs:80-102 |
| SendPathAuditTests reflection | every public media send calls ExecuteSendAsync | reflection IL scan | ✗ NOT_WIRED | Fact never written (gap 3) |
| MessageDispatcher receipt path | rmr check → media-retry handler | rmr-presence test before type=retry | ✗ NOT_WIRED | HandleReceipt only dispatches ReceiptKind.Retry to Pattern-5 (MessageDispatcher.cs:605-612) |
| MediaRetryTracker cap | server-error receipt → fresh path → downloader → event | retry exchange chain | ✗ NOT_WIRED | No server-error sender exists; tracker orphaned (gap 2) |
| RecentMessageEntry.ResendPayload | retry correlator parses mediaKey | cached payload re-parse | ✗ NOT_WIRED | No media-retry correlator exists (part of gap 2) |
| MessageDispatcher media branch | MediaDownloader → verify → MediaReceived | receive pipeline | ✗ NOT_WIRED | No media branch exists (gap 1) |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
| -------- | ------------- | ------ | ------------------ | ------ |
| SendImageAsync pipeline | MediaUploadResult | Live mmg POST via MediaUploader → MediaConn IQ | Yes — real HTTP with server-returned url/direct_path mapped into the proto builder | ✓ FLOWING |
| MessageBuilder media builders | MediaUploadResult | Uploader output (real mediaKey/hashes) | Yes — real crypto material flows into ImageMessage/DocumentMessage/AudioMessage | ✓ FLOWING |
| MediaReceived event | Stream Content | — | No producer: dispatcher never raises the event | ✗ DISCONNECTED |
| MediaDownloader output | plaintext Stream | — | No production caller invokes DownloadAsync | ✗ DISCONNECTED |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
| -------- | ------- | ------ | ------ |
| Warning-free build | `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` | Build succeeded, 0 warnings, 0 errors | ✓ PASS |
| Media test filters green | `dotnet test --filter "FullyQualifiedName~Media"` | 23/23 passed | ✓ PASS |
| Send-path audit green | `dotnet test --filter "FullyQualifiedName~SendPathAudit"` | 1/1 passed (inventory only — fact count confirms gap 3) | ✓ PASS (with caveat) |
| Crypto fixture round-trip + tamper fail-closed | via MediaCryptoFixtureTests in the 23-test run | pass | ✓ PASS |
| No v2 media surface anywhere | grep `videoMessage\|stickerMessage\|linkPreview\|viewOnce` in src/ | zero matches | ✓ PASS |
| Full-suite regression | `dotnet test Iwasug.WhatsApp.Sdk.sln --no-build` (single full run, no `--nologo` per IN-10 quirk) | 622 total: 618 passed, 4 failed — all 4 are the documented pre-existing PairingLifecycleTests environmental timeouts; zero media failures | ✓ PASS (no new failures vs. 4-test baseline) |

Note: the full suite requires the documented `--nologo`-free invocation (IN-10 toolchain quirk recorded in the test csproj: passing `--nologo` to `dotnet test` exits 5 with "Zero tests ran"). The 4 failures match the accepted pre-existing environmental PairingLifecycleTests timeout baseline recorded in 06-02-SUMMARY; no media regression.

### Probe Execution

No probes declared for this phase — N/A.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
| ---- | ---- | ------- | -------- | ------ |
| tests/.../Media/MediaSchemaTests.cs | 12, 15 | Tautological asserts (`Assert.Equal(1, 1)`, `Assert.True(true)`) inside the branch-number fact | ⚠️ Warning | The "baseline stability" and "field number pin" claims asserted by the fact are not actually pinned; real branch round-trips are checked but the number-pinning/scope-fence prose is unbacked. Schema itself is correct per direct proto inspection. |
| tests/.../Safety/SendPathAuditTests.cs | 56-58 | Whitelist rows for methods that do not exist (`SendServerErrorReceiptAsync`, `EmitServerErrorReceiptAsync`, `SendMediaRetryReceiptAsync`) | ⚠️ Warning | Pre-authorizes egress sites that were never implemented (the 06-03 retry senders); harmless today but silently reserves ungated-looking capacity for future methods |
| src/.../Media/MediaDownloader.cs | 18-22 | `ExpiredMediaDownloadException` "Compatibility alias for hidden test naming" | ℹ️ Info | Dead alias class kept for hypothetical test naming; no current test references it |
| src/.../Media/MediaUploader.cs | 166-182 | Full-stream buffering into MemoryStream despite "streaming encrypt" plan language | ℹ️ Info | Bounded by byte caps (16/64 MB) so not unbounded; noted as deviation from D-63 streaming design |
| src/.../Messaging/Messenger.cs | 229-327 | XML doc on six public media APIs is `Sends media. See plan 06-02.` | ⚠️ Warning | Public API surface shipped to hosts carries no contract documentation (parameters, exceptions, stream ownership) |

No debt markers (`TBD`/`FIXME`/`XXX`) found in any phase-modified file.

### Human Verification Required

See `human_verification` frontmatter — 4 items: MVP goal-format decision, live send trio, expire-and-recover live (blocked on gap 2), and live wire-attr rendering.

### Gaps Summary

The phase achieved its send-side goal but not its receive-side goal. **MED-01 is satisfied**: six public send APIs traverse `ISafetyGate.ExecuteSendAsync` with upload inside the gate, over a real media-conn IQ seam with caps, all pinned by green tests and a warning-free build. **MED-02 is blocked**: the entire receive pipeline is missing. `MessageDispatcher` handles only SKDM and conversation text after E2EE decrypt — there is no media branch, `MediaReceived` is never raised, and `MediaDownloader`/`MediaMetadata`/`MediaRetryTracker` are unit-tested orphans with zero production callers. The server-error + rmr media-retry exchange has no sender, no routing, and no correlator. Additionally, the 06-02 audit proof (reflection fact pinning every media send to the gate) and the 06-03 refusal zero-egress facts were never written, so the SAF-01 regression invariant over the media surface rests on code structure alone. These gaps are self-consistent with the honest deviations recorded in 06-01-SUMMARY (tracer receive half deferred) and 06-03-SUMMARY ("full dispatcher/receipt-handler wiring remains future work") — the SUMMARYs did not overclaim, but the phase goal is not achieved. Four focused gap groups are structured in frontmatter for `/gsd-plan-phase --gaps`.

---

_Verified: 2026-10-15T03:43:23Z_
_Verifier: the agent (gsd-verifier)_
