# Audited local-variable default: fresh startup controls (2026-10-01)

PASS on immutable `1a008b1` Native ABI95 / Agent wire84 defaults artifacts. These checks exercise the compiled stock allowlist; they do not certify arbitrary local-variable keys or mod scripts. The genuine combat exclusion check is documented in [localvar-combat-negative-2026-10-01.md](localvar-combat-negative-2026-10-01.md).

Two fresh two-player sessions used private game1/2 on port 39147. Each loaded the unchanged disposable baseline, then traveled to `tar_m10aa`. The actual current typed GIT bartender creature `0x17` had Boolean10 zero on both instances. Running unmodified stock `k_act_talktrue` with that creature as OBJECT_SELF executes the original VM SetLocalBoolean call; this is not the console local-variable publishing path.

| Fresh configuration on both instances | Native hook status host/client | Host capture cursor | Host Boolean10 | Client Boolean10 / capture cursor |
| --- | --- | --- | --- | --- |
| No `local_var_sync` key | 3 / 3 | 0 -> 1 | 0 -> 1 | 0 -> 1 / 0 |
| Explicit `local_var_sync=0` | 0 / 0 | 0 | 0 -> 1 | Remained 0 / 0 |

Missing-key PIDs were 28088/54576. Explicit-zero PIDs were 51332/22008; the negative observation lasted 30.9 seconds between before/after client snapshots. Both sessions were connected, with current module/native epoch fences and current object-array body checks in the read-only oracle. The positive proves both native capture and Agent/peer application default on; explicit zero prevents native capture and remote application in a fresh process.

Each case used a fresh baseline: between sessions the scoped games and child Agents were stopped by recorded PID/start-time/executable identity, and the complete saves/runtime folders plus configuration/native DLL were restored. Final original file SHA256, sizes and mtimes matched exactly. No original installation was changed. Test configurations and exact binary hashes, native/Agent logs, coherent before/after snapshots, raw command replies, and restoration manifests are retained under `artifacts/localvar-defaults-live-v95`; `proof.json` contains the machine-readable result.

Preparation first encountered a missing artifact-local source tree and a mistaken save index 900; those launches were stopped by the scoped harness before the successful fresh runs. The final harness used Save 1 with the original baseline directory newest. Neither preparation failure is counted as a feature test.
