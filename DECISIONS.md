# Decisions

First I looked for the replication of the problem, debugging the code and investigated the problem against the specs,after my findings, solved them, then I had to create the tests, but because of the time I asked an AI to help me with the tests for my changes.

## Where the spec was unclear, contradictory, or silent

§3.2 (dedupe attachments by hash) contradicts §3.5 (families must be intact). This is the root cause of INGEST-4471.

    The old code suppressed any document whose hash had been seen, so `vendor-report.pdf` on the forward (DOC-000005) was dropped because the same PDF was on the original (DOC-000003). The forward then arrived with no attachment.
    Decided: §3.5 wins. The unit of dedup is the family, not the attachment. An attachment is only suppressed when its whole family is suppressed. Two different emails that carry the same file both keep it.

§3.4 (dedupe across the whole batch) and §3.3 contradict §4 (each custodian's set must be self-contained).
    §3.3 is explicitly cross-custodian (sender's Sent Items vs recipient's Inbox), so suppressing a copy necessarily removes a document from one custodian's set. The load file has fixed columns, so there is nowhere to record "also held by X".
    
    Decided: follow §3.1–3.4 (global dedupe). **§4 is NOT satisfied**: if `mlee` is severed, her suppressed copies are gone from the production. Example in the sample: DOC-000009/10/11 (mlee's copy of the Q3 email and its attachments) and DOC-000013 (`policy.docx`) are suppressed.
    Considered: dedupe only within a custodian. That satisfies §4 but makes §3.3 a no-op for the very case it describes.
§3.3 does not say what makes two emails "the same". 
    I defined identity as: sent instant (UTC, to the second) + `from` + set of `to` addresses + `subject` + `body`, after normalising.
    - Addresses: lower-cased, display name stripped (`John <j@x.com>` → `j@x.com`), recipients de-duplicated and order-insensitive.
    - Dates: compared as instants, so `14:22-05:00` equals `19:22Z`.
    - Subject: whitespace collapsed. Case and `Re:`/`FW:` are kept, because a reply is a different email.
    - Body: CRLF/CR → LF, trailing whitespace per line and leading/trailing blank space trimmed.
    - Excluded: `sha256`, `fileName`, `custodian`, `sizeBytes` (they vary per mailbox by definition).
## What I changed and why

- `Deduper.cs`: rewritten around families. Resolve each document to its top-level root, bucket families by dedup key (email identity key, or hash for loose files), suppress duplicates as whole families, emit survivors as whole families. This fixes INGEST-4471 and implements §3.3.Also UTC date formatting; `FamilyID` from the real root; deterministic survivor choice and row order.
- `LoadFileWriter.cs`: RFC 4180 quoting, explicit UTF-8 no BOM, streaming write instead of building one giant string.
- `Program.cs`: streams the JSON input; clean error message and exit code 1 on invalid input.

## What I didn't get to

- §4 (custodian self-containment): not satisfiable with the fixed columns; see above. No sidecar yet.it will not match.
- Body normalisation is deliberately conservative: no Unicode normalisation (NFC), no stripping of mail-client footers/disclaimers, no HTML-vs-plain-text reconciliation. Real mailboxes will have copies this misses.
- Emails with no `sentDate` are matched with an empty date; I have not decided whether that is safe.
- Email-in-email (an attached email that is also a top-level email elsewhere) is not de-duplicated against the top-level copy. Family-intact wins over §3.2 there too.

## What I'd do next
- Get the §4 vs §3.3/3.4 answer, then add the sidecar mapping (suppressed DocID → survivor DocID, custodians) without touching the load-file columns.
- Run against real mailbox exports and look at near-misses: same date/from/subject with different bodies, to tune normalisation.

## If you reimplemented in another language

I did not; the tool stays C# / .NET 8 

## Working with the AI

Because of the time I had to ask help to an AI to complete my tests according to the changes I made, I made the one about the INGEST-4471 and the "cross-mailbox email deduplication" and the AI completed other tests, I reviewed and run them succesfully.
