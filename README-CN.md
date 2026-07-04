<img width="1344" height="768" alt="Title" src="https://github.com/user-attachments/assets/ada996fb-730e-400a-826a-c614b0bd6087" />

语言： [English](README.md) | 简体中文

# NAI Utility Tool

NAI Utility Tool 是一个面向 NovelAI 图像工作流的 Windows 桌面客户端。它基于 .NET 9、WinUI 3、Windows App SDK、Win2D、SkiaSharp 和 ONNX Runtime 构建，目标不是替代 NovelAI 官方站点，而是把日常高频操作整合到一个本地工具里：生成、重绘、遮罩编辑、提示词管理、参考图工作流、批量自动化、后期处理、超分、元数据检视和本地反推。

项目当前版本为 `1.1.0`，仍在快速迭代。本文档以当前 `main` 分支源码结构为准。

<img width="1384" height="892" alt="Interface" src="https://github.com/user-attachments/assets/f24696cb-dce5-4e37-8e44-8976be5c9c06" />

## 主要能力

### 图像生成

- 支持 NovelAI Diffusion 4.5、4、3 系列模型。
- 支持常用采样器与调度器，并会根据当前模型过滤不适用选项。
- 支持正面、负面、风格提示词拆分输入。
- 支持 NAI v4+ 角色提示词、角色负面提示词和角色位置。
- 支持种子管理、Variety+、质量词、UC 预设、CFG Scale、CFG Rescale、步数等高级参数。
- 支持生成后预览、保存、复制、删除、发送到其他工作区和历史记录管理。
- 支持连续生成与重复请求防护。

### 重绘与遮罩编辑

- 支持遮罩重绘和降噪重绘两种模式。
- 内置 Win2D 画布，支持笔刷、橡皮、矩形遮罩、撤销、重做、缩放和平移。
- 支持遮罩预览、反转、扩展、收缩、清空、填充空白、裁剪画布和对齐图像。
- 支持从图片元数据回填参数，再发送到重绘工作区。
- 支持画布或遮罩导出。

### 提示词工作流

- 标签自动补全来自 `assets/tagsheet/`。
- 支持提示词权重高亮、提示词标准化、快捷提示词替换和随机风格词。
- 支持 Wildcards 抽卡器，默认使用 `__name__` 显式语法，也可在设置中调整。
- 支持 NAI 经典权重、NAI 4+ 数字权重和 SD-WebUI 权重格式互转。
- 支持 NovelAI 文本接口的提示词生成辅助。

### 参考图与 Vibe

- 支持 NAI 官方 Vibe Transfer。
- 支持 NAI 官方 Precise Reference，并可选择角色、风格或角色加风格参考类型。
- 支持本地 Vibe 预编码缓存，减少重复编码消耗。
- 提供 Vibe 预编码管理器，可查看、清理、重定位原图和导出编码文件。
- 可选自动复制 Vibe 原图到本地工作目录，便于长期管理参考素材。

### 自动化

- 支持保存和载入自动化预设。
- 支持请求次数限制、请求间隔、HTTP 错误重试策略。
- 支持随机尺寸、随机快捷提示词、随机风格词和随机 Vibe。
- 支持生成完成后自动超分。
- 支持生成完成后自动套用后期预设链。

### 后期处理

- 支持效果链编辑、排序、撤销、重做、保存预设和应用预设。
- 当前内置效果包括亮度/对比度、饱和度/自然饱和度、色温、泛光、径向模糊、暗角、镜头色散、杂色、Gamma、像素化、实色遮挡、扫描线和 JPEG 损耗。
- 预览和导出使用后期渲染服务统一处理，优先使用 Win2D GPU 路径，并在不适合 GPU 的效果链上回退到 CPU 路径。
- 像素化和实色遮挡支持区域编辑，可在预览区域直接调整位置和范围。

### 本地超分

- 使用 ONNX Runtime DirectML 执行本地超分，设置中可切换 GPU/CPU 偏好。
- 仓库内置 `models/upscaler/` 下的动漫向超分模型。
- 支持滑块或手动输入目标倍率，按目标倍率多轮处理，并使用分块推理降低大图内存压力。

### 检视、元数据与反推

- 支持拖入或打开图片查看 NovelAI 元数据。
- 支持把图片中的提示词、角色提示词、参数、参考图信息回填到生成或重绘工作区。
- 支持保存时移除元数据，也支持全局元数据消除。
- 支持图像混淆与还原混淆。
- 可选配置本地 ONNX 反推模型，对图片执行 Tagger 推理。
- 反推支持常见 WD14、SmilingWolf 和 PixAI Tagger 模型目录格式，使用模型目录中的 `selected_tags.csv` 读取标签定义。
- 反推结果会区分普通标签、角色标签和评级标签，可分别控制阈值，并可选择是否把评级标签加入生成提示词。

### 本地体验

- 支持简体中文、繁体中文、英文、日文、韩文、俄文、德文、法文、西班牙文和拉丁文界面资源。
- 支持浅色、深色、跟随系统和透明度选项。
- 支持 SuperDrop：把图片拖入窗口后选择生成提示词、Vibe、Precise Reference、重绘、超分、后期或检视等目标。
- API Token 使用 Windows DPAPI 加密后保存在本机当前用户下。
- 支持隐私模式，开启后生成结果不会自动写入 `output/` 和历史记录。
- 默认删除行为为放入系统回收站，可在设置中改为永久删除。

## 模型与参数概览

生成模型：

- `nai-diffusion-4-5-full`
- `nai-diffusion-4-5-curated`
- `nai-diffusion-4-full`
- `nai-diffusion-4-curated-preview`
- `nai-diffusion-3`
- `nai-diffusion-furry-3`

重绘模型：

- `nai-diffusion-4-5-full-inpainting`
- `nai-diffusion-4-5-curated-inpainting`
- `nai-diffusion-4-full-inpainting`
- `nai-diffusion-4-curated-inpainting`
- `nai-diffusion-3-inpainting`
- `nai-diffusion-furry-3-inpainting`

采样器：

- `k_euler_ancestral`
- `k_euler`
- `k_dpmpp_2m`
- `k_dpmpp_sde`
- `k_dpmpp_2s_ancestral`
- `k_dpm_2`
- `k_dpm_fast`
- `k_dpmpp_2m_sde`
- `ddim`
- `ddim_v3`

调度器：

- `native`
- `karras`
- `exponential`
- `polyexponential`

## 环境要求

- Windows 10 或 Windows 11。
- 从源码构建需要 .NET SDK `9.0.301` 或兼容的更新 feature band SDK。
- 需要可用的 Windows App SDK 构建环境，通常由 Visual Studio 2022 及相关 Windows 开发组件提供。
- 需要 NovelAI API Token 才能使用生成、重绘、Vibe 编码、提示词生成、账户额度查询等联网功能。
- 可选：本地 ONNX 反推模型目录，用于图片 Tagger 推理；模型目录需要包含 `.onnx` 模型和 `selected_tags.csv`。

检查本机 SDK：

```powershell
dotnet --info
```

## 仓库结构

```text
NAITool/
|-- NAITool.sln
|-- src/
|   |-- NAITool/             主 WinUI 3 桌面应用
|   `-- NAIToolLauncher/     外层启动器
|-- assets/
|   |-- fxpresets/           默认后期预设
|   |-- i18n/                多语言界面资源
|   |-- icon/                应用图标
|   |-- img/                 应用内图片资源
|   |-- splash/              启动画面资源
|   |-- svg/                 UI中用到的svg资源
|   |-- tagsheet/            标签补全与风格词数据
|   `-- wildcards/           内置抽卡器资源
|-- models/
|   `-- upscaler/            内置本地超分模型
|-- publish.ps1              本地发布脚本
|-- Directory.Build.props    共享构建输出布局
|-- global.json              .NET SDK 版本策略
|-- CONTRIBUTING.md
|-- SECURITY.md
`-- LICENSE
```

## 构建

开发构建建议使用 `Debug | x64`：

```powershell
dotnet build .\NAITool.sln -c Debug -p:Platform=x64
```

构建成功后会形成开发运行布局：

```text
build/Debug/
|-- NAI Utility Tool.exe     启动器入口
|-- bin/                     主应用与依赖
|-- assets/                  指向仓库 assets 的目录联接
|-- models/                  指向仓库 models 的目录联接
|-- user/
|-- output/
`-- logs/
```

只构建主应用：

```powershell
dotnet build .\src\NAITool\NAITool.csproj -c Debug -p:Platform=x64
```

## 运行

完整构建后，从仓库根目录运行启动器：

```powershell
.\build\Debug\NAI Utility Tool.exe
```

也可以在 Visual Studio 或 Rider 中打开 `NAITool.sln`，运行 `src/NAITool/NAITool.csproj` 进行调试。

首次启动会显示快速导览，可配置语言、NovelAI API Token、资产保护模式和可选反推模型目录。

## 发布

使用仓库内脚本创建本地发布包：

```powershell
.\publish.ps1
```

默认发布 `Release`、`win-x64`，输出目录为：

```text
publish/NAI Utility Tool/
|-- NAI Utility Tool.exe     启动器入口
|-- bin/                     主应用与依赖
|-- assets/                  运行所需资源
|-- models/                  内置超分模型
|-- user/                    默认用户数据目录和预设
|-- output/
`-- logs/
```

可指定配置和运行时：

```powershell
.\publish.ps1 -Configuration Release -Runtime win-x64
```

项目声明的运行时包括：

- `win-x64`
- `win-x86`
- `win-arm64`

## 本地数据与隐私

应用把运行时数据写在可执行文件旁边，使开发构建和发布构建保持一致。以下目录和文件属于本地数据、生成结果或敏感配置，不应提交到仓库：

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

`user/config/apiconfig.json` 保存 API Token 与账户缓存信息。当前版本会用 Windows DPAPI 加密 Token，但它仍然属于敏感文件，不应上传、截图公开或附在 issue 日志中。

隐私相关选项：

- 隐私模式：生成结果不自动保存到 `output/`，也不进入历史记录。
- 全局元数据消除：保存图片时自动移除元数据。
- 删除行为：可选择放入系统回收站或永久删除。
- 开发日志：仅在需要排查问题时开启，提交 issue 前请检查日志中是否包含 Token、账户信息或本地隐私路径。

## 外部资源与模型

仓库内置的小型超分模型位于 `models/upscaler/`，供本地超分工作流使用。

本地反推模型不是仓库的一部分。默认建议把用户自行下载的反推模型放在 `models/tagger/` 或其他本地目录，并在设置中选择该目录。模型目录需要包含 `.onnx` 模型和 `selected_tags.csv`，应用会根据 CSV 列结构兼容常见 WD14、SmilingWolf 和 PixAI Tagger 标签表。该目录已被 `.gitignore` 排除，因为模型体积较大且可能有独立授权条款。

`assets/` 下的 tagsheet、wildcards、预设和其他资源可能来自第三方或派生数据。如果你要重新分发、打包到其他项目或商业使用，请自行核对这些资源和额外模型的原始授权与再分发条款。

## 贡献

提交 issue 时请尽量提供：

- 复现步骤
- 预期行为和实际行为
- 截图或录屏
- 相关日志片段
- Windows 版本、.NET SDK 版本、显卡/驱动信息

提交 PR 前请确认：

- 改动范围聚焦，不混入无关格式化或运行时数据。
- 至少完成一次本地构建验证。
- 如果改动影响用户行为，同步更新 README 或相关文档。
- 如果改动影响发布布局，同步验证 `publish.ps1`。

不要提交：

- `user/`
- `output/`
- `logs/`
- `build/`
- `publish/`
- API Token、账户信息、本地私密路径或下载的大模型文件

更多说明见 `CONTRIBUTING.md` 和 `SECURITY.md`。

## 许可证

本项目使用 GPL-3.0 协议开源。完整文本见 `LICENSE`，如果使用该项目源码或设计参考请署名并指向该项目仓库。
