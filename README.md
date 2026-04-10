# NAITool

NAITool 是一个基于 WinUI 3、Windows App SDK 和 .NET 9 的 Windows 桌面工具仓库。当前仓库包含主应用、外层启动器、内置资源、预置模型和本地发布脚本，目标是让开发版与发布版尽量保持一致的目录结构。

## 仓库结构

```text
NAITool/
├─ NAITool.sln
├─ src/
│  ├─ NAITool/                 主应用项目（WinUI 3）
│  └─ NAIToolLauncher/         外层启动器项目
├─ assets/                     只读资源
├─ models/                     内置模型资源
├─ publish.ps1                 本地发布脚本
├─ global.json                 .NET SDK 版本约束
└─ .gitignore
```

其中：

- `src/NAITool/NAITool.csproj` 是主应用项目。
- `src/NAIToolLauncher/NAIToolLauncher.csproj` 是启动器项目。
- `NAITool.sln` 方便直接用 Visual Studio 或 Rider 打开整个仓库。

## 环境要求

- Windows 10/11
- .NET SDK `9.0.301` 或兼容更新版本
- Windows App SDK 相关构建环境

可先检查本机 SDK：

```powershell
dotnet --info
```

## 开发构建

推荐直接构建整个解决方案：

```powershell
dotnet build .\NAITool.sln -c Debug -p:Platform=x64
```

构建后默认输出到：

```text
build/Debug/
├─ NAITool.exe
├─ bin/
├─ assets/
├─ models/
├─ user/
├─ output/
└─ logs/
```

其中：

- 根目录的 `NAITool.exe` 是启动器入口。
- `bin/` 内是主应用和依赖库。
- `assets/`、`models/` 在开发模式下会自动指向仓库内对应目录。

如果只想单独构建主应用，也可以：

```powershell
dotnet build .\src\NAITool\NAITool.csproj -c Debug -p:Platform=x64
```

## 本地运行

推荐先构建解决方案，再直接运行构建输出目录里的启动器：

```powershell
.\build\Debug\NAITool.exe
```

如果你正在调试主应用本体，也可以直接从 IDE 打开 `NAITool.sln` 后运行 `src/NAITool/NAITool.csproj`。

## 发布

仓库自带 PowerShell 发布脚本：

```powershell
.\publish.ps1
```

默认输出到 `publish/NAITool/`，发布目录结构与开发构建保持一致：

```text
publish/NAITool/
├─ NAITool.exe
├─ bin/
├─ assets/
├─ models/
├─ user/
├─ output/
└─ logs/
```

## 运行时数据与敏感信息

程序运行后会在工作目录下生成本地数据，这些内容不应提交到仓库：

- `user/config/settings.json`
- `user/config/apiconfig.json`
- `output/`
- `logs/`
- `build/`
- `publish/`

其中 `user/config/apiconfig.json` 可能包含 API Token 或账户缓存信息。仓库中的 [`.gitignore`](./.gitignore) 已对常见本地数据和构建产物进行了排除，但提交前仍建议手动检查一次变更列表。

## 第三方资源与模型说明

- `assets/stable-diffusion-webui-wildcards/` 包含第三方 wildcards 资源，请遵循其上游项目的许可与使用约束。
- `models/upscaler/` 中的预置模型用于本地功能体验。若你计划二次分发、镜像或商用，请先确认各模型原始来源及其再分发许可。

建议在发布页或文档中继续补充更详细的第三方来源说明。

## 开源协作建议

- 提交 issue 前请尽量提供复现步骤、截图和日志片段。
- 提交 PR 前请确认没有把 `user/`、`output/`、`logs/`、`build/`、`publish/` 等本地文件带入提交。
- 涉及凭证、账户信息或安全问题时，请不要公开贴出敏感内容。

## 许可证

本项目采用 `GPL-3.0` 许可证。具体条款见 [`LICENSE`](./LICENSE)。
