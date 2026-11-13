# Third-party components

Free Video Studio is licensed under the [GNU GPL v3](LICENSE). The installer also ships these
unmodified upstream programs and libraries (folder `binaries\` in this repository, `backend\` /
`frontend\` when installed). Each keeps its own licence.

| Component | Files | Version | Licence | Source code |
| :--- | :--- | :--- | :--- | :--- |
| FFmpeg | `ffmpeg.exe`, `ffprobe.exe`, `avcodec-62.dll`, `avdevice-62.dll`, `avfilter-11.dll`, `avformat-62.dll`, `avutil-60.dll`, `postproc-59.dll`, `swresample-6.dll`, `swscale-9.dll` | `N-119166-g1e5c65f539` (2025-04-08 build, `--enable-gpl --enable-version3`) | GPL v3 | https://github.com/FFmpeg/FFmpeg/commit/1e5c65f539 and the build scripts at https://github.com/BtbN/FFmpeg-Builds |
| mpv / libmpv | `mpv.exe`, `libmpv-2.dll` | `v0.40.0-298-gb9ceaf243` (built 2025-09-06) | GPL v2 or later (used under GPL v3) | https://github.com/mpv-player/mpv/commit/b9ceaf243 |

GPL source offer: the exact source of every component above is available at the links listed. If a link
stops working, open an issue at https://github.com/alonreich/Free_Video_Studio/issues and the
corresponding source will be provided for at least three years after the release that shipped it.

.NET runtime, Avalonia, SkiaSharp and other NuGet packages are MIT- or Apache-2.0-licensed and are
compiled into the program; their licences are in each package.

Starter media (`meme\`, `mp3\`): original/generated clips and pictures by the author, and songs under
Creative Commons licences — each song's credit is in the `.txt` file next to it.
