# PulseGen

PulseGen is a physics-first, stereo sound-simulation workstation. The first
vertical slice renders a moving sine emitter through a bounded acoustic world
into independently addressable binaural and stereo microphone receiver pairs.

The repository intentionally separates the managed editor from the native audio
engine. The native engine owns sample-time work; the editor exchanges versioned
commands and reads bounded telemetry.

## Current verified scope

- Versioned scene/project contracts using SI units.
- C++20 C ABI and deterministic offline stereo renderer.
- Retarded-time propagation, distance loss, wind-adjusted sound speed, simple
  air absorption, first-order shoebox reflections, and receiver-pair modelling.
- Float WAV render output and isolated stereo takes.
- Avalonia editor shell with linked World, Signal, Physics, Timeline, Receiver,
  and Library views.
- Local model-host protocol, an application-owned checksum-pinned Python 3.11
  runtime, multi-resolution STFT decomposition, partial tracks, transient and
  stochastic layers, and exact-residual reconstruction.
- Demucs/AudioSep adapter manifests that deliberately remain unavailable until
  their weight artifacts, checksums, and licenses are audited.
- Content-addressed project assets, guarded portable PulsePack archives, bounded
  SQLite autosave history, and a local sync journal.
- JSON Schema, sample project, numerical tests, and CI verification.

Steam Audio, miniaudio, KISS FFT, libsndfile, Assimp, codec packs, GPU model
execution, full mesh authoring, and live device playback are explicit adapters;
their dependency records prevent an unfinished integration from being presented
as active.

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
