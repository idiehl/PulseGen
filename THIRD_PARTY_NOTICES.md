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

## Assimp 6.0.5

Copyright the Assimp project contributors. PulseGen statically links the
modified BSD 3-clause licensed Open Asset Import Library with only its OBJ and
glTF/GLB importers enabled; exporters, tools, samples, and upstream tests are
disabled in production builds. Source is fetched from immutable commit
`392a658f9c271be965271f45e7521a1b80ea4392`, and the archive is verified by the
SHA-256 recorded in `dependencies.lock.json`. The upstream license text remains
available in the checksum-verified fetched source tree and must be included in
packaged distributions.
