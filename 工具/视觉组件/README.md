# 本地 OCR 组件

V3 使用 `RapidOcrNet` 4.2.0 和本地中文模型识别文字。组件只识别传入的图片或内存数据，不连接网络，也不自行操作窗口；执行器负责窗口身份、画面和操作前检查。OCR 文字不能证明业务成功。视觉操作仅在用户明确配置后启用，识别结果不明时暂停。

## 新环境准备

运行需要 Windows x64 和 .NET 10 SDK。`nuget/` 与 `models/` 不纳入 Git：请根据 `组件清单.json` 从 [NuGet 官方站](https://www.nuget.org/)取得列出的各个精确版本 `.nupkg`，放入 `nuget/`；从 [RapidAI/RapidOCRCSharp 固定版本的模型目录](https://github.com/RapidAI/RapidOCRCSharp/tree/2c6e4d168e8c8cae3025f72e96479ea7be59b210/RapidOCRConsole/models)取得清单中的四个模型文件，放入 `models/`。取得后逐一核对清单中的文件大小和 SHA-256，不一致或不可用时不要启用 OCR。不会自动下载依赖。

在项目根目录执行 `dotnet build 工具/视觉组件/OcrHelper.csproj -p:NuGetAudit=false`；项目仅从本地 `nuget/` 还原。源码预览从本目录的构建输出查找组件，缺少组件或模型时视觉识别暂停。`OcrHelper.exe <图片路径>` 输出识别文字、置信度和坐标的 JSON，也支持由执行器通过标准输入传入图片，不落盘保存屏幕内容。

已检查本地 NuGet 包的许可元数据或随包许可：`RapidOcrNet` 为 Apache-2.0，SkiaSharp、ONNX Runtime 与 System.Numerics.Tensors 为 MIT，Clipper2 为 Boost Software License 1.0。模型所在仓库的固定版本没有找到独立许可证文件，模型权利及完整分发义务仍为 [待核实]，发布前须继续核对。本轮未制作发布包。
