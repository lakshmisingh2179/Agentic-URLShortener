# Executed validation and evidence

Validated on Windows with workspace-local .NET SDK 10.0.401. The machine did not initially have an SDK on PATH. There are no external NuGet dependencies.

## Results

- **Release build passed: zero warnings and errors.** See [build output](build-output.txt).
- **52/52 automated tests passed**, including an actual HTTP server. See [test output](test-output.txt).
- **Both engineering revisions completed** using the included shortener as baseline, with explicit offline simulated approvals.
- For each engineering revision, the unchanged baseline passed **52/52** tests; the regression-only version produced **52/53 with exactly one expected failure**; the patched candidate passed **53/53**, including HTTP integration.
- Requirement-change approval invalidated the old outputs and approvals, preserved the original evidence, and required fresh gates. The superseded candidate source was restored to its archived baseline.
- Audit verification passed. Artifact, patch, source and command-output hashes are supplied for independent inspection.

## Evidence index

| Evidence | File |
|---|---|
| First change: reserve admin | [Diff](evidence/revision-1/change.diff), [patch](evidence/revision-1/patch.json), [commands/results](evidence/revision-1/validation.json) |
| Changed requirement: reserve admin and health | [Diff](evidence/revision-2/change.diff), [patch](evidence/revision-2/patch.json), [commands/results](evidence/revision-2/validation.json) |
| Requirement proposal and invalidation | [Proposal](evidence/requirement-proposal.json), [invalidation snapshot](evidence/invalidation.json) |
| Superseded source restoration | [Restoration check](evidence/superseded-workspace.json) |
| Requirements → artifacts → validation → approvals | [Traceability](evidence/traceability.json), [audit chain](evidence/audit.json) |
| Evidence hashes and audit head | [Manifest](evidence/manifest.json) |
| Reliability measurements | [Report](evidence/reliability.json) |
| Findings-derived tasks and enforced security blocker | [Proposal](evidence/findings/proposal-r1.json), [accepted plan](evidence/findings/accepted-r2.json), [final workflow](evidence/findings/workflow.json) |
| Validated findings-driven engineering change | [Diff](evidence/findings/change.diff), [results](evidence/findings/validation.json) |
| Greenfield acceptance criteria and actual tests | [Workflow](evidence/scenarios/greenfield/workflow.json), [validation](evidence/scenarios/greenfield/validation.json) |
| Ambiguity explicitly blocked then resolved | [Unresolved](evidence/scenarios/ambiguous/unresolved.json), [answers](evidence/scenarios/ambiguous/resolved.json), [validation](evidence/scenarios/ambiguous/validation.json) |
| Separately approved fallback execution | [Proposal](evidence/fallback/proposal.json), [workflow](evidence/fallback/workflow.json) |

Each revision directory also contains the final candidate `LinkService.cs`, regression source, baseline manifest and complete workflow snapshot. The JSON validation reports contain the actual command arguments, exit codes, output and durations; they are not agent assertions.

## Observed reliability sample

The report covers the two-revision engineering run, greenfield validation, clarified ambiguous validation, findings-driven remediation, separately approved fallback, recovery/retry and intentional rollback. Rates, latency, restart recovery, observed failure-to-recovery and sample counts are exported at full precision in the linked report. Wall-clock latency includes environmental execution delays and is not a throughput benchmark. Faults and approvals are explicitly simulated; these are not production SLO measurements or complete service-incident MTTR.

## Tests and disclosures

Six additional regression cases close the fallback restart authorization gap. They verify zero calls and no consumed fallback attempt after changed model, changed agent, missing configuration, or legacy unbound approval; successful execution under the exact persisted identity; and approval of an original proposal after restart refusing a newly configured model. The mismatch is auditable and rollback is enforced.

The [focused source/test diff](fallback-identity-fix.diff) compares this fix with an earlier development revision.

Tests cover URL validation/expiry/collisions/persistence/concurrency, store ownership/atomicity/corruption, graph validation, all three scenarios, gates, parallel joins, retry bounds/backoff, timeout, safe-stop, recovery, compensation, governed proposals, revision limits, unsafe patch rejection, requirement invalidation/re-execution, trace hashes, audit tampering and reliability denominators. New tests cover a critical finding overriding a pass verdict, non-waivable rejected remediation, malformed security output, graph attempts to bypass security, findings proposal persistence, separate fallback approval, bounded fallback failure and mandatory ambiguity answers. HTTP tests verify that operators cannot approve findings or fallback decisions.

The security fixture discovers a missing protected alias and supplies remediation text and a scoped engineering recommendation. The engine derives additional nodes/dependencies from that output, pauses for approval and reruns security and actual patch validation. The fixture is deterministic; it proves orchestration and enforcement rather than independent live-model reasoning.

Live AI calls were not performed; the adapter was tested with a fake HTTP transport. Docker and Linux CI were not run. The tests use a custom C# harness, not VSTest/xUnit. Interactive approvals were not automated; the evidence explicitly labels its offline simulated approvals. Recovery uses injected persisted Running state, not an OS process crash. The executor supports exact reviewed patch templates, not arbitrary model-written code. Greenfield/ambiguous scenarios validate the included implementation, rather than generating new source autonomously. The initial graph is predefined; findings drive its approved expansion. No deployment or live legacy migration occurred.

Reproduce with `scripts/test.ps1` and `scripts/engineering-demo.ps1 -AutoApproveOffline`, or the cross-platform commands in the README. Inspect the ZIP's source and rerun independently.
