# Native engine dependencies

All codec sources are pinned by SHA-256 in [scripts/fetch-native.ps1](../scripts/fetch-native.ps1) and built into SevenZipper.Native.dll. No compression executable runs in the installed product. If a local 7-Zip command is unavailable, the fetch script uses a hash-verified official 7zr.exe solely to unpack the source SDK.

| Component | Version | License notice to include |
| --- | --- | --- |
| [LZMA SDK](https://github.com/ip7z/7zip/releases/tag/26.06) | 26.06 | Igor Pavlov released the SDK to the public domain; see third_party/lzma-26.06/DOC/readme.txt. |
| [minizip-ng](https://github.com/zlib-ng/minizip-ng/tree/4.2.2) | 4.2.2 | zlib license, third_party/minizip-ng-4.2.2/LICENSE. |
| [zlib](https://zlib.net/fossils/) | 1.4 | zlib license, third_party/zlib-1.4/LICENSE. |
| [Zstandard](https://github.com/facebook/zstd/releases/tag/v1.5.7) | 1.5.7 | BSD 3-Clause (selected under dual licensing), third_party/zstd-1.5.7/LICENSE. |

The 7z container handler, LZMA/LZMA2, PPMd, standard executable filters, and 7z AES are from the official source SDK. ZIP uses minizip-ng for headers, Zip64, and encryption, with zlib raw Deflate and Zstandard method 93 streams. ZIP creation uses AES-256 if given a password. ZIP reading also supports traditional ZipCrypto. Compression methods outside the linked 7z SDK and ZIP Store, Deflate, and method 93 can be listed but may not be extracted.

The build generates a local variant of minizip-ng 4.2.2's mz_zip.c. Its entry read close used to discard the WinZip AES stream without invoking the stream's MAC verification. The local change explicitly closes that stream, propagating any authentication error before discarding it. Raw ZIP writers use the encryption stream when passed a password because this application feeds them compressed plaintext. Split ZIP directory handling uses a concatenated read view and records the correct per-disk ZIP/ZIP64 directory offsets and counts. The exact upstream source is kept unchanged and the patch anchors are checked during CMake configuration.

For split archives, use the .001 part for 7z or the .zip part for ZIP, with all companion volumes together. ZIP central directories may span volumes. A single header record must fit in the selected volume size. ZIP output is limited to 65,534 volumes; unsupported sizes fail without overwriting existing files.
