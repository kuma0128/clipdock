# Third-party software

ClipDock source is MIT licensed. Download engines and the self-contained .NET runtime retain their own licenses. Executables are not stored in the source repository.

The .NET runtime licenses and third-party notices are included under `licenses/dotnet`, grouped by framework and version. These files are copied from the runtime packages used to build the distribution.

| Component | Official distribution / license information |
| --- | --- |
| .NET / WPF | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT / https://github.com/dotnet/wpf/blob/main/LICENSE.TXT (MIT and bundled third-party notices) |
| Microsoft Edge WebView2 SDK | https://www.nuget.org/packages/Microsoft.Web.WebView2 (Microsoft license and notices included under `licenses/webview2`) |
| yt-dlp | https://github.com/yt-dlp/yt-dlp#license (source: Unlicense; standalone bundles contain separately licensed dependencies) |
| FFmpeg / ffprobe | https://github.com/yt-dlp/FFmpeg-Builds / https://ffmpeg.org/legal.html (the selected `win64-gpl` build includes GPL components) |
| Deno | https://github.com/denoland/deno/blob/main/LICENSE.md (MIT and third-party components) |
| Avalonia / Avalonia WebView (macOS app) | https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md / https://www.nuget.org/packages/Avalonia.Controls.WebView (MIT; Avalonia bundles SkiaSharp and HarfBuzzSharp, MIT) |
| FFmpeg / ffprobe (macOS) | https://github.com/eugeneware/ffmpeg-static / https://ffmpeg.org/legal.html (static builds per architecture; GPL components included) |

The installer downloads release assets directly from the above official GitHub repositories and checks SHA-256 against GitHub's release asset digest before installation. The selected release, download URL and digest are recorded in `tools/versions.json`. License files included in upstream ZIP archives are retained under `tools/licenses`; yt-dlp's license and third-party notices and Deno's license are also fetched from the matching release tags. Consult the exact upstream release and its source/build instructions before redistributing these binaries.

The macOS app bundle contains the .NET runtime license and third-party notices under `Contents/Resources/licenses/dotnet`. On macOS the engine comes from the same yt-dlp and Deno repositories; FFmpeg and ffprobe come from the `eugeneware/ffmpeg-static` release assets, with the license published beside each binary stored under `tools/licenses`.

The iPhone app embeds [CPython from Python Apple Support](https://github.com/beeware/Python-Apple-support/releases/tag/3.14-b14), the [yt-dlp Python package](https://pypi.org/project/yt-dlp/2026.11.23/), and [certifi](https://pypi.org/project/certifi/2026.11.20/). The bundle includes the Python license in `python/lib/python3.14/LICENSE.txt`, the wheel licenses in `packages/*.dist-info/licenses/`, the runtime notices in `RuntimeNotices.txt`, and the support package versions in `VERSIONS`.

The iPhone app runs the bundled [yt-dlp-ejs 0.8.0](https://pypi.org/project/yt-dlp-ejs/0.8.0/) scripts in Apple's JavaScriptCore. ClipDockRemux.framework uses libavformat, libavcodec and libavutil from [FFmpeg 8.1.3](https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz) under LGPL 2.1 or later. Its license is included as `FFmpeg-LICENSE.txt`. The wrapper source is in `src/ClipDock.iOS/Remux/` and the build options are in `scripts/ios/build-remux.sh`.

The Android app uses [Chaquopy](https://github.com/chaquo/chaquopy/tree/17.2.0) to embed CPython, yt-dlp and certifi. Its user interface uses [AndroidX and Jetpack Compose](https://developer.android.com/jetpack/androidx). The APK includes license texts under `assets/licenses/` and in the embedded Python packages.

Chrome and GitHub are trademarks of their respective owners.
