# PulseGen third-party dependency ledger

No third-party source or model weights are committed in this initial foundation.
`dependencies.lock.json` records the intended adapters and their review state.

Before a dependency moves from `planned` or `interface-ready-not-vendored` to
enabled, record its exact release/commit, downloaded artifact SHA-256, license
text location, linkage/distribution mode, and the verifier that exercised it.

Model weights are never bundled implicitly. A model-pack manifest must identify
the upstream URL, checksum, native sample rate, runtime, license notice, and the
explicit user action that installed it.

