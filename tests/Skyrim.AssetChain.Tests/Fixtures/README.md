# BSA fixtures

BSArch v0.9c created the runtime-compatible archives with `-sse`. The files with `compressed` in the name also use `-z`.

`archive-skyrim-le.bsa` uses `-tes5`. It is an intentional incompatible-format fixture.

Verified with `BSArch64.exe <archive> -list`:

- Runtime-compatible archives use Skyrim SE format version `0x69`.
- `archive-skyrim-le.bsa` uses Skyrim LE format version `0x68`.
- Each stored path matches the expected test member.
- The compressed fixtures set the archive compression flag.
- The file flags identify meshes, textures, and miscellaneous files as applicable.
