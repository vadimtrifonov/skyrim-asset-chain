# BSA fixtures

BSArch v0.9c created these archives with the `-sse` option. The files with `compressed` in the name also use `-z`.

Verified with `BSArch64.exe <archive> -list`:

- Each archive uses Skyrim SE format version `0x69`.
- Each stored path matches the expected test member.
- The compressed fixtures set the archive compression flag.
- The file flags identify meshes, textures, and miscellaneous files as applicable.
