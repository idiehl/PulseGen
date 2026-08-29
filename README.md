# PulseGen

PulseGen is a physics-first, stereo sound-simulation workstation. The first
vertical slice renders a moving primitive emitter through a bounded acoustic world
into independently addressable binaural and stereo microphone receiver pairs.

The repository intentionally separates the managed editor from the native audio
engine. The native engine owns sample-time work; the editor exchanges versioned
commands and reads bounded telemetry.

## Current verified scope

- Versioned scene/project contracts using SI units.
- C++20 C ABI, deterministic offline stereo renderer, and low-latency WASAPI
  preview through checksum-pinned miniaudio 0.11.25.
- Sine, triangle, band-limited saw/square/pulse, deterministic white/pink/brown
  noise, impulses, additive banks, and procedural turbulence sources.
- Retarded-time propagation, distance loss, wind-adjusted sound speed, simple
  air absorption, first-order shoebox reflections, bounded modal resonances,
  late-field feedback delays, and receiver-pair modelling.
- WAV render output as 16/24-bit PCM with explicit TPDF dither or unclipped
  32-bit float, isolated stereo takes, and deterministic provenance receipts
  with scene/profile/dependency and per-take SHA-256 identities.
- Avalonia editor with linked World, Signal, Physics, Timeline, Receiver, and
  Library views; numerical SI editing; draggable paths; raw gesture retention;
  audible preview; atomic save; isolated-take rendering; and analysis import.
- Local model-host protocol, an application-owned checksum-pinned Python 3.11
  runtime, multi-resolution STFT decomposition, partial tracks, transient and
  stochastic layers, and exact-residual reconstruction.
- Demucs/AudioSep adapter manifests that deliberately remain unavailable until
  their weight artifacts, checksums, and licenses are audited.
- Content-addressed project assets, guarded portable PulsePack archives, bounded
  SQLite autosave history, recoverable multipart sync journals, and ETag-based
  conflict branches.
- Explicit Atlas sign-in and project sync using authorization code + S256 PKCE,
  rotating refresh tokens in Windows Credential Manager, HTTPS presigned upload
  grants, and local-only defaults. The corresponding identity and immutable
  library services live in `I:\dev\AtlasUniversalis`.
- JSON Schema, sample project, numerical tests, and CI verification.

Steam Audio, KISS FFT, libsndfile, Assimp, codec packs, GPU model execution, and
full mesh authoring remain explicit adapters; their dependency records prevent
an unfinished integration from being presented as active.

## Build

```powershell
./scripts/bootstrap.ps1 -IncludeModelHost
./scripts/build.ps1 -Configuration Release
./scripts/verify.ps1 -Configuration Release
```

Run the editor after a successful build:

```powershell
dotnet run --project managed/PulseGen.Editor/PulseGen.Editor.csproj -c Release
```

The sample scene is `samples/PhysicsFlyby.pulseproj/project.json`.

Atlas integration defaults to the production service origins but does not upload
anything until **Sync project** is pressed. Candidate or local services can be
selected before launch without storing secrets in project files:

```powershell
$env:PULSEGEN_IDENTITY_ORIGIN = 'https://identity.atlasuniversalis.com'
$env:PULSEGEN_LIBRARY_ORIGIN = 'https://pulse.atlasuniversalis.com'
```

An opt-in silent hardware smoke test verifies the default WASAPI device without
emitting a test tone:

```powershell
./out/native/release/native/PulseGen.Engine.Tests.exe --audio-device-smoke
```
