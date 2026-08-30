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
- Retarded-time propagation, distance loss, temperature/humidity-dependent
  sound speed, pressure-aware air absorption, altitude layers, and transformed
  bounded wind/turbulence zones. Preview samples the field once per propagation
  leg; final rendering subdivides each leg for higher-accuracy travel time and
  tonal loss. Shoebox rooms render every first bounce plus deterministic
  opposing-wall second bounces, physically estimated three-band Eyring fade,
  bounded low-frequency modal resonances, deterministic scattered-surface tails,
  late-field feedback delays, analytic binaural head shadowing, and receiver-pair
  modelling. Stereo microphone capsules can carry independent
  three-axis offsets and aim, pickup patterns, and sensitivity into both preview
  and final rendering.
  Receiver rigs can follow the same editable absolute-coordinate paths as
  emitters; their arrival-time motion produces listener-side Doppler in every
  isolated stereo take.
- Static, transformed finite planes and oriented boxes with octave-band
  absorption, scattering, and transmission; geometry-aware direct occlusion;
  bounded first-order image-source reflections; and energy-normalized diffuse
  decay fed by authored scattering. The viewport renders the same authored
  surfaces used by offline and preview audio. The render profile's diffuse-detail
  budget and fade time now control the audible scattered response in both paths.
- Checksum-pinned Assimp 6.0.5 import for static OBJ and self-contained
  glTF/GLB acoustic meshes. Project assets are size/SHA-256 verified before a
  memory-only native import, capped at 250,000 triangles per mesh, and compiled
  into immutable BVHs for direct occlusion and transmission. The editor shows
  an explicit authored-bounds proxy; imported-mesh reflections fail clearly
  until the geometric reflection backend is active.
- WAV render output as 16/24-bit PCM with explicit TPDF dither or unclipped
  32-bit float, isolated stereo takes, and deterministic provenance receipts
  with scene/profile/dependency and per-take SHA-256 identities.
- Avalonia editor with linked World, Signal, Physics, Timeline, Receiver, and
  Library views; hierarchical scene selection; validated add/edit/remove flows
  for rooms, acoustic surfaces, atmosphere layers, bounded wind zones, binaural
  listeners, and independent microphone pairs; numerical SI editing;
  synchronized world highlighting and safe X/Z manipulation; receiver motion-path
  assignment; rollback of physically invalid edits; guarded acoustic-mesh and
  SOFA asset pickers; raw gesture retention; audible preview; atomic save;
  isolated-take rendering; and analysis import. Imported measured SOFA selections
  are content-addressed and deliberately render-gated until their Steam Audio
  adapter is active, so the analytic head model is never mislabeled as measured.
- First-class acoustic-material authoring with eight-band soak/scatter/pass
  controls, roughness, plain-language incoming-energy summaries, six adjustable
  starting points, independent duplication for comparisons, live surface usage
  reporting, guarded removal, and a non-destructive per-band listening workflow.
  Parametric rooms show their size/material-derived bass, voice, and brightness
  fade estimates, and all three estimates drive separate active room tails.
  Render profiles are selectable and editable in the scene tree,
  and the active profile drives preview as well as export. Preset coefficients
  are intentionally labeled as simulation starting points rather than certified
  measurements.
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

Steam Audio, KISS FFT, libsndfile, codec packs, GPU model execution, full mesh
authoring, and triangle-level imported-mesh viewport rendering remain explicit
adapters; their dependency records prevent an unfinished integration from being
presented as active.

Reflection depth 2 currently applies only to opposing wall pairs in an
axis-aligned parametric room. Authored finite planes and boxes remain deliberately
limited to one reflection, and imported meshes still provide occlusion rather
than reflection paths.

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

The repository includes two deterministic checked-in sample projects and two
built-in focused scenarios:

- `samples/PhysicsFlyby.pulseproj/project.json` exercises motion, Doppler,
  atmosphere, a shoebox room, and simultaneous receiver rigs.
- `samples/SurfaceEcho.pulseproj/project.json` compares finite stone, curtain,
  and timber surfaces with direct transmission and early reflections.
- `--sample moving-receiver` compares a static listener with a microphone pair
  approaching a stationary 440 Hz source at 10 m/s.
- `--sample atmosphere-field` compares two microphones across warm/cold air
  layers and bounded following/opposing wind zones, including deterministic
  turbulence and final-quality segmented propagation.

The render CLI can also create or render either built-in scene:

```powershell
dotnet run --project managed/PulseGen.RenderCli/PulseGen.RenderCli.csproj -c Release -- --sample surface-echo --output out/renders/surface-echo
dotnet run --project managed/PulseGen.RenderCli/PulseGen.RenderCli.csproj -c Release -- --sample moving-receiver --output out/renders/moving-receiver
dotnet run --project managed/PulseGen.RenderCli/PulseGen.RenderCli.csproj -c Release -- --sample atmosphere-field --output out/renders/atmosphere-field
```

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
