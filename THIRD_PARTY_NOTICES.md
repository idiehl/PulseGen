# PulseGen third-party dependency ledger

No third-party source, binary, or model weights are committed in this repository.
Pinned sources and runtime binaries are downloaded into ignored build output and
verified before compilation or loading. `dependencies.lock.json` records active
and intended adapters.

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

## libsndfile 1.2.2 and bundled codec implementations

PulseGen dynamically loads the official `libsndfile-1.2.2-win64.zip` release to
write and verify FLAC, MP3, Ogg Vorbis, and Ogg Opus masters. The release archive
and extracted `sndfile.dll` are checked against the SHA-256 values in
`dependencies.lock.json`; the managed loader repeats the DLL check before any
native code executes. `dumpbin /DEPENDENTS` reports only `KERNEL32.dll`, so the
codec implementations are contained within that official DLL rather than copied
into PulseGen itself.

libsndfile is used under LGPL-2.1-or-later through a replaceable DLL boundary.
The exact LGPL 2.1 text from the 1.2.2 tag is downloaded, checksum-verified, and
stored beside the runtime as `COPYING-libsndfile-LGPL-2.1.txt`. Packaged builds
must include that file, identify the DLL as replaceable, and preserve access to
the corresponding upstream source.

The pinned DLL identifies its bundled encoders as libFLAC 1.4.2, LAME 3.100,
libopus 1.4, and Xiph.Org libVorbis I 20200704. FLAC, Ogg, Vorbis, and Opus use
Xiph/BSD-style licenses; LAME uses LGPL. Their copyright notices and applicable
license texts must accompany a distributable PulseGen package. This development
milestone produces an unsigned portable engineering package and copies the
verified libsndfile, Assimp, miniaudio, CPython, and NumPy license records beside
the application. It also records the fully resolved managed NuGet inventory.
Public installer-level notice review and code signing remain packaging-hardening
gates rather than being implied complete.

## CPython 3.11.9 and NumPy 2.0.2

The optional local decomposition host uses the official CPython 3.11.9 Windows
embeddable distribution and the exact NumPy 2.0.2 CPython 3.11 x64 wheel. Both
artifacts are size- and SHA-256-verified against `modelhost/runtime.manifest.json`
before packaging. Their upstream license files, including NumPy's bundled-library
notices, are copied into the package `licenses` directory. Python is launched
with bytecode writes disabled so a package self-test does not modify the sealed
application payload.
