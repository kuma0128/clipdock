# Third-party software

ClipDock source is MIT licensed. Download engines and the self-contained .NET runtime retain their own licenses. Executables are not stored in the source repository.

The .NET runtime licenses and third-party notices are included under `licenses/dotnet`, grouped by framework and version. These files are copied from the runtime packages used to build the distribution.

| Component | Official distribution / license information |
| --- | --- |
| .NET / WPF | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT / https://github.com/dotnet/wpf/blob/main/LICENSE.TXT (MIT and bundled third-party notices) |
| yt-dlp | https://github.com/yt-dlp/yt-dlp#license (source: Unlicense; standalone bundles contain separately licensed dependencies) |
| FFmpeg / ffprobe | https://github.com/yt-dlp/FFmpeg-Builds / https://ffmpeg.org/legal.html (the selected `win64-gpl` build includes GPL components) |
| Deno | https://github.com/denoland/deno/blob/main/LICENSE.md (MIT and third-party components) |

The installer downloads release assets directly from the above official GitHub repositories and checks SHA-256 against GitHub's release asset digest before installation. The selected release, download URL and digest are recorded in `tools/versions.json`. License files included in upstream ZIP archives are retained under `tools/licenses`; yt-dlp's license and third-party notices and Deno's license are also fetched from the matching release tags. Consult the exact upstream release and its source/build instructions before redistributing these binaries.

Chrome and GitHub are trademarks of their respective owners.
