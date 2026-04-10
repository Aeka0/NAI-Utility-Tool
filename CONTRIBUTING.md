# 贡献指南

感谢你对 `NAITool` 的关注。

## 开始之前

- 建议先阅读 [`README.md`](./README.md)。
- 推荐直接打开 `NAITool.sln`。
- 请优先在 `Debug | x64` 下完成本地构建验证。

## 本地开发

```powershell
dotnet build .\NAITool.sln -c Debug -p:Platform=x64
```

如果你修改了发布逻辑，也请额外检查：

```powershell
.\publish.ps1
```

## 提交 Issue

请尽量提供以下信息：

- 复现步骤
- 预期行为
- 实际行为
- 截图或录屏
- 相关日志片段
- 使用环境，例如 Windows 版本、.NET SDK 版本、显卡信息

## 提交 Pull Request

请尽量保证：

- 变更范围清晰，避免把无关修改混在同一个 PR 中
- 提交前已经完成本地构建
- 如果改动影响用户行为，请同步更新 README 或相关文档
- 如果改动影响发布结构，请同步验证 `publish.ps1`

## 不要提交的内容

以下内容属于本地数据、构建产物或敏感信息，不应提交：

- `user/`
- `output/`
- `logs/`
- `build/`
- `publish/`
- `user/config/settings.json`
- `user/config/apiconfig.json`
- `.env`、`.env.*`

尤其不要在 issue、PR、截图或日志里公开 API Token、账户信息或本地路径中的个人隐私信息。
