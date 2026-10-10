# Plan D: Elevation Worker

1. Implemented Core elevation contracts for bounded JSON jobs/results and a testable `IJobTransport`.
2. Implemented reviewed-plan SHA256, GUID job directory, absolute target/payload/backup containment, reparse-point and protected-path checks.
3. Implemented Windows `runas` transport with UAC cancellation mapped to `Declined`.
4. Implemented `Deployer.Worker` with strict arguments, existing `DeploymentExecutor`, owned cancel marker and atomic result JSON.
5. Added focused tests for checksum/path policy, replay, cancellation, crash/missing result, recovery-manifest binding and fake transport round trips.
6. Added health log parsing/retry, official release payload caching/integrity checks, and RID-aware Worker packaging. Real UAC, game and GPU compatibility remain manual acceptance work.
7. Added logging-off replanning from the original deployment snapshot, explicit review before applying, failed-task recovery manifest display, and administrator CLI instructions before protected-directory recovery.

## Verification (2026-10-10)

- Debug and Release solution builds: 0 warnings, 0 errors.
- Latest Release tests after functionality acceptance fixes: 318 Core + 37 App = 355 passed, 0 failed, 0 skipped.
- Default, win-x64 framework-dependent and self-contained single-file App packages include an independent `worker/` with dependencies. Packaged Worker loads and rejects invalid arguments with exit code 2; no UAC or deployment performed by this check.
- Mock WPF previews checked at 1320×860 and 1020×650.
- Previous review findings for mutable recovery manifests, canonical EXE paths, Xbox shell activation, large logs and configuration-dump false health signals have been addressed.
- Isolated acceptance: 22 checks passed against published App/Core libraries and actual Worker process. Synthetic and official payload deployments restored original file hashes and directory sets. Desktop opens/responds/closes with exit code 0.
- Official v0.9.4 55 MB asset downloaded with production host/digest/extraction/cache checks using earlier official metadata snapshot because the live API later returned HTTP 403 rate limit. Live latest-query availability did not pass. Stable AMD backend aliases and Steam platform launching were corrected.
- Detailed evidence and limits: [functionality validation](2026-10-10-optiscaler-deployer-functionality-validation.md).

## Remaining manual acceptance

Real UAC consent/decline, game startup, GPU/driver and specific FSR model compatibility remain unverified. Live Releases API is currently rate limited; nightly had no assets at the earlier check. Desktop source/channel selection remains a target-product gap. Some initial DX12 feature paths do not emit an unambiguous initialized-backend signal and may conservatively time out. Timeout preserves deployment. Protected-directory manual recovery uses administrator CLI, not an elevated desktop rollback job.
