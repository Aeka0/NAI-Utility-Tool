# NAI Utility Tool

NAI Utility Tool is a Windows desktop client for NovelAI image workflows. It is built with WinUI 3, Windows App SDK, and .NET 9, and focuses on day-to-day image generation, image-to-image editing, prompt management, local post-processing, upscaling, and metadata inspection.

The repository contains the main WinUI application, a lightweight launcher, bundled UI/resources, wildcard and tag data, local upscaler models, and a PowerShell publishing script that produces the same layout used by development builds.

## Features

- NovelAI text-to-image generation with current NAI Diffusion models.
- Image-to-image and inpainting workflows with mask editing, denoise controls, canvas alignment, mask expansion/contraction, and canvas inference helpers.
- Positive, negative, style, and per-character prompt areas with tag completion, weight highlighting, prompt normalization, prompt shortcuts, wildcard expansion, and weight-format conversion.
- Vibe Transfer and Precise Reference support, including local Vibe pre-encode caching and a Vibe pre-encode manager.
- Automation presets for repeated generation, randomized size/style/prompt/vibe selection, request pacing, retry handling, optional post-processing, and optional auto-upscale.
- Local image post-processing with effect chains and presets, including brightness/contrast, saturation, temperature, glow, blur, vignette, chromatic aberration, noise, pixelation, blocks, and scanlines.
- Local ONNX upscaling with bundled anime upscaler models.
- Image metadata inspection and transfer back into generation settings.
- Optional local reverse tagging with a user-provided ONNX tagger model.
- Generation history, drag-and-drop routing, multilingual UI resources, theme/transparency settings, and local privacy controls.

## Requirements

- Windows 10 or Windows 11.
- .NET SDK 9.0.301 or a compatible newer feature-band SDK.
- Windows App SDK build support, usually installed with Visual Studio 2022 and the Windows App SDK workload/components.
- A NovelAI API token for generation, inpainting, prompt generation, Vibe encoding, and account/quota features.
- Optional: a reverse tagger ONNX model folder for local image tag inference.

Check the local SDK with:

```powershell
dotnet --info
```

## Repository Layout

```text
NAITool/
|-- NAITool.sln
|-- src/
|   |-- NAITool/             Main WinUI 3 application
|   `-- NAIToolLauncher/     Outer launcher executable
|-- assets/
|   |-- fxpresets/           Default post-processing presets
|   |-- i18n/                UI localization files
|   |-- icon/                Application icon
|   |-- img/                 Bundled app images
|   |-- splash/              Splash screen assets
|   |-- tagsheet/            Tag completion and style data
|   `-- wildcards/           Bundled wildcard libraries
|-- models/
|   `-- upscaler/            Bundled local upscaler models
|-- publish.ps1              Local publish script
|-- Directory.Build.props    Shared build output layout
|-- global.json              .NET SDK version policy
`-- LICENSE
```

## Build

Build the full solution for local development:

```powershell
dotnet build .\NAITool.sln -c Debug -p:Platform=x64
```

The shared build settings write the launcher to `build/Debug/` and the main app to `build/Debug/bin/`. A successful development build creates this layout:

```text
build/Debug/
|-- NAIUtilityTool.exe       Launcher entry point
|-- bin/                     Main application and dependencies
|-- assets/                  Junction to repository assets
|-- models/                  Junction to repository models
|-- user/
|-- output/
`-- logs/
```

To build only the main app:

```powershell
dotnet build .\src\NAITool\NAITool.csproj -c Debug -p:Platform=x64
```

## Run

After building the solution, run the launcher:

```powershell
.\build\Debug\NAIUtilityTool.exe
```

You can also open `NAITool.sln` in Visual Studio or Rider and run the `src/NAITool/NAITool.csproj` project directly while debugging the main WinUI application.

On first launch, the quick tour can help configure language, the NovelAI API token, account asset-protection behavior, and the optional reverse tagger model path.

## Publish

Use the included PowerShell script to create a local distributable build:

```powershell
.\publish.ps1
```

By default, the script publishes a Release x64 build to:

```text
publish/NAITool/
|-- NAIUtilityTool.exe       Launcher entry point
|-- bin/                     Main application and dependencies
|-- assets/                  Runtime assets copied from the repository
|-- models/                  Bundled upscaler models
|-- user/                    Default user data directories and presets
|-- output/
`-- logs/
```

Optional parameters:

```powershell
.\publish.ps1 -Configuration Release -Runtime win-x64
```

Supported runtimes should match the project runtime identifiers, such as `win-x64`, `win-x86`, or `win-arm64`.

## Runtime Data and Privacy

The app writes user data beside the executable so development and published layouts behave consistently. These files and directories are local runtime data and should not be committed:

- `user/config/settings.json`
- `user/config/apiconfig.json`
- `user/fxpresets/`
- `user/userprompts/`
- `user/wildcards/`
- `user/automation/`
- `user/vibe/`
- `output/`
- `logs/`
- `build/`
- `publish/`

`user/config/apiconfig.json` can contain API tokens or cached account information. Treat it as sensitive. The repository `.gitignore` excludes common runtime data, build output, publish output, downloaded tagger models, and local design assets, but always review changes before committing.

## Models and External Assets

The repository includes small local upscaler models under `models/upscaler/` for the built-in upscale workflow.

Reverse tagging is optional and expects a user-provided model folder, typically under `models/tagger/`. That folder is ignored by git because tagger models can be large and may have separate redistribution terms.

The wildcard and tag resources under `assets/` include third-party or derived data. If you redistribute this application, package it into another project, or use it commercially, review the original source licenses and redistribution terms for those resources and any additional models you add.

## Contributing

- Open issues with clear reproduction steps, screenshots when useful, and relevant log snippets.
- Keep changes focused and avoid committing local runtime data, generated output, build artifacts, API tokens, downloaded models, or personal configuration.
- See `CONTRIBUTING.md` and `SECURITY.md` for contribution and vulnerability-reporting guidance.

## License

This project is licensed under GPL-3.0. See `LICENSE` for the full license text.
