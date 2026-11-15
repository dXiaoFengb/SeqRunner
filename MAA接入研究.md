# MAA 接入研究（v1.12.2）

本记录只研究 MAA WPF 桌面版 6.21.4 与 6.22.0，基于官方文档、对应版本源码和用户提供的隔离验收报告；不读取本机 MAA 配置或日志。执行器默认仍使用人工确认，研究结果不是对真实游戏业务或无人值守的验收。

## 已确认资料

- [MAA 官方说明](https://docs.maa.plus/zh-cn/manual/introduction/others.html)介绍 `--config <配置名>` 可切换启动配置，也提供开始前与结束后脚本、定时启动等能力。切换配置不等于业务已经启动或成功；文档本身不能证明更新重启后每种配置的行为。
- 版本源码 [6.21.4](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.21.4/src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs) 与 [6.22.0](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.22.0/src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs) 的 `CheckAfterCompleted` 在 `AllTasksCompleted` 收尾路径执行结束脚本，并另外接收 `hasErrorTask` 控制后续动作；同版本代码还存在按配置在手动停止时运行结束脚本的路径。因此结束脚本出现本身不能证明全部业务成功。上述是源码分析，并非在本轮运行 MAA。
- [6.22.0 AsstProxy](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.22.0/src/MaaWpfGui/Main/AsstProxy.cs) 和 [6.21.4 AsstProxy](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.21.4/src/MaaWpfGui/Main/AsstProxy.cs) 区分任务链错误、停止、完成及全部任务结束，并在队列收尾检查错误状态。它们是潜在线索，但目前尚未证明外部执行器可可靠地将信息与本次运行、各任务和更新后的进程关联。

## 用户报告的隔离观察

- 用户报告：客户机独立副本从 6.21.4 更新至 6.22.0，启动计划触发，Runner 使用 `dotnet Runner.dll`，数据预置，beta 为受控命令；V3 观察到候选及其归属，未判业务成功或启动 beta。PID 6544 查询不明的具体身份未证明。
- 用户报告：两项 `AnnouncementInfo.DoNotShow`、`Update.DoNotShowUpdate` 均为 False 的对照出现公告和更新后版本信息；两项同时为 True 的实验观察到的采样中均未出现。该结果只说明本次组合设置与观察到的窗口情况，不能单独归因每个字段，也不保证其他版本、完整包或失败提示相同。仅建议在独立副本确认行为；不自动修改原程序配置。

## 接入结论与待验证事项

不能仅凭窗口消失、版本变化、进程退出、`AllTasksCompleted`、某个结束脚本或单条共享日志判定业务成功。真实业务、日志来源隔离、错误和停止与成功的区分、更新后本次运行关联、异常中断及长期运行均待验证；保留人工确认、未知暂停和现有 Job 边界。

若后续研究自动接续，先取得可重复的版本化成功与失败事件、可证明的本次会话标识及错误覆盖，再用受控失败和隔离环境独立验证；真实游戏业务另需授权。本轮不实现 MAA 适配、自动点击、自动修改第三方设置或网络服务。
