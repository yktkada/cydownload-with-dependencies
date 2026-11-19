# CyDownload

把 Cydia 源的 `.deb` 与其**依赖闭包**离线归档，导出**依赖有序**的 deb 集合，供刚刷完机的 iOS 4–8 设备
通过 `Cydia/AutoInstall` 安装。C# / WPF / MVVM / .NET 10。

- [设计说明](docs/design.md) —— 分层、数据模型、求解、内容寻址 pool、导出语义
- [使用说明](docs/usage.md) —— 抓索引 → 算闭包 → 下载/导出 → 放到设备

## 构建

```powershell
dotnet build CyDownload.sln -c Release --nologo
dotnet test  CyDownload.sln -c Release --nologo
```

需要 .NET 10 SDK；运行需要 .NET 10 桌面运行时。

## 目录

- `src/CyDownload.Core` —— 纯逻辑（Debian 语义 / 索引 / 求解 / 下载 / 导出）
- `src/CyDownload.App` —— WPF 界面
- `tests/CyDownload.Tests` —— 单元测试
- `tools/CyDownload.Probe` —— 命令行自检台

## 许可

MIT，见 [LICENSE](LICENSE)。

## 免责声明

本项目**不包含、不托管任何 deb 包或仓库内容**，只提供客户端工具；使用时请自行确认所访问来源的合法性与可用性。
本项目与 Cydia / Saurik 官方无关。
"@
