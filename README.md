# URL shortener and governed SDLC execution

A governed SDLC prototype with constrained engineering execution, built around a .NET 10 URL shortener. It includes persistent DAG orchestration, role-based agents, offline mode, human approvals, parallel execution, bounded retries, rollback, safe-stop, audit history, reliability metrics and governed requirement changes. It does not provide general requirement-to-code autonomy.

**The brownfield workflow produces a structured patch and executes build/test validation.** The brownfield demo produces a structured patch against the included shortener, then builds and runs real tests in a separate workspace. It demonstrates baseline green → new regression red → implementation green, followed by an upstream requirement change and governed re-execution.

The executor deliberately supports one reviewed capability: reserved-alias changes. Live model output must match the allowed patch edits and hashes. Arbitrary model-written code, project changes, commands and dependencies are rejected. Release remains an approval/checklist, not a deployment.

**Security and adaptation are enforced:** security output must be structured, bound to the current requirement hash, and free of unresolved high/critical blockers. A blocker automatically creates a findings-derived remediation plan and pauses the run. Human acceptance adds the agent-described remediation tasks, invalidates old outputs/approvals, and requires another security review; rejection rolls back. Malformed output fails closed. The initial graph remains a predefined scaffold, not model-generated task decomposition.

## Inspect the code and actual evidence

- [Engineering executor](src/Shortener.Core/EngineeringAgent.cs), [patch policy](src/Shortener.Core/PatchPolicy.cs), [governance](src/Shortener.Core/WorkflowService.cs), [scheduler](src/Shortener.Core/Engine.cs).
- [Revision 1 diff](docs/evidence/revision-1/change.diff), [revision 2 diff](docs/evidence/revision-2/change.diff).
- [Revision 1 build/test results](docs/evidence/revision-1/validation.json), [revision 2 results](docs/evidence/revision-2/validation.json).
- [Invalidation snapshot](docs/evidence/invalidation.json), [audit](docs/evidence/audit.json), [traceability](docs/evidence/traceability.json), [reliability](docs/evidence/reliability.json).
- [Findings-driven proposal](docs/evidence/findings/proposal-r1.json), [resulting diff](docs/evidence/findings/change.diff), [validation](docs/evidence/findings/validation.json).
- [Greenfield scope and artifacts](docs/evidence/scenarios/greenfield/workflow.json), [actual validation](docs/evidence/scenarios/greenfield/validation.json).
- [Unresolved ambiguity](docs/evidence/scenarios/ambiguous/unresolved.json), [recorded answers](docs/evidence/scenarios/ambiguous/resolved.json), [validation](docs/evidence/scenarios/ambiguous/validation.json).
- [Fallback proposal](docs/evidence/fallback/proposal.json), [fallback execution and identity](docs/evidence/fallback/workflow.json).
- [Validation report](docs/VALIDATION.md), [architecture and limitations](docs/ARCHITECTURE.md).


## Build and test

Install the .NET 10 SDK, then run from this directory:

```powershell
./scripts/test.ps1
```

Linux/macOS: `sh scripts/test.sh`. The scripts build Release with warnings as errors and run a custom C# assertion harness, including an actual HTTP server. No NuGet packages are required. **This is not an xUnit/VSTest project; `dotnet test` does not discover these tests.** Failed assertions return a nonzero exit code.

## Execute the brownfield change

```powershell
# Interactive requirements, execution, release and change approvals:
./scripts/engineering-demo.ps1

# Explicitly simulated approvals, restricted to offline mode:
./scripts/engineering-demo.ps1 -AutoApproveOffline
```

Cross-platform, after building:

```sh
export DOTNET_HOST_PATH="$(command -v dotnet)"
export AGENT_MODE=offline
dotnet tools/EngineeringDemo/bin/Release/net10.0/EngineeringDemo.dll \
  "$PWD" /tmp/shortener-engineering "$PWD/evidence-local/run" --auto-approve-offline
```

Use fresh workspace/evidence directories for independent runs. Revision 1 reserves `admin` case-insensitively for new aliases while preserving existing stored aliases and clicks. The agent produces edits to `LinkService.cs` and a regression in `tests/Shortener.Tests/Program.cs`. After execution approval, the executor builds/tests the baseline, requires the new regression to fail, applies the implementation and requires the entire suite to pass. The baseline checkout is never changed.

After revision 1 is approved, the demo proposes reserving `health` too. Human change approval archives prior evidence, invalidates downstream outputs and approvals, restores the superseded workspace, and executes revision 2 behind fresh gates. The validated candidate source, diff, hashes and command results are exported. Applying that candidate to a deployment remains a separate release action.

The expanded demo also validates the greenfield deliverable against explicit acceptance criteria and demonstrates that an ambiguous run cannot start until creation, retention and alias-policy questions have recorded supported answers. These scenarios compile and test the included from-scratch implementation; they do not claim that a model independently synthesized its source.

An offline security fixture detects an unprotected `health` alias and recommends concrete work. The scheduler derives remediation nodes from that finding, requests approval and validates the resulting patch in a new revision. This fixture is deterministic; live security reports use the same schema and policy but were not exercised here.

The demo separately injects an interrupted attempt, transient and permanent errors, and a primary-agent outage. After primary retries are exhausted, one fallback attempt requires its own approval; rejection or fallback failure rolls back. These are explicitly simulated faults and approvals, not production reliability measurements.

## Start the API

```powershell
$env:OPERATOR_KEY = 'local-operator-change-me'
$env:APPROVER_KEY = 'local-approver-change-me'
$env:AGENT_MODE = 'offline'
$env:ASPNETCORE_URLS = 'http://localhost:8080'
dotnet run --project src/Shortener.Api -c Release --no-build
```

Use distinct random secrets for shared environments. `DATA_DIR` defaults to `data`. The new hash-linked audit format rejects legacy unsealed prototype stores; preserve any old store and use a fresh directory.

```powershell
$op = @{ 'X-Api-Key' = $env:OPERATOR_KEY }
Invoke-RestMethod http://localhost:8080/api/links -Method Post -Headers $op `
  -ContentType application/json -Body '{"url":"https://example.com/docs","alias":"docs"}'
```

`GET /s/docs` returns 302 and durably counts the click. Missing/expired codes return 404. Destinations must be absolute HTTP(S), without embedded credentials/control characters. Aliases are case-sensitive and 4–32 URL-safe characters; expiry must be future. Generated codes use 48 random bits with collision checking. The response returns a relative `shortPath`.

## Workflow/control routes

All `/api/*` and `/metrics` routes require `X-Api-Key`. Both roles may read; operators control work and approvers decide gates. Health, landing page and redirects are public. Limits: 128 KiB request bodies, 120 requests/minute/IP.

| Route | Body/purpose |
|---|---|
| `POST /api/runs` | `{scenario, context, engineering?}` |
| `GET /api/runs` or `/api/runs/{id}` | State, graph, artifacts, history |
| `POST /api/runs/{id}/approval` | Approver: `{nodeId, revision, accept, reason}` |
| `POST /api/runs/{id}/stop` or `/rollback` | Operator; no body |
| `POST /api/runs/{id}/replan` | Operator: `{nodes, reason, revision}` for text-mode unstarted work |
| `POST /api/runs/{id}/plan-decision` | Approver: `{revision, accept, reason}` |
| `POST /api/runs/{id}/requirement-change` | Operator: `{context, engineering, reason, revision}` |
| `POST /api/runs/{id}/requirement-decision` | Approver: `{revision, accept, reason}` |
| `POST /api/runs/{id}/findings-decision` | Approver: `{revision, accept, reason}`; no blocker waiver |
| `POST /api/runs/{id}/fallback-decision` | Approver: `{revision, accept, reason}`; one alternate attempt |
| `POST /api/runs/{id}/clarifications` | Operator: `{revision, answers:{creation:"operator-only",retention:"optional-expiry",aliases:"case-sensitive"}}` |
| `GET /api/runs/{id}/audit` | Linked decision/execution records |
| `GET /api/audit-integrity` | Chain verification and current head |
| `GET /api/reliability` and `/metrics` | Reliability report and Prometheus metrics |

For API engineering execution configure absolute `ENGINEERING_BASELINE`, `ENGINEERING_ROOT` and `DOTNET_HOST_PATH`, and create a run with `engineering: {"id":"REQ-ALIAS-001","reservedAliases":["admin"]}`. For actual baseline validation without a patch, set `validateBaseline:true`. Unconfigured execution requests are rejected. Early, duplicate and stale gate decisions return 409. The security node and its dependency before tests cannot be removed by a replan.

The original `scripts/demo.ps1 -Scenario greenfield|brownfield|ambiguous` scripts remain review-only scenarios. Use `engineering-demo.ps1` for the actual brownfield engineering execution. Ambiguous scope is gated before analysis.

## Live agents and Docker

Live mode requires `AGENT_MODE=live`, `OPENAI_API_KEY` and `OPENAI_MODEL`. Context and dependency artifacts are sent to the Responses API and incur usage costs. The engineering wrapper validates returned JSON against the exact approved patch capability before any execution. No live provider call was made during validation; the HTTP adapter was tested with a fake transport.

Fallback is disabled by default. Set `OPENAI_FALLBACK_MODEL` to a different model for live mode; set `ENABLE_OFFLINE_FALLBACK=true` only for an offline alternate. Live failures never silently downgrade to demo output. Approval persists the proposed agent and model identity on the node. Before dispatch, including after restart, the engine requires an exact match with the configured fallback. A changed/missing configuration or legacy unbound approval makes no fallback call, records `fallback-identity-mismatch`, and rolls back; it never transfers approval to the replacement. The same security/validation policies still apply.

```sh
cp .env.example .env
# Edit distinct role keys, then:
docker compose up --build
# Separate SDK-equipped offline engineering runner:
docker compose -f compose.engineering.yaml run --build --rm engineering-demo
```

The API runtime is non-root and persists data in a volume. The separate engineering runner disables runtime networking and constrains capabilities, memory and processes. Docker and Linux CI are supplied but were not executed here. The API runtime image alone has no SDK.

This remains a single-process prototype with whole-file JSON persistence, template-constrained engineering, role keys rather than individual identities, and no deployment or general-purpose code sandbox. Audit hashes detect corruption but cannot defeat a disk administrator who rewrites the chain. See the architecture for precise guarantees.

This prototype follows the supplied interview assignment. No legacy repository was provided; the brownfield scenario uses the included shortener as its baseline.
