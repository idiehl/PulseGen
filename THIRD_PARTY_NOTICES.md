# PulseGen third-party dependency ledger

No third-party source or model weights are committed in this initial foundation.
Pinned sources are downloaded into ignored build output and verified before
compilation. `dependencies.lock.json` records active and intended adapters.

Before a dependency moves from `planned` or `interface-ready-not-vendored` to
enabled, record its exact release/commit, downloaded artifact SHA-256, license
text location, linkage/distribution mode, and the verifier that exercised it.

Model weights are never bundled implicitly. A model-pack manifest must identify
the upstream URL, checksum, native sample rate, runtime, license notice, and the
explicit user action that installed it.

## miniaudio 0.11.25

Copyright David Reid. PulseGen uses the MIT-0 option for the WASAPI playback
transport. Decoder, encoder, resource-manager, and generation modules are
disabled in the native build. Source is fetched from immutable commit
`9634bedb5b5a2ca38c1ee7108a9358a4e233f14d`; the release archive is verified by
the SHA-256 recorded in `dependencies.lock.json`.
