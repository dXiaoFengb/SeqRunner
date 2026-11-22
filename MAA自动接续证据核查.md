# MAA 自动接续证据核查

核查日期：2026-11-22。基线：v1.20.0、提交 `92ed250f12b785b0b409d0e8b5e02b69ff030e23`。这是阶段研究，不代表真实 MAA 自动接续通过。

## 结论

现有证据不足以安全解除未知子进程的暂停。本轮不修改产品判定，原有身份不明、更新线索和其他失败分支继续生效。进程角色缺失不能靠总数、主程序文件指纹、结束时间或普通日志补证。

## 代码与资料

- `RunnerEngine.cs` 在无更新线索且受管组累计多于一个进程时暂停；`JobEventObserver.cs` 对非根进程通知标记身份不明，还要求通知计数核对。因此已有真实运行的子进程会触发保守阻断。
- [Windows Job 文档](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)说明普通完成端口通知不保证送达，某些创建方式如 `Win32_Process.Create` 不自动入组。[统计字段文档](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information)说明累计数与活动数的含义，包括进程引用释放与活动数递减的关系。计数不提供完整身份和用途。
- [MAA 6.22.0 Bootstrapper 源码](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.22.0/src/MaaWpfGui/Main/Bootstrapper.cs)有外部更新器、进程内更新和重启路径；其普通 GUI 日志格式包含线程 ID，但没有给每行附加进程 ID。普通日志无法仅凭文件锚点证明每行来自本轮实例。
- 用户报告的真实 MAA 6.22.0 曾完成一次开始唤醒并正常关闭一次，受管组累计 33、部分身份不明后暂停，后项未启动。该历史观察不是本轮重测。

## 本轮隔离验证

- `dotnet run --project tests/Runner.Checks/Runner.Checks.csproj -p:RestoreSources=. -- probe-job-events`：8 项 PASS、Failures: 0。包含短进程、事件缺口注入、身份不可用和独立组外进程的已有夹具；无真实 MAA。
- 独立 PowerShell 进程创建 Job，将自身加入后依次运行 200 个 `cmd /d /c exit 0`，每个退出后释放对象，查询 Job 并排空通知：累计 401、活动 1、加入通知 401、退出通知 400；活动进程为探针本身。除控制进程和 200 条命令外，剩余进程的具体身份未采集，不能断言用途。此次通知吻合不能证明以后不丢失。
- 另一个隔离探针将自身加入 Job，运行 30 个短命命令并保留获取到的进程对象句柄；全部命令退出后读取累计 61、活动 2，释放对象后累计 61、活动 1。该观察与进程引用可能影响活动数相符，但不证明每个保留句柄都影响活动数，也不证明其他环境必然相同。
- 上述两个临时探针均为本轮内联执行、未保存独立脚本；结果可供定位，不是可重复测试套件。未测试有意缺失系统事件的真实投递机制、突破 Job、WMI 组外启动、完整更新模拟或真实 MAA。

## 后续条件

需要进一步建立适用于 MAA 6.22.0 的、能把本轮业务与相关进程及更新状态可靠关联的证据来源，并用受控反例验证：身份丢失、更新器或重启进程、组外服务、共享日志追加和文件变更后恢复均不得误放行。候选方向包括目标程序主动提供带会话标识的完成协议，或在受控环境验证具有完整性保证的系统级进程创建证据；前者需要目标程序配合，后者的权限、覆盖范围和可行性均 [待核实]。不得将任一方向当成已可实施的保证。

真实 MAA 或游戏业务需要针对具体验收内容另行授权并由验收方执行。多日无人值守、真实休眠恢复和更新后接续均 [待核实]。

## 2026-11-22 续查：本轮关联与反例

- MAA 6.22.0 的 [Bootstrapper](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.22.0/src/MaaWpfGui/Main/Bootstrapper.cs) 先处理同安装位置的单实例互斥；若已有实例，可能向已有实例发出唤醒信号后退出。其待更新包路径包括交给外部更新器，以及进程内应用后再重启。不能仅凭 V3 创建了根进程便推定业务由该根进程完成。
- V3 默认更新关键词是 `正在更新`、`更新中`、`正在重启`、`重启中`、`updating`、`restarting`。上述 MAA 源码中的待更新包检测英文日志并不包含这些默认词；用户自定义词可能不同。未观察到默认词不能证明没有更新，且这里的源码差异不代表已经实测对应日志落盘。
- 版本固定的 [AsstProxy](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.22.0/src/MaaWpfGui/Main/AsstProxy.cs) 用 `AsstCreateEx` 注册界面自身的回调；`CallbackFunction` 将 JSON 解析并派发给界面处理，原始 JSON 的 Console 输出在此处被注释。这只能说明所核查的回调路径；是否存在其他完整、可供 V3 识别本轮来源的外部记录 [待核实]。
- 本轮新增 `probe-maa-evidence-counterexamples` 隔离夹具。普通等待子进程与悄悄更改隔离资源文件的子进程输出相同业务完成日志；两种情况均被现有 MAA 判定暂停，未启动后项。这个模拟证明不能仅因看到相同完成文字和子进程数就把二者视为等价；它不代表重现 MAA 真正的更新流程。
- 独立进程向同一测试日志追加开始与完成文字时，旧内容锚点仍未改变，解析器也能识别出完成；这验证日志锚点不认证写入来源。测试文件中途改变后恢复原内容和写入时间，前后哈希与时间一致；这验证有限次指纹采样不能排除采样间变化。
- 上述四项专项结果均为 PASS、Failures: 0。现有“外部实例无法写入同一日志”的用户声明是使用前提，不能由该锚点独立证明；对更新、重启和组外行为仍未建立可安全放行的充分依据。保留现有阻断，未修改产品功能。
- `probe-maa-resume` 专项退出码 0、Failures: 0。完整回归首次在沙箱内因本机模拟监听返回错误 5 中止，沙箱外在同一隔离夹具范围重跑退出码 0、Failures: 0；未统计本轮完整回归项目总数。未运行真实 MAA、游戏、真实更新或源码预览。
