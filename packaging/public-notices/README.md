# Public distribution notices

`scripts/add-publication-notices.ps1` assembles the actual upstream license and
copyright texts for the package being staged. It includes notices embedded in
the resolved NuGet packages, .NET's bundled third-party inventory, Assimp's
contributed libraries, and the separately compiled codecs inside `sndfile.dll`.
It fails if the managed inventory contains an unrecognized license expression
or if any checksum-pinned source archive is missing or changed.

The codec source archives, including the LGPL components libsndfile, LAME and
mpg123, are supplied as a separate release asset with an index, original URLs,
identity evidence and SHA-256 hashes. The LAME download uses Debian's unchanged
upstream-source mirror; its full SHA-512 is checked against Microsoft's vcpkg
recipe. This avoids accepting SourceForge's HTML download interstitial as code.

The libogg version and reference vcpkg snapshot are explicitly qualified in the
index because the original upstream Windows binary did not retain complete
toolchain provenance. Source archives include upstream tools and documentation
under their own licenses; this does not mean those tools were linked into
PulseGen. No model weights or user recordings are distributed by this process.

Run `scripts/package-windows.ps1 -PublicDistribution -OutputDirectory
out/publication/packages` after committing the intended source and completing
the normal release build. This produces an independently sealed package and a
companion third-party source ZIP under `out/publication/sources`, preserving
existing private-alpha packages. The managed editor must retain the explicit
codec replacement workflow when distributing its LGPL runtime.
