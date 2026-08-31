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
  into immutable BVHs for direct occlusion and transmission. A read-only native
  copy operation gives the editor the exact transformed, non-degenerate triangle
  set accepted by that acoustic importer; the dashed authored bounds remain only
  as an editable selection handle. Imported-mesh reflections fail clearly until
  the geometric reflection backend is active.
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
- Retained OpenGL/ANGLE terrain and imported-mesh rendering beneath the editable
  world overlay. Geometry is verified and prepared away from the UI thread,
  uploaded once to retained vertex/index buffers, and submitted in one indexed
  draw. Flat acoustic-surface normals make imported triangles readable without
  inventing source materials. A live viewport badge reports measured frame rate,
  triangle count, and imported-mesh count. Procedural and verified-OBJ acceptance
  modes each exercise exactly 250,000 triangles and produce atomic JSON receipts
  instead of treating a successful build as a responsiveness result.
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
  runtime, multi-resolution STFT decomposition, editable sinusoidal partial
  tracks, transient and stochastic layers, and exact-residual reconstruction.
  The editor can analyze a chosen receiver take as linked stereo, independent
  left/right, or center/width (mid-side), with an adjustable 1–256 tones per
  frame. Before declaring any result, it verifies the untouched source identity,
  layer hashes, timing/channel alignment, null error, local-only provenance, and
  partial-track count; the three audio layers, track data, and analysis receipt
  then become immutable content-addressed assets in the saved project.
- Interactive spectral workspace with a time/log-frequency tone-path view,
  selectable center/width or L/R tracks, independent tone mute/gain/±24-semitone
  edits, and separate controls for tonal, transient, and stochastic layers.
  Unedited layers retain their exact reconstruction path; edited tones use
  phase-aware delta resynthesis against that untouched tonal layer. Edit saves
  are immutable, parent-linked versions, and deterministic two-channel float WAV
  mixes are content-addressed back into the project without normalization.
- Receiver Takes workspace that turns every saved render into one verified,
  editable stereo take per listener or microphone rig. PulseGen retains the
  untouched interleaved render, stores true mono left/right channel assets,
  shows both channels on an arrangement timeline, and supports linked or
  independent mute/gain, non-destructive in/out trims, and timeline placement.
  A take, either isolated channel, or the selected multi-take master can be
  auditioned in process. The studio-master workflow exports standard WAV,
  metadata-rich BWF, RF64/BWF, or AIFF with explicit 16/24/32-bit PCM choices,
  deterministic optional TPDF dither, plus unclipped 32-bit-float standard WAV.
  It also exports 16/24-bit lossless FLAC and high-quality or compact VBR MP3,
  Ogg Vorbis, and Opus. MP3 and Opus use deterministic offline sample-rate
  conversion when the project rate is outside the codec's valid set; the UI and
  receipt disclose both rates instead of silently relabeling the audio.
  Each master is measured for gated programme loudness (LUFS), loudness range,
  sample peak, and oversampled true peak. No normalization is applied: integer
  export rejects an overloaded mix and float WAV preserves it with an explicit
  warning. A SHA-256 receipt records scene revision, included take IDs, file
  identity, precision or VBR profile, lossiness, encoder/version/settings,
  source and encoded rates, dither seed, measurements, and engine version; both
  the verified audio and receipt become immutable project assets. Ogg exports
  receive a deterministic stream identity with repaired page checksums, so all
  eight containers are repeatable for the same input and request. Render receipts
  identify each source output by receiver GUID and name before any project import
  is accepted.
- Bounded in-process stereo-buffer audition through miniaudio/WASAPI. The native
  engine copies finite audio before device start, and its real-time callback only
  reads the preallocated buffer—no allocation, locks, file access, or managed
  callback occurs on the audio thread. If Windows removes, replaces, or reroutes
  the output endpoint, the editor retains that scene or copied buffer, makes up
  to five short backoff attempts against the current default device, and resumes
  at the current timeline frame instead of restarting the sound. Reopen work is
  kept off the audio callback, stopped auditions fail clearly after the bounded
  retry window, and a live scene can continue as a visual-only preview.
- Lock-free real-time callback diagnostics. Every rendered hardware block records
  total and peak render time, peak budget use, observed block size, and an exact
  count of callbacks that finished after their own audio deadline. The editor's
  engine badge translates this into `CLEAN`, `TIGHT`, or an explicit missed-block
  count; its tooltip shows the average, peak, and configured budget in
  milliseconds. These are PulseGen render-deadline measurements, not a falsely
  inferred operating-system underrun counter. Live preview uses one retarded-time
  solver step, one path-averaged atmosphere sample, and receiver-centred early
  reflection geometry; final and offline rendering retain the six-step,
  segmented, per-ear calculation.
- Protected editor autosave and crash recovery. After eight seconds without a
  new scene edit, the editor writes a validated manifest snapshot to the local
  SQLite index while leaving the `.pulseproj` untouched. The newest 50 snapshots
  per project are retained. A process-ID/start-time session journal distinguishes
  another live editor from a crashed window and from an intentional close that
  still contained unsaved work. Startup restores only a valid, newer snapshot in
  memory; the user must explicitly save it or use the two-step discard control.
  Corrupt snapshots are quarantined rather than substituted for the current scene.
- Demucs/AudioSep adapter manifests that deliberately remain unavailable until
  their weight artifacts, checksums, and licenses are audited.
- Content-addressed project assets, guarded portable PulsePack archives,
  recoverable multipart sync journals, and ETag-based conflict branches.
- Explicit Atlas sign-in and project sync using authorization code + S256 PKCE,
  rotating refresh tokens in Windows Credential Manager, HTTPS presigned upload
  grants, and local-only defaults. The corresponding identity and immutable
  library services live in `I:\dev\AtlasUniversalis`.
- JSON Schema, sample project, numerical tests, and CI verification.

Steam Audio, KISS FFT, GPU model execution, full mesh authoring, and source
material/texture recreation for imported meshes remain explicit adapters; their
dependency records prevent an unfinished integration from being presented as
active.

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

Bootstrap downloads the official libsndfile 1.2.2 Windows codec package into
the ignored `.runtime` directory, verifies the archive, DLL, and license with
the hashes in `dependencies.lock.json`, and never accepts a different DLL at
runtime. The application links to it dynamically.

Run the editor after a successful build:

```powershell
dotnet run --project managed/PulseGen.Editor/PulseGen.Editor.csproj -c Release
```

The repository includes two deterministic checked-in sample projects and four
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
- `--sample realtime-stress` supplies the audio acceptance shape: eight moving
  primitive emitters, one binaural head, two microphone pairs, a reflective
  parametric room, and the interactive 48 kHz / 512-frame profile.
- The editor-only viewport stress scene supplies one visual-only 50 m by 25 m
  terrain tessellated to exactly 250,000 triangles for retained-GPU acceptance.
  Its imported mode generates a content-addressed 250,000-triangle OBJ, verifies
  it through the production Assimp boundary, and renders those accepted triangles.

The render CLI can also create or render any built-in scene:

```powershell
dotnet run --project managed/PulseGen.RenderCli/PulseGen.RenderCli.csproj -c Release -- --sample surface-echo --output out/renders/surface-echo
dotnet run --project managed/PulseGen.RenderCli/PulseGen.RenderCli.csproj -c Release -- --sample moving-receiver --output out/renders/moving-receiver
dotnet run --project managed/PulseGen.RenderCli/PulseGen.RenderCli.csproj -c Release -- --sample atmosphere-field --output out/renders/atmosphere-field
```

Run the silent, hardware-backed real-time acceptance scenario for six seconds
on each of its head and two microphone receiver rigs:

```powershell
./scripts/run-realtime-stress.ps1 -Seconds 6
```

The command fails when playback never starts or any callback exceeds its block
deadline, and prints machine-readable average/peak callback timing and peak load.
Pass `-ReceiverIndex 1` to exercise only one receiver when investigating a
specific rig.

Run the hardware-backed viewport acceptance scenario for six measured seconds:

```powershell
./scripts/run-viewport-stress.ps1 -Seconds 6
./scripts/run-viewport-stress.ps1 -ImportedMesh -Seconds 6
```

Both modes warm the GPU path, require an exact 250,000-triangle source, enforce
the requested frame-rate and CPU-submit thresholds, and print the OpenGL backend
plus measured timing. Imported mode additionally records the verified asset
SHA-256, native engine version, imported mesh count, and imported triangle count.
Results are specific to the tested machine, driver, window size, build, and
display refresh rate.

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
