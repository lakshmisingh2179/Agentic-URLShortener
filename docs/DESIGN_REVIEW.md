# Design Review and Improvements

| Review concern | Implemented change | Remaining boundary |
|---|---|---|
| Security text did not enforce quality | Schema/hash checks; open high/critical findings block tests and release; malformed reports fail closed; rejection cannot waive blockers | A model can miss a vulnerability or incorrectly report resolution; this is not a full scanner |
| Findings did not drive adaptation | Findings produce remediation task content, additional DAG nodes/dependencies and optional supported scope changes; source-hash-bound approval starts a fresh review/validation revision | Initial decomposition and topology rules remain predefined |
| Generic greenfield/ambiguous evidence | Concrete acceptance contracts, recorded answers, blocked unresolved approval, actual clean-copy builds/HTTP tests and acceptance-to-test mappings | Validates the included implementation rather than synthesizing an independent greenfield project |
| No fallback route | Primary retries exhausted → paused proposal → explicit alternate-agent/model approval → one bounded attempt; provenance and metrics persist | No silent live-to-offline downgrade; alternate configuration must remain available |
| Fallback approval could authorize a different model after restart | Persist exact proposed agent/model on the node; verify before dispatch and invocation; mismatch is audited and rolls back without calling the replacement | Binding covers configured identity strings, not remote model weights |
| MTTR only covered restart | Added observed first-failure-to-success timing, including retries and approval wait, with recovered-sample and fallback counts | Neither measure represents whole-service incident MTTR or includes undetected outage time |
| Coding capability too scripted | Kept exact reserved-alias patch capability and documented its boundaries | Live mode still returns a pre-authorized patch template; general code generation remains outside scope |

The included expanded offline demo exercises these transitions and produces inspectable source diffs, command results, scopes, plans, decisions and hashes. Simulated approvals, finding fixtures and injected faults are labeled. No live-model or Docker verification is claimed. See VALIDATION.md and evidence/manifest.json for the executed evidence.
