# 脚本界面日志来源核查

本记录只说明可核实的来源，不将诊断日志直接认定为用户界面日志。实际安装版本可能与所查源码不同。

| 预设 | 已核实的事实 | 本轮接入状态 |
| --- | --- | --- |
| MAA | [v6.20.0-beta.1 TaskQueueViewModel](https://github.com/MaaAssistantArknights/MaaAssistantArknights/blob/v6.20.0-beta.1/src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs) 的界面日志经 `DisplayLog` 写入与其他信息共享的记录器；仅凭记录器名称不能区分全部界面内容。[TaskQueueView](https://github.com/MaaAssistantArknights/MaaAssistantArknights/blob/v6.20.0-beta.1/src/MaaWpfGui/Views/UI/TaskQueueView.xaml) 提供普通和卡片两种日志布局，所见日志滚动容器没有明确的辅助标识。 | 用户选择窗口并框选区域后，以本机 OCR 读取本轮受管窗口；受控窗口通过，真实 MAA 和不同 DPI 待验收。窗口布局变更但客户区尺寸不变时不能自动识别，需要用户重新框选。 |
| 绝区零一条龙 | [官方日志工具](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/src/one_dragon/utils/log_utils.py) 使用 `.log/log.txt` 和按日轮转，但也允许项目日志分流；尚未确认文件内容与界面完全对应。 | 界面来源待核实。 |
| MaaEnd | [官方项目](https://github.com/MaaEnd/MaaEnd) 有多个前端和版本；参考项目在特定启动方式下读取子进程标准输出，不能证明当前 GUI 预设暴露相同输出。 | 当前 GUI 界面来源待核实。 |
| 三月七小助手 | [官方日志界面](https://github.com/moesnow/March7thAssistant/blob/main/app/log_interface.py) 从它启动的子进程读取输出并展示；日志页面对象名为 `logInterface`，只读 `PlainTextEdit`；游戏悬浮窗正文对象名为 `gameLogOverlayBody`，展示末尾有限行。 | 优先按唯一标识读取受管页面，否则读取属于同一受管 PID 的唯一悬浮正文；受控 WPF 窗口通过，真实 Qt 程序是否暴露标识以及控件类型仍待验收。悬浮窗仅含末尾行，无法保证完整。 |
| BetterGI | [官方项目](https://github.com/zc990320/BetterGI) 提供界面日志展示；尚未确认稳定可读取的数据流及适用版本。 | 界面来源待核实。 |

现有原始明细继续从用户配置的日志文件中只采集本轮新增内容。无可确认界面来源时明确提示，不用现有通俗转译伪装成脚本界面日志。

用户于 2026-10-18 同意通过 Windows 界面辅助接口只读本轮受管窗口中的日志控件，并允许手动框选及本机 OCR；不授权运行真实业务软件。编辑时只列出与所配置可执行文件完整路径匹配的窗口供用户选择；运行时仅验证和读取本轮受管进程的窗口。

窗口读取先以首次快照为基线，此前内容不计入本次日志，因此启动初期内容可能遗漏。OCR 的近似对齐明确标记可能有错字、重复或遗漏；无法确认重叠或读取中断时标记可能不完整。界面日志与配置的原始文件日志分别存储和查看，并同时受压缩、清理与运行记录上限约束。其他软件的实际适配情况见上表。
