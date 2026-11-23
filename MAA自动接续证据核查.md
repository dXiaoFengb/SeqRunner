# MAA 自动接续证据核查

核查日期：2026-11-22。基线：v1.20.0、提交 `92ed250f12b785b0b409d0e8b5e02b69ff030e23`。这是阶段研究，不代表真实 MAA 自动接续通过。

> 阅读提示：本文件按时间追加，前面章节反映当时认知。2026-11-23 的「续查：方案 E 前提的源码核对…」一节已用 `v6.22.0` 源码推翻两处旧说法——MAA 日志并不跟随启动工作目录，`asst.log` 行头带 `[Px…]` 且内容是英文结构化回调；判断是否可恢复自动接续，以最新一节为准，旧章节保留作历史对照。

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

## 2026-11-23 续查：MaaCore 结构化回调日志

- 更正并细化此前“是否存在其他完整外部记录待核实”：MAA 6.22.0 [Assistant.cpp](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.22.0/src/MaaCore/Assistant.cpp) 的 `append_callback` 在将消息加入队列前，以 `Log.info` 写入回调类型与 JSON；[Logger.hpp](https://raw.githubusercontent.com/MaaAssistantArknights/MaaAssistantArknights/v6.22.0/src/MaaCore/Utils/Logger.hpp) 指向 `debug/asst.log`，日志前缀包含进程 ID，INFO 默认启用。这是固定版本源码证据，真实运行是否完整写入 [待核实]。
- 核心源码区分任务开始、错误、停止和完成；`finished_tasks` 包含已运行任务，即使任务报错也可能被记录，不能单靠 `AllTasksCompleted` 断定全部成功。回调在写日志之后才排队，不能仅凭该行证明界面最终接收或游戏内真实结果。
- 这是可研究的本轮关联候选，但日志中的 PID 与任务 ID 不等于不可伪造的会话身份；PID 复用、日志被替换或截断、重启、日志滚动、写入失败和多个实例仍须验证。源码中日志文件可滚动且写入打开失败时存在回退，不能将日志存在或 PID 一致视作完整性证明。
- 本轮没有读取或修改用户的实际 `asst.log`，没有将该线索接入产品成功判定；下一步如研究此方案，应先使用隔离合成数据验证实例关联、事件遗漏、重启和失败处理，再由验收方在具体授权下验证真实行为。

## 2026-11-23 受控证据：组外进程追加日志的完整链路

- 目的：此前只证明日志锚点不认证独立写入者，尚未证明“受管组只有一个根进程、由组外进程追加完成文字”的完整执行链路中 V3 一定暂停。该问题原为 [待核实]，本轮先用隔离夹具实测再记录，不预写结论。
- 夹具：仅改 `tests/Runner.Checks/Program.cs`，未改 `src/` 任何文件。任务使用 `MAA日志` 完成方式、独占日志确认和自动推进；根夹具 `fixture-maa-chain-root` 不写开始和完成文字，收到释放事件后以退出码 0 退出；组外夹具 `fixture-maa-chain-outsider` 由测试进程独立 `Process.Start`，核实不属于受管 Job，等就绪事件后向同一临时日志追加 `开始任务: A` 与 `任务已全部完成！`，写回执后正常退出。顺序由三个命名事件（就绪/已写/释放）控制，不依赖随机等待。对照案 `root-writes-log` 由根进程自己追加同样文字。
- 结果（`probe-maa-external-writer`，退出码 0、`Failures: 0`）：组外写入案 `advanced=True`、`state=已结束`、`successLine=True`、`groupPause=False`。事件依次为 `Job 进程加入通知：PID 29388`（只有根进程）、`Job 进程退出通知：PID 29388`、`Job 事件核对：累计进程 1；加入通知 1；退出通知 1；数量已核对；进程用途及通知完整性仍需核实`、`MAA 日志观察到任务开始；业务结果仍待确认`、`MAA 日志观察到完成文字；仍需等待进程退出和最终判定`、`MAA 日志观察到任务开始与全部完成；日志文字不能证明每行来源`、`maa 成功，退出码 0；依据：本次 MAA 日志任务开始与全部完成`、`已启动 after-outside-writer`、`队列已结束`。组外夹具回执为 `PID=…;READY=1;INJOB=1;WRITTEN=1;`，其中 `INJOB=1` 表示它继承了宿主 Job，不在 Runner 的 Job 内；Runner 侧证据是累计进程 1 且没有该 PID 的加入通知。
- 结论（受控夹具，不是真实 MAA）：单根受管组的累计进程数为 1，不触发累计进程暂停；Job 事件只涉及根进程，不标记身份不明；组外进程追加的完成文字被当作本轮证据，V3 判成功并启动下一项。用户此前担心的“外部写入是否一定被暂停”在本夹具下得到相反结果：不会暂停，会误判成功。既有真实运行中的暂停来自累计多于一个进程或身份不明，不能据此认定日志来源已被认证。
- 边界：该组外夹具是本轮测试自行创建、持有句柄并正常退出的受控进程，未接管或结束其他进程；未涉及真实 MAA、其更新器、游戏或用户日志。一次通过不证明所有时序、共享日志或真实文件行为相同，也不能证明取消现有阻断是安全的。
- v1.20.2 复验更新（2026-11-23）：同一 `probe-maa-external-writer` 现在为 `advanced=False`、`closeCalls=0`，最终 `MAA 日志来源无法确认`。上面 `advanced=True` 是 v1.20.1 的历史记录，不是当前行为；本节保留原样以免改写历史。

## 2026-11-23 受控证据：短命进程观察能力

- 夹具：`fixture-short-parent 6 <delay>` 起 6 个 `fixture-short-child`，父进程等全部退出后返回；任务使用 `MAA日志` 以获得 Job 事件观察。异常情形只使用既有 internal `JobEventObserver.Record(message,pid,open,report)` 注入查询失败、重复 PID 和缺少加入的退出，未改产品代码。
- `probe-short-lived-processes`（退出码 0、`Failures: 0`）三案：
  - `normal-wait`（子进程各存活 120ms，测试以 20ms 采样）：累计 7、加入通知 7、退出通知 7、取得句柄和创建时间 7、取得路径 5~7（跨轮在 5 与 7 之间波动）、组归属 7、无法确认 0。
  - `quick-exit`（子进程立即退出，20ms 采样）：累计 7、加入 7、退出 7、句柄和创建时间 7、路径 5~7、组归属 7、无法确认 0。
  - `quick-exit-production-poll`（子进程立即退出，按产品 200ms 轮询节奏）：累计 7、加入 7、退出 7，但句柄和创建时间只成功 1、路径 1、组归属 1、无法确认 6，原因为 `process-open-failed`（采样时进程已不存在）。
  - 三案 `summary` 均含 `不明：不能仅凭 PID 证明通知与当前句柄属于同一身份`；`purposeKnown=0`，即没有任何一项用途被证明。
- 注入案：查询返回空的加入通知记为身份未确认；重复 PID 记为“重复或 PID 复用”；无加入的退出记为“退出事件缺少加入事件”；通知数与累计数不一致时 Summary 明确不明。全部 PASS。
- 含义与边界：通知送达时 Job 累计数可与已知子进程数吻合，但“事件数量吻合”“实例身份被可靠核对”“用途已知”是三件不同的事；采样间隔大于进程存活时间时，身份、路径和组归属都无法取得，不能保证捕获全部短命进程。以上均为受控夹具观察，不代表真实 MAA 的进程构成，也未验证通知丢失的真实投递机制。

## 修复方向（未实施，待用户认可）

- 现有安全实际依赖“写入者恰好在受管组内”：组内多进程会被累计进程数拦下，组外的写入者不会。仅凭日志文字、文件锚点、通知计数或进程总数无法区分写入来源。
- 候选方案 A（优先核实）：要求完成证据能与本轮根进程实例关联。MaaCore 的 `asst.log` 前缀含进程 ID，但 MAA 普通 GUI 日志没有每行进程 ID；真实 6.21.4/6.22.0/6.23.1 日志格式是否包含可核对的本轮实例标识 [待核实]。若包含，可要求所有证据行与根 PID 及创建时间一致，且该方案仍需验证日志滚动、截断和失败。
- 候选方案 B（若 A 不可行）：把 `MAA日志` 的成功判定收紧为“需要目标程序主动提供的会话标识或系统级创建证据”，在取得前把仅有日志文字的结果判为不明。这属于修改产品判定，须先报原因、范围和验证方法并取得用户认可，本轮未改。
- 候选方案 C（不改产品）：在文档和人工验收中明确该误判路径，并建议存在外部更新器或组外写入者时不要启用 `MAA日志` 自动接续。该方案不修复缺陷，只降低误用风险。

## 2026-11-23 续查：本轮实例、业务完成与短命进程身份（v1.20.2）

结论：本轮新增的隔离证据仍不足以安全恢复 MAA 自动接续。`asst.log` 的 PID 只能说明“写日志的进程”，不能说明它是 V3 本轮根进程；`AllTasksCompleted` 不等于全部任务成功；生产节奏下短命进程的身份采集不完整。v1.20.2 的“来源无法确认即暂停”应继续保留，产品判定未改。

### 1. `asst.log` 能说明什么

- 源码事实（沿用既有固定版本引用，本轮联网复核失败，未新增源码结论）：MAA 6.22.0 的 `Assistant.cpp` 在回调入队前用 `Log.info` 写回调类型和 JSON；`Logger.hpp` 指向 `debug/asst.log`，日志前缀含进程 ID，INFO 默认启用。真实运行是否完整落盘仍 [待核实]。
- 只读副本位置：`参考/MAA-isolated-v170-a603afd/debug/asst.log`，同目录另有 `asst.bak.log`。它是参考目录里的隔离副本，不是用户实时日志，不能当作真实验收。按 `CONSTRAINTS.md` 只读资产条款，本轮已移除此前摘录的字节数、最后写入时间、各类计数、文件行号和完整日志行；只保留路径引用。
- 上一轮曾在该副本中观察到“错误后仍出现完成回调、完成后仍出现停止、同一文件跨多个进程追加、固定 uuid 不随进程变化”四类关系；这些具体值已不在本报告中，无法就地复核，因此**该副本相关的统计结论统一标 [待核实]**。其中可由独立证据支持的部分改由下一条的合成夹具承担。
- 合成夹具（`tests/Runner.Checks/Program.cs` 的 `MaaInstanceEvidence`，时间、PID、线程号、任务号与 uuid 全部虚构）独立验证三条格式关系：① `SubTaskError` 可以先于 `AllTasksCompleted`，且失败任务仍留在 `finished_tasks`；② `TaskChainStopped` 可以晚于同一 `taskid` 的 `AllTasksCompleted`；③ 同一 uuid 可以跨多个 PID 与多个批次出现。它证明的是“日志格式允许这些时序”，**不是**真实日志里的发生频率。
- 当前 `MaaLogEvidence` 只匹配中文/英文完成文本，不解析 `Assistant::append_callback`，也不看 `Px`；合成行断言英文回调对 Started/Succeeded/Failed/Stopped 四态均为 false，带 `[Px…]` 前缀的中文“开始任务/任务已全部完成”仍被判 Started+Succeeded，证明当前解析器完全不看 PID 来源。
- 日志完整性：现有 `MaaLogAnchor` 只比较上一段 256 字节窗口，能发现之前区域被改写，但不能发现“替换成相同字节”、未来滚动、追加期间截断或写失败；真实日志的追加、替换、截断、滚动和写失败仍 [待核实]。本轮没有读取用户真实日志，也没有把 `asst.log` 接入产品成功判定。

### 2. 短命子进程身份采集

- Job 通知能给出加入/退出事件和 PID，但通知到达后还要用 `Process.GetProcessById`、创建时间、路径和 Job 归属去核对同一个实例。进程在查询前退出时，句柄和创建时间就取不到。
- 本轮 `probe-short-lived-processes` 的受控结果：20ms 采样下 7 个进程的句柄/创建时间可取得 7 个，路径 5~6 个，组归属 7 个；按产品 200ms 节奏时，最新一次只有 2 个句柄/路径/组归属可核实、5 个 `process-open-failed`，此前一次为 1 个可核实、6 个不可核实。波动说明“某次数量吻合”不能保证“每次都采集完整”。
- 注入 `Record(6,999999)` 两次和 `Record(7,888888)` 一次后，通知重复/PID 复用、退出缺少加入、数量不一致都被标记为不明；这是反例能力，不是身份已解决。
- 进程句柄、创建时间、完整路径和 Job 归属可以回答“这是不是一个仍在同一 Job 中的进程实例”，但不能回答“它是不是 MAA 更新器、重启器或业务子进程”。路径相同也不等于用途相同；PID 相同更不等于同一实例（PID 可复用）。
- 可行的系统级方向及缺口（本轮只做源码核查，没有安装或开启任何追踪）：
  - ETW `Microsoft-Windows-Kernel-Process` 一类进程事件：可补充进程创建/退出和映像信息；通常需要管理员/实时会话和缓冲配置，存在事件丢失、缓冲溢出、时间戳与 PID 复用问题，且仍需和应用层会话标识关联。[待核实]
  - Sysmon / Procmon：需要安装服务/驱动、管理员权限并改变系统配置；本轮明确未安装、未开启，覆盖率和部署成本 [待核实]。
  - Windows 安全审计 4688/4689：需要改审计策略（通常管理员），命令行是否记录取决于策略；仍只能证明“某 PID 启动过某映像”，不能证明业务完成。[待核实]
  - WMI `Win32_ProcessStartTrace`：需要相应权限，事件可能延迟或丢失，且不提供可靠命令行/业务角色。[待核实]
- 因此，系统级追踪最多补“进程创建事实”，仍不能单独补“本轮业务成功”；把二者绑在一起还需要目标程序提供会话标识、签名回执或其他可验证协议。

### 3. 隔离反例矩阵

本轮新增 `probe-maa-instance-evidence`，所有案例都由测试自行创建、持有并正常退出的夹具进程执行；未接管或结束其他进程；使用 `MaaVersionSupported=_=>true` 仅为绕过版本门控，不声称真实版本支持。每案均设置 `MaaCloseOnCompletion=true` 并统计关闭请求，实际 `closeCalls=0`。

| 案例 | 构造 | 观察到的证据 | 能否区分 | 实际队列结果 |
| --- | --- | --- | --- | --- |
| `success` | 根进程写开始/完成，正常退出 | 完成文字与退出码一致 | 不能证明写入者就是根实例 | 暂停：`MAA 日志来源无法确认` |
| `short-child` | 根进程起一个 20ms 短命子进程，再写相同完成文字 | Job 看到子进程，但查询时已退出 | 能看出“有子进程”，不能证明用途 | 暂停：`存在未确认的受管子进程` |
| `update` | 旧轮完成文字后启动更新子进程并写新轮完成 | 更新线索/Job 身份不完整 | 不能证明新轮业务成功 | 暂停：`Job 事件或进程身份无法完整核实` |
| `error` | 先写 `任务出错:`，后写完成 | 错误证据仍在 | 能区分错误，不能据此成功 | 暂停：`MAA 日志报告任务错误` |
| `stop` | 先写 `已停止`，后写完成 | 停止证据仍在 | 能区分停止 | 暂停：`MAA 日志报告任务停止` |
| `roll` | 把日志移走为 `.1`，在新文件写完成 | 文件身份变化 | 能发现替换 | 暂停：`日志文件已替换` |
| `replace` | 把日志移走为 `.old`，写新完成 | 文件身份变化 | 能发现替换 | 暂停：`日志文件已替换` |
| `truncate` | 覆盖原日志为不同内容 | 既往区域锚点变化 | 能发现截断/改写 | 暂停：`MAA 日志已有内容发生变化` |
| `job-missing` | 注入 `MarkUncertain("测试：通知缺失")` | Job 通知不完整 | 能标记证据不足 | 暂停：`Job 事件或进程身份无法完整核实` |

补充复用的既有专项：组外进程写入案（`probe-maa-external-writer`）中，组外夹具 `INJOB=1`（继承宿主 Job、不在 Runner Job 内），Runner 只看到根进程的加入/退出通知；最终仍为 `MAA 日志来源无法确认`，人工 `Continue()` 只跳过、不记成功。短命进程、保存失败、用户停止、日志被占用等案也继续暂停。以上均为受控夹具，不是真实 MAA 验收。

### 4. 本轮改动与验证

- 实际改动：只改 `tests/Runner.Checks/Program.cs`（新增 `probe-maa-instance-evidence`、6 条合成回调断言、9 案矩阵、`fixture-maa-short-child`、`fixture-maa-roll`）、`MAA自动接续证据核查.md`、`人工验收流程.md`、`TASK.md`、`handoff.md`、专项计划与本地记录/备份。`src/` 产品代码零改动，开发版本未推进。
- 构建：`dotnet build tests\Runner.Checks\Runner.Checks.csproj -c Debug`，退出码 0，0 警告 0 错误。
- 专项（均退出码 0、`Failures: 0`）：`probe-maa-instance-evidence` 15 PASS；`probe-maa-resume` 102 PASS；`probe-maa-external-writer` 13 PASS；`probe-short-lived-processes` 8 PASS；`probe-maa-evidence-counterexamples` 4 PASS；`probe-maa622-clues` 13 PASS；`probe-job-events` 8 PASS；`probe-maa-start` 17 PASS。
- 完整回归：第一次 `probe-regression-local` 303 PASS 后在本机既有 race 测试 `Program.cs:103` 的 `File.Move` 报 `UnauthorizedAccessException`，无 FAIL 行、`Failures: 1`；第二次和第三次重试均为 446 PASS、1 FAIL、1 SKIP，唯一 FAIL 为 `MAA controlled start refuses another process window`（SKIP 为设计内需单独本地管道权限的 controlled resume）。同一窗口项单独运行 `probe-maa-start` 17 PASS、0 失败；race 文件 ACL 允许 Modify、同目录 `Move-Item -Force` 可成功。失败点均可单独通过，判断为本机桌面焦点/文件瞬时占用，不据此宣称完整回归通过，也不据此改产品。
- 证据日志目录：`对话记录\证据\v3-maa-instance-evidence-20261123\`。

### 5. 是否足以恢复自动接续

不足以。当前没有一种已核实证据能同时回答“这是本轮根进程实例”“这次业务真的全部成功”“没有更新/重启或组外写入”；`asst.log` 的 PID/`taskid`/`uuid`、Job 计数、路径、创建时间和文件锚点都各自有缺口。把任一单项包装成成功依据都会重复 v1.20.1 的误判路径。保留 v1.20.2 暂停是当前唯一有证据支持的结论。真实 MAA、真实更新、多日无人值守、休眠恢复和 6.23.1 均未执行，仍 [待核实]。

### 6. 下一步建议（等待用户认可）

- 方案 A 仍需补“本轮实例”证据：可在验收方另行授权下，对一次隔离的 MAA 6.22.0 运行只读采集 `asst.log`，核对完成回调的 `Px` 是否与 V3 根 PID 一致、滚动/重启后是否变化；这只解决“写日志的进程”，仍不解决业务成功，需同时记录错误/停止/`finished_tasks`。
- 若 A 仍不能绑定业务，优先评估目标程序主动提供带随机 nonce/会话 ID 的完成回执；这需要 MAA 侧配合，不能由 V3 单方面假设。
- 系统级 ETW/Sysmon/审计只能作为“进程创建事实”的补充，需单独评估权限、部署和事件丢失；本轮不安装、不开启，也不建议直接把进程路径当业务用途。
- 方案 B（收紧产品判定）和任何白名单/自动重试都不在本轮范围；若要做，须先给出具体判定、反例和验收方法，再由用户认可。

## 2026-11-23 参考样本边界整改与证据通道实测

本轮只改测试夹具、证据报告、人工验收步骤和项目记录，`src/` 零改动，产品判定仍为 v1.20.2 的“来源无法确认即暂停”。这里记录三件事：样本边界怎么处理、四条证据通道各自能证明什么、下一步最快且安全的方案。

### 1. 参考样本边界的实际处理

- 测试夹具：`tests/Runner.Checks/Program.cs` 的 `MaaInstanceEvidence` 原先有 6 行回调样例几乎逐字取自参考副本。现已改为完全虚构的数据（虚构日期、虚构 `Px`/`Tx`、`uuid":"synthetic-fixture-0001"`、虚构 taskchain 与 taskid），并在注释中声明“entirely fictional；测试不读取任何真实 MAA 日志”。保留的四条关系是：错误后仍可出现完成回调、完成后仍可出现停止、同一 uuid 可跨多个 PID 出现、PID 或 taskid 不能单独认证本轮实例。断言同步改为虚构 taskid 与虚构首个任务名。
- 证据报告：本文件 `### 1. asst.log 能说明什么` 已删除样本字节数、最后写入时间、各类计数、文件行号和完整日志行，只保留 `参考/MAA-isolated-v170-a603afd/debug/asst.log` 路径；原先由该副本支撑的结论统一标 [待核实]，其中“格式允许错误后完成、完成后停止、跨 PID 追加”三条改由上面的合成夹具独立验证。
- 任务卡与交接：`TASK.md`、`handoff.md` 中复述的样本派生数值（跨多个 PID、完成与停止的毫秒间隔等）已删除，改为不含数值的表述并指向本报告。
- 仍含相关派生值的可编辑位置：`对话记录/04-问题、风险与恢复.md:1227`（历史记录，按用户要求只定位、不改写）。Git 历史 `8800fbb` 及更早提交也包含原值；本轮不改写历史，只报告。旧备份目录 `对话记录/Git提交备份/v3-maa-instance-evidence-20261123-1823/` 与全局 `内容/` 里可能仍有含原值的旧文件副本，未删除、未改动，是否清理需用户单独授权。
- 本轮备份：`对话记录/Git提交备份/v3-reference-samples-20261123-1915/` 只覆盖本轮会改的 5 个可编辑文件及其修改前字节。**缺项**：`参考/` 原件、`对话记录/` 历史记录、Git 对象不在备份范围内，也不应复制；不能宣称“修改前全部内容均已备份”。

### 2. 证据通道实测（`probe-maa-channel-boundary`）

新增 5 项隔离夹具断言，全部由测试自行创建并正常退出，未接管或结束任何外部进程；退出码 0、`Failures: 0`。

| 案 | 构造 | 实测结果 | 说明 |
| --- | --- | --- | --- |
| `pid-forged` | 根进程存活且不写日志；组外进程写入带 `[Px<根PID>]` 前缀的合成完成行 | `pidBoundAccepted=True`，`allFromPid=True` | **按 PID 绑定证据行可被组外进程伪造**，不能单独作为“本轮实例”认证 |
| `stdout-owned` | 根进程用重定向 stdout 写完成行，同目录日志另含外部标记 | 管道 `started=completed=True`，`pipeCarriedOutsideMarker=False` | 受管 stdout 管道只承载根进程自己写的内容，这一点成立 |
| `stdout-file-sided` | 目标把完成只写进文件（模拟 MAA 写 `asst.log`） | 管道 `pipeStarted=False pipeCompleted=False`，文件含完成文字 | **stdout 通道对 MAA 这类写文件的程序完全看不到证据**，不能作为修复 |
| `receipt-pipe`（正例） | 根进程作为客户端连 V3 命名管道并写回执；外部进程另连一次 | `clientPidIsRoot=True`，`outsiderPidIsRoot=False` | 回执通道可用客户端 PID + 创建时间绑定到根实例，外部进程无法冒充 |
| `receipt-pipe`（反例） | 同上，换外部进程连接 | 身份核对失败 | 证明该通道能区分“根实例”和“其他进程” |

结论：能真正把“回执”与“本轮实例”绑定的是命名管道回执，但它要求目标程序**主动连接并写回执**；PID 前缀可伪造，stdout 对写文件的程序无效。

### 3. 候选方案比较

| 方案 | 绑定本轮实例 | 判定业务成功 | 反例验证 | 依赖与成本 | 红线 |
| --- | --- | --- | --- | --- | --- |
| A. 按 `Px` 匹配根 PID 的日志行 | 否（已实测可被伪造） | 否 | 通过（证伪） | 无新增依赖 | 不触碰 |
| B. 受管 stdout/stderr 捕获完成文字 | 是（仅对写 stdout 的程序） | 部分 | 通过 | 无 | MAA 写文件，故无效 |
| C. 目标主动连命名管道写会话回执 | 是（PID+创建时间） | 需回执含业务结果 | 通过 | 需修改 MAA 或加受信中间层；跨版本、跨更新升级未知 [待核实] | 不碰第三方配置、不改 MAA |
| D. Job 事件 + 保留句柄 + 创建时间 | 只能证明“仍在同一 Job 内” | 否 | 部分通过 | 现有能力；短命进程 200ms 采样仍不完整 | 不触碰 |
| E. 独占工作目录 + 解析 `asst.log` 结构化回调 + `finished_tasks`/错误交叉 + Job 核对 | 否（前提被源码否定，见 2026-11-23 续查 §1） | 中（见下） | 需新增夹具 | 只改 V3 启动参数与解析；**MAA 日志位置与工作目录无关**，无法靠启动目录隔离 | 不改 MAA 配置；需用户认可后才动产品 |
| F. ETW / Sysmon / 4688 / WMI 进程创建事件 | 补“进程创建事实” | 否 | 未测 | 需管理员权限或改系统配置；本轮明确不安装 | 触碰红线，不做 |

方案 E 的判定草案（仅提案，未实施）：

> 2026-11-23 源码核对后的修订说明，原草案保留作历史对照：第 1 条前提不成立（MAA 启动即把 CWD 改为 exe 目录，日志固定在 `UserDir/debug/asst.log`，无配置入口）；第 2 条事件名有误（6.22.0 无 `SubTaskError`，实际为 `TaskChainError`；`AllTasksCompleted` 只表示队列跑完，不代表每个任务成功）；第 4 条“`Px` 属于根进程”已被实测证明可伪造。修订后的可行部分见下文 2026-11-23 续查 §5 方案 G。

1. V3 启动 MAA 时把工作目录设为本轮独占的受控目录，使 `debug/asst.log` 路径本轮唯一；不修改 MAA 配置文件。
2. 只解析结构化回调：`TaskChainStart`、`AllTasksCompleted`、`SubTaskError`、`TaskChainStopped`；不看中文 GUI 文本。
3. 业务成功要求：存在与本轮 taskid 一致的开始与完成；`finished_tasks` 中所有 taskid 都没有对应 `SubTaskError`；完成之后没有同 taskid 的 `TaskChainStopped`。
4. 归属要求：完成行的 `Px` 属于根进程或本轮 Job 内已核实的进程；日志文件身份（卷号+文件索引）与锚点不变；Job 无未确认成员。
5. 任一条件不满足即暂停，不自动重试、不放行白名单。

残余风险：本机任意进程只要能找到该独占目录，仍可写入并伪造 `Px`；方案 E 消除的是“MAA 自身更新/重启/多实例/普通组外程序”的误判，**不能消除同机恶意伪造**。要消除后者只能走方案 C（目标程序配合）或系统级受信通道。

### 4. 推荐的最快安全路径

1. **立即（不需要产品改动）**：保留 v1.20.2 暂停；把本节限制写进人工验收（已加 2026-11-23 节），并在界面/文档提示“存在外部更新器或组外写入者时不要启用 `MAA日志` 自动接续”。
2. **下一步产品改动（需用户认可）**：实施方案 G（见 2026-11-23 续查 §5、§6），只改 V3 的日志采信与判定：读取 `UserDir/debug/asst.log` 的结构化回调、用独占句柄 + Restart Manager 把写入者绑定到本轮根进程、只在根进程存活期间采信；不改 MAA、不改配置格式、不依赖启动工作目录；新增夹具覆盖：组外进程抢先占位、退出后追加、滚动/替换、错误后完成、完成后停止、`finished_tasks` 与开始集合不一致。
3. **若要求抵御同机任意进程伪造**：必须走方案 C，需要 MAA 侧或受信中间层配合；本轮不具备该条件，也不应假装已解决。

### 5. 本轮无法取得的证据

- 真实 MAA 的 `asst.log` 是否按上述格式完整落盘、滚动与写失败行为、6.23.1 是否一致：均需验收方在授权隔离环境只读采集，本轮 [待核实]。
- MAA 是否按工作目录写 `debug/asst.log`（方案 E 的前提）：**源码已否定**（`Bootstrapper.OnStart` 强制 `Directory.SetCurrentDirectory(AppContext.BaseDirectory)`，`Logger` 用 `UserDir/debug/asst.log`，`set_directory` 被注释）；真实运行仍 [待核实]。
- 多日无人值守、休眠恢复、真实更新后接续：未执行，[待核实]。

## 2026-11-23 续查：方案 E 前提的源码核对、事件模型补全与三条新夹具（研究轮，`src/` 零改动）

本节只写研究结论与隔离夹具结果；产品判定未改，v1.20.2 的“来源无法确认即暂停”继续生效。

### 0. 结论先行

方案 E 的两个前提都不成立：

1. **改启动工作目录不能改变 MAA 日志位置。** `Bootstrapper.OnStart` 在任何 MaaCore 代码加载前执行 `Directory.SetCurrentDirectory(AppContext.BaseDirectory)`，`Logger` 的目录来自 `UserDir`，而 `Logger::set_directory` 在 6.22.0 已被整段注释，没有任何 API 或配置能改。
2. **现有解析器与 `asst.log` 的格式不匹配。**（2026-11-23 续查二更正，见 §9.1）产品读的是任务配置的 `task.LogPath`（`RunnerEngine.cs:225/239`），界面提示「MAA日志请选择 gui.log」（`MainWindow.xaml:38`）；它匹配的是 GUI 本地化中文（`开始任务:`、`任务出错:`、`任务已全部完成！`），而 `asst.log` 里是英文 `Assistant::append_callback | <Event> {json}`。所以准确说法是：**若任务把日志路径指向 `asst.log`，当前解析器不支持其结构化回调**；不是“产品固定读错文件”。格式匹配本身也不构成来源认证。

替代路径（方案 G）在“写入者身份”这一环的证据被本轮证伪（见 §9.2、§9.3）：Restart Manager 只列“采样时刻正在使用已登记资源的应用”，不区分读写、不覆盖采样间隙；只读打开者同样被列出，写者可在两次采样之间完成打开/追加/关闭，写者退出后外部追加还能让候选规则判“成功”。它最多能支撑“采样时刻未观察到根进程以外的持有者”，**不能证明每个字节来自本轮根进程**，也不能证明链内每一步业务成功。

### 1. 方案 E 的四问核对（源码事实，tag `v6.22.0`）

1. **启动时工作目录怎么变**：`OnStart()` 先 `string launchDir = Environment.CurrentDirectory;`，紧接着 `Directory.SetCurrentDirectory(AppContext.BaseDirectory);`，然后建 `debug` 目录、读命令行、初始化日志、加载配置；`launchDir` 只用于解释“启动时工作目录”的相对路径参数（`ParseEarlyLaunchArgs(args, launchDir)`）。→ V3 设的启动目录对日志位置没有任何作用。
2. **日志目录如何确定**：`Logger() : m_directory(UserDir.get())`；`m_log_path = m_directory / "debug" / "asst.log"`；`m_log_bak_path = … "asst.bak.log"`；`MaxLogSize = 64 MiB`。`UserDir` 无默认值，只能由宿主调 `AsstLoadResource(path)`（为空时设为该绝对路径）或 `AsstSetUserDir(path)` 设置。`set_directory` 在 `Logger.hpp` 599–607 行整段被注释。→ **6.22.0 没有改日志目录的入口**；GUI 侧 `CoreLogFile = PathsHelper.DebugDir / "asst.log"` 只是同一个位置的显示常量。
3. **改工作目录的连带影响**：资源（`AsstLoadResource` 收绝对路径）、配置（`PathsHelper` 基于 exe 目录）、更新安装（`VerifyInstallLocation` / `TryApplyPendingUpdatePackage`）、单实例（Mutex + `MAA_KEEPAWAKEDATA_` 事件）都不依赖 CWD。也就是说改 CWD 换不来任何隔离收益，只会改变命令行里相对路径的解释。MAA 内是否有别处依赖 CWD 未逐行确认，[待核实]。
4. **真实运行验证**：未执行。真实 MAA 是否与源码一致、GUI 是否与 MaaCore 同进程、日志是否按该格式落盘，全部 [待核实]；需要验收方另行授权只读采集。

### 2. 更基础的错误：解析器匹配的中文不在 `asst.log` 里（源码事实）

- MaaCore 侧：`Assistant::append_callback` 用 `Log.info("Assistant::append_callback |", msg, more_detail.to_string())` 写 `asst.log`，内容是英文事件名 + JSON（`taskchain` / `taskid` / `uuid` / `finished_tasks` / `details.error`），没有中文业务文案。
- GUI 侧（`AsstProxy.cs`）：`TaskChainStart` → `AddLogSection(GetString("Assst.TaskQueue.StartTask") + taskName)` 并 `_logger.Information("Start Task Chain: …")`；`TaskChainError` → `GetString("Assst.TaskQueue.TaskError") + chain` 后 `DisplayLog(..., UiLogColor.Error)`；`TaskChainCompleted` → `GetString("Assst.TaskQueue.CompleteTask")`；`AllTasksCompleted` → `GetStringFormat("Assst.TaskQueue.AllTasksComplete", 用时)`，有错时换 `CompletedWithErrors`。这些进界面日志和 Serilog（`gui.log`），旧的 `AsstLog` P/Invoke 整段被注释，注释原文：「现在拆分成了 core 和 UI 的日志，所以这个函数暂时弃用」。
- 推论：v1.20.2 的 `MaaLogEvidence` 在真实 `asst.log` 上很可能一条都匹配不到，因此“解析成功→放行”这条路在现状下本来就走不通；先把 `src/` 改回“更宽松的中文匹配”只会更危险。

### 3. 业务结果事件模型（源码事实 + 缺口）

| 事件 | 触发点 | 关键字段 | 顺序/次数 | 能否当“业务成功” |
| --- | --- | --- | --- | --- |
| `TaskChainStart` | 每个任务 `run()` 前 | `taskchain`、`taskid`、`uuid` | 每任务一次 | 否，只表示开始 |
| `TaskChainCompleted` | `run()` 返回 true 且未置 idle | 同上 | 失败任务不会发 | 单任务级“无异常结束” |
| `TaskChainError` | `run()` 抛异常或返回 false | 额外 `details.error`：`OpenCVException`/`OutOfMemory`/`GpuDeviceRemoved`/`UnhandledException`/`UnknownException` | 非致命异常**继续跑后续任务** | 否，且同一 taskid 不再发 Completed |
| `TaskChainStopped` | 用户 stop 后任务返回；`task_delay` 等待期间被 stop；顶层 `catch(...)` | 前两种带 `taskchain`/`taskid`；顶层 catch 的 `details` 为空 | 停止时**不发** `AllTasksCompleted` | 否 |
| `AllTasksCompleted` | 队列清空 | `finished_tasks`（**含失败任务 id**）、`taskchain`、`taskid` | 一次；之后仍可能来 `TaskChainStopped` | 否，只表示队列跑完 |

- 完成回调**之后**仍可能出现停止回调（`task_delay` 期间 stop）；错误之后仍可能继续并最终收到 `AllTasksCompleted`。这两条由合成夹具独立验证，不依赖真实日志。
- 进程崩溃/被杀：`working_proc` 顶层 `catch` 只覆盖该函数内异常，进程级崩溃没有任何收尾事件；V3 只能看到日志停在半途。
- 日志滚动：`rotate()` 在超过 64 MiB 时 `rename(asst.log, asst.bak.log)` 后重开；`try/catch(...) {}` 吞掉失败。写入失败时 `m_ofs` 无效，写会被丢弃且不报错（细节 [待核实]）。
- `uuid` 是 MAA 实例级，`taskid` 是每实例自增，PID 可被任意进程在日志行里伪造（已实测）。三者都不能单独当“本轮会话”认证。
- **语义上限**：`TaskChainCompleted` 只代表任务链 `run()` 返回 true，不代表链内每个子步骤业务成功（战斗失败重试、理智不足提前收工等仍可能返回 true）。要判断“每一步都真的赢了”没有官方回调支持，[待核实]。

### 4. 本轮新增隔离夹具与实测（受控测试，未碰真实 MAA）

| 探针 | 断言 | 关键证据 |
| --- | --- | --- |
| `probe-maa-log-exclusive` | 3 PASS | `held=True appendDenied=True readAllowed=True truncateDenied=True moveDenied=True deleteDenied=True`；持有者退出后 `fileWritableAfterExit=True anchorUnchangedAfterAppend=True forgedStarted=True forgedSucceeded=True`；抢先占位时 `targetSideOpenFailed=True` |
| `probe-maa-writer-identity` | 3 PASS | Restart Manager `holderFound=True holderOnlyCount=1`，PID 与创建时间匹配；只读打开者也会被列出（`count=2`）；关闭后 `count=0` |
| `probe-maa-event-model` | 5 PASS | 官方回调行对中文解析器 `started=False succeeded=False`；`TaskChainError` 单独出现被判 `succeeded=True`；`TaskChainStopped {}` 不被识别；`finished_tasks` 从不检查 |
| `probe-maa-session-rule` | 9 PASS | 通过/暂停 9 个判定分支，见下 |

`probe-maa-session-rule` 的 9 个分支（测试内纯函数，不进产品）：正常成功；错误后完成→暂停；完成后停止→暂停；**退出后追加的伪造行被忽略**；持有者非根→暂停；文件身份变化→暂停；有任务未完成→暂停；`finished_tasks` 为空→暂停；退出码非零→暂停。

实测补充两条事实：`File.ReadAllText` 默认 `FileShare.Read`，读 MAA 式 deny-write 日志会抛 `IOException`（共享冲突），必须 `FileShare.ReadWrite|Delete`；目标退出后文件立刻可写，追加内容对 `MaaLogAnchor` 不可见（锚点只校验旧区域），会被当作目标证据读入。因此“退出后继续读日志”必须禁止。

### 5. 候选方案比较（本轮更新）

| 方案 | 绑定本轮实例 | 业务成功 | 反例覆盖 | 依赖/成本 | 红线 |
| --- | --- | --- | --- | --- | --- |
| A 按 `Px` 匹配 PID | 否（实测可伪造） | 否 | 通过（证伪） | 无 | 不触碰 |
| B 受管 stdout | 仅对写 stdout 的程序 | 部分 | 通过 | 无 | MAA 写文件，无效 |
| C 目标主动连命名管道回执 | 是（PID+创建时间） | 取决于回执内容 | 通过 | 需改 MAA 或受信中间层 | 不碰第三方配置 |
| D Job + 句柄 | 只能证明同 Job | 否 | 部分 | 现有能力 | 不触碰 |
| E 工作目录 + `asst.log` | **否（前提否定）** | 中 | 需新增 | 只改 V3，但换不来隔离 | 不成方案 |
| F ETW/Sysmon/4688 | 补进程创建事实 | 否 | 未测 | 需管理员/系统配置 | 红线，不做 |
| **G 独占句柄 + Restart Manager + 结构化回调** | 仅“采样时未见他者”（§9 证伪更强表述） | 中（任务链无异常跑完） | 见 §4 九分支、§9.3 矩阵 | 只改 V3 解析与判定 | 不碰 MAA |
| H 官方 C API 自建 Assistant（`AsstCreateEx` 回调） | 是（自己创建的实例） | 强（每 taskid 回调） | 需自建夹具 | 需资源、账号、模拟器、真实运行 | 越“不改 MAA 配置、不启动真实 MAA”，须另行授权 |

方案 G 的信任边界：deny-write 保证运行期间只有一个写者；RM 给出持有者 PID；根进程身份由 V3 自己的 `OwnedProcess`（句柄 + 创建时间）给出。缺口：MAA 关闭句柄到进程退出之间的窗口、日志滚动、RM 采样间隔；这些都必须判暂停而不是猜。方案 G **不能**抵御同机注入或篡改根进程本身，也不能证明子步骤业务成功。

### 6. 推荐的最快安全路径

1. **现在**：保留 v1.20.2 暂停；把“MAA 日志位置固定、现有中文解析对 `asst.log` 无效、不要在外部更新器运行时启用 MAA 自动接续”写进人工验收。
2. **产品改动（需用户认可，未实施）**：方案 G，五步——只读打开并记录文件身份；RM 校验持有者含根进程且创建时间一致，并确认自己写打开被拒（deny-write 生效）；只解析结构化回调；按 §4 九条规则判定；根进程退出后不再采信任何新字节，任一异常即暂停。
   - 2026-11-23 续查二修正：第 2 步只能作为“采样时未见他者”的辅助证据，不能升级为写入者认证；按 §9.4 的结论，G 单独不足以自动判成功，若要放行必须追加目标侧回执（方案 C）或自建宿主（方案 H）。
3. **验收条件**：新夹具覆盖 §4 全部反例并通过；验收方在授权隔离环境只读采集一次真实 `asst.log`，确认格式、滚动与写失败行为，再决定是否放行。
4. **若要抗同机恶意进程或证明每一步业务成功**：只能走 C、H 或系统级追踪，均需另外授权，本轮不做。

### 7. 本轮证据缺口

- 真实 `asst.log` 的实际内容、是否偶含中文、滚动/写失败行为：未采集，[待核实]。
- GUI 与 MaaCore 是否同进程（`AsstProxy` 直接 P/Invoke `MaaCore.dll` 指向同进程，但未在真实进程上核实）、Serilog 是否就写 `debug/gui.log`：均 [待核实]。
- Restart Manager 在真实 MAA 上是否稳定列出持有者、是否有性能或权限问题：未测，[待核实]。
- 6.23.1 是否同构（版本门限未动）：[待核实]。
- 更新/重启、多日无人值守、休眠恢复：未执行。

### 8. 本轮实跑记录（2026-11-23 20:03–20:20）

| 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- |
| `dotnet build tests\Runner.Checks\Runner.Checks.csproj -c Debug` | 0 | 0 警告 0 错误 | 控制台 |
| `Runner.Checks.exe probe-maa-log-exclusive` | 0 | 3 PASS / 0 FAIL | 控制台 |
| `Runner.Checks.exe probe-maa-writer-identity` | 0 | 3 PASS / 0 FAIL | 控制台 |
| `Runner.Checks.exe probe-maa-event-model` | 0 | 5 PASS / 0 FAIL | 控制台 |
| `Runner.Checks.exe probe-maa-session-rule` | 0 | 9 PASS / 0 FAIL | 控制台 |
| `Runner.Checks.exe probe-maa-instance-evidence` | 0 | 15 PASS / 0 FAIL | `.build/probe-maa-instance-evidence-20261123-2055.log` |
| `Runner.Checks.exe probe-maa-resume` | 0 | 127 PASS / 0 FAIL | `.build/probe-maa-resume-20261123-2055.log` |
| `Runner.Checks.exe probe-maa-external-writer` | 0 | 13 PASS / 0 FAIL | `.build/probe-maa-external-writer-20261123-2055.log` |
| `Runner.Checks.exe probe-short-lived-processes` | 0 | 8 PASS / 0 FAIL | `.build/probe-short-lived-processes-20261123-2055.log` |
| `Runner.Checks.exe probe-job-events` | 0 | 8 PASS / 0 FAIL | `.build/probe-job-events-20261123-2055.log` |
| `Runner.Checks.exe probe-maa-channel-boundary` | 0 | 5 PASS / 0 FAIL | `.build/probe-maa-channel-boundary-20261123-2055.log` |
| `Runner.Checks.exe probe-maa-start`（受控窗口专项单独跑） | 0 | 17 PASS / 0 FAIL | `.build/maa-start-standalone-20261123-2050.log` |
| 无参数完整回归 | **1** | **486 PASS / 1 FAIL / 0 SKIP**，失败项 `MAA controlled start refuses another process window` | `.build/full-regression-20261123-2032.log` |
| `Runner.Checks.exe probe-regression-local`（会跳过部分案例） | 0 | 472 PASS / 0 FAIL / 1 SKIP（`SKIP controlled resume: run probe-controlled-resume separately with local pipe access`） | `.build/probe-regression-local-20261123-2100.log` |

- 完整回归本轮**未通过**：唯一失败项与上一轮同一处（受控窗口在整包连续运行时被本机桌面焦点影响），单独运行该专项 17 PASS；不能宣称完整回归通过，也不能据此改产品迁就测试。
- `probe-regression-local` 与无参数完整回归不是同一入口：前者会跳过需要本地管道/Dispatcher 的案例（本轮 1 SKIP），数字不能互相替代。

## 2026-11-23 续查二：纠正“读错文件”与“RM 能证明写入者”，补 Holder 矩阵反例（研究轮，`src/` 零改动）

本节纠正上一节 §0 的两处过强表述，并补上方案 G 的反例矩阵。产品判定未改，v1.20.2 的“来源无法确认即暂停”继续生效。结论按来源分层标注：源码事实 / 官方文档 / 受控测试 / 真实运行 `[待核实]`。

### 9.1 纠正一：产品读的是任务配置的 `LogPath`，不是固定的 `asst.log`（源码事实）

- `src/Runner/RunnerEngine.cs:208`：`bool maaMode = task.Completion == "MAA日志";`；`:225`：`if (maaMode && (string.IsNullOrWhiteSpace(task.LogPath) || !File.Exists(task.LogPath))) return "结果不明：MAA 日志文件不存在，请先检查路径";`；`:239`：`file = new FileStream(task.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);`。→ 日志路径完全来自任务配置，产品没有硬编码 `asst.log`。
- `src/Runner/MainWindow.xaml:38` 提示原文：「日志文件路径（MAA日志请选择 gui.log）」；`:30`：「仅支持 MAA 6.21.4 / 6.22.0 中文日志格式。日志来源无法确认，当前不会自动判成功，请人工核实。」→ 设计意图是读 GUI 的中文日志。
- `src/Runner/MaaLogEvidence.cs` 的匹配词是 GUI 本地化文案；`asst.log` 只有英文回调（`Assistant::append_callback | <Event> {json}`，源码见 §2、§3）。
- 因此准确表述是：**中文 GUI 日志解析与 `asst.log` 结构化英文回调是两种格式；任务若指向 `asst.log`，当前解析器不支持**（`probe-maa-event-model`：`callback-lines started=False succeeded=False failed=False stopped=False`）。上一节 §0 第 2 条的“现有解析器读 `asst.log`，所以读错文件”作废。
- 不受影响的部分：无论读哪个文件，“文字匹配”都只是内容识别，**不是来源认证**；v1.20.2 的暂停理由仍然成立。

### 9.2 纠正二：Restart Manager 只给“采样时刻的持有者集合”（官方文档 + 受控测试）

官方文档语义（`RmGetList`，learn.microsoft.com，页面更新 2024-11-18）："Gets a list of all applications and services that are **currently using** resources that have been registered with the Restart Manager session."；失败码只有 `ERROR_MORE_DATA`/`ERROR_CANCELLED`/`ERROR_SEM_TIMEOUT`/`ERROR_BAD_ARGUMENTS`/`ERROR_WRITE_FAULT`/`ERROR_OUTOFMEMORY`/`ERROR_INVALID_HANDLE`/`ERROR_ACCESS_DENIED`（路径是目录）。`RM_PROCESS_INFO` 字段：`Process`（PID + 进程启动时间 FILETIME）、`strAppName`、`strServiceShortName`、`ApplicationType`、`AppStatus`、`TSSessionId`、`bRestartable`——**没有读/写区分，没有逐次写入或时间区间信息**。

受控测试（`probe-maa-holder-matrix`，13 PASS，矩阵见 §9.3）实测到的边界：

- 只读打开者与写者被同样列出（S1、S2），RM 无法区分谁在写；
- 写者可在两次采样之间完成“打开→追加→关闭”，两次采样都只看到根进程（S3）——采样间隙不可消除；
- 写者退出后，外部进程可继续追加完成文字；此时 RM 只看到只读的根进程，候选规则仍判“成功”（S4）；
- 根进程退出后追加的文字不再关联任何持有者，但内容照样被解析成完成（S6）；
- 不存在的路径 RM 返回成功且空列表（S7），“RM 没报错”不能推出“没人写”。

结论：RM 最多支撑“**采样时刻**未观察到根进程以外的持有者”。上一节 §0 第 3 段的“能在根进程存活期间证明这些字节来自本轮根进程”作废，改为：**采样间隙内的写入者无法排除；只有目标侧主动回执才能把写入者绑定到本轮实例**。

### 9.3 Holder 矩阵实测（受控测试：临时目录、夹具进程、命名事件同步，未碰真实 MAA）

夹具 `fixture-holder <path> <read|write|rw> <gatePrefix> <payload>`：`read` = `FileShare.ReadWrite|Delete` 只读；`write` = `FileMode.Append + FileShare.Read`（复刻 MAA Logger 的 deny-write）；打开后打印 `OPEN`，等父进程 `-step` 事件后追加 payload、打印 `STEPPED`，等 `-done` 后关闭并打印 `CLOSED`。父进程每个观察点都由事件/输出行确定，不靠随机等待。

| 场景 | 谁持有什么 | 观察 | 候选规则 |
| --- | --- | --- | --- |
| S1 只读根 + 外部写者 | 根：只读；外部：deny-write | `rmRootOnly=[根]`，写者打开后 `rmBoth=[根,写者]`；写者关闭后 `rmNow=[根]` 而文件已追加 | 采样时“根在列表里”完全不代表根在写 |
| S2 写持有者 + 只读读者 | 根：deny-write；测试进程：只读 | `rm=[读者,根]`，两者都在 | 成员资格无法区分读写 |
| S3 采样间隙 | 根：只读；写者短暂开关 | 前后两次采样集合相同（仅根），期间写者完成追加 | 规则若只看采样会漏掉整个写窗口 |
| S4 写者退出后外部追加 | 根：只读；MAA 式写者已退出；外部再写 | RM 仅根；文本 `开始任务: A` + `任务已全部完成！`；`started=True succeeded=True`；规则判“**成功**” | **反例**：G 的候选规则可被完全绕过 |
| S5 滚动 / 截断 / 打开失败 | 根：只读句柄 | 滚动后 `LogFileIdentity.IsSame=False`；截断后 `length=0 < position=8`；只读属性下写打开 `UnauthorizedAccessException` | 前两者可检测；打开失败只剩沉默，只能暂停 |
| S6 根退出后追加 | 根已退出 | `rmAfterExit=[]`，文本仍解析成完成，规则判“暂停（未见根持有）” | 这一条 G 能挡住；但它挡不住 S4 |
| S7 RM 失败 / 死 PID / 时间漂移 | — | 不存在的路径 `ok=True[]`；已退出 PID 查询抛 `ArgumentException`；启动时间加 5 秒即判定不一致 | RM 不提供失败信号；身份只剩 PID+启动时间这一道校验 |
| S8 错误后完成 | 文本路径 | `failed=True succeeded=True`；规则因错误标志判暂停 | 必须把错误标志当作否决项，不能只看完成文字 |

补充：S4 的规则函数是本轮测试内的候选原型（`JudgeHolder`），不是产品代码；它刻意实现“采样仅见根 + 身份稳定 + 事件完整 + 退出正常 → 成功”，用来证明该规则在真实反例下会误放行。

### 9.4 候选方案比较（本轮更新；每格都是“能否证明”，不是“技术名称”）

| 方案 | 本轮实例认证 | 业务成功 | 更新/重启 | 短命进程身份 | 依赖与成本 | 红线/边界 |
| --- | --- | --- | --- | --- | --- | --- |
| A 日志行内 `Px` 匹配 | 否（可任意伪造，已实测） | 否 | 无 | 无 | 无 | 不触碰 |
| B 受管 stdout | 仅对写 stdout 的程序 | 部分 | 弱（重启后管道断） | 无 | 无 | MAA 写文件，无效 |
| C 目标主动回执（命名管道等） | **是**：客户端 PID + 创建时间可绑定本轮根实例（夹具已验证外部连接被拒） | 取决于回执内容：必须带 taskid、错误/停止标志；回执完整才可判“链完成”，仍不等于每步业务成功 | 需目标在重启后重连并携带同一会话 nonce | 无（目标须存活） | 需改 MAA 或受信中间层/插件 | 不碰第三方配置；改 MAA 需单独授权 |
| D Job + 句柄 | 只能证明“同 Job” | 否 | 弱 | 部分（句柄 + 创建时间） | 现有能力 | 不触碰 |
| G 只读 + 文件身份 + RM + 结构化回调 | **否**（§9.3 S4/S3 证伪“写入者认证”） | 中（任务链无异常跑完） | 可检测滚动/替换，不可证更新后身份 | 不覆盖 | 只改 V3 | 可作为“辅助证据 + 暂停理由”，不能单独放行 |
| H 官方 C API 自建宿主（`AsstCreateEx`） | **是**（自己创建的 Assistant 实例，回调在自进程内） | 强于日志（每 taskid 的 Completed/Error/Stopped），仍不等于每步业务成功 | 需自己处理实例重建 | 强（实例句柄） | 需资源目录、账号/连接配置、真实运行 | 改变用户操作与授权边界，须另行授权 |

- C 的信任边界：回执必须由 V3 启动的那个根进程（或它授权的子进程）发出，V3 用命名管道客户端 PID + 创建时间校验；内容需含 taskid 集合、每个 task 的结束状态、停止/错误标志与会话 nonce。**仅“连上了管道”不等于业务成功**，`probe-maa-holder-matrix` S9 已把“根绑定但缺 taskid / 带停止”列为暂停。
- H 的信任边界：V3（或其 sidecar）通过官方 C API 自己 `AsstCreateEx` 并 `AsstAppendTask`，回调直接在进程内；不再依赖日志。代价是要自己管理资源、账号/模拟器连接与更新，等于把“用户现有 GUI 配置”换成一套并行配置——这会改变用户体验，且本轮红线禁止真实运行。
- G 的正确定位：在不改 MAA、不引入中间层的前提下，它能做到的极限是“结构化解析 + 采样时未见他人 + 文件身份稳定 → 仍暂停，但暂停理由更准确、证据更完整”。把它当自动放行依据不成立。

### 9.5 推荐的最快安全路径

1. **保持 v1.20.2 暂停**（现状），不恢复任何基于日志文字的自动关闭/自动成功。
2. **可在 V3 内单独实施的安全改进（不自动放行，需用户认可）**：把 `MAA日志` 模式的中文匹配换成/并列 `asst.log` 结构化回调解析——识别 `TaskChainStart/TaskChainCompleted/TaskChainError/TaskChainStopped/AllTasksCompleted`、区分错误与停止、展示 taskid 与 `finished_tasks` 差异；判定结果**仍然只输出“结果不明，请人工核实”**，但给出可核对的证据清单（哪些 taskid 完成、是否出现错误/停止、是否滚动）。收益是人工核验更快、误判更少；成本是解析 JSON 行 + 处理滚动/截断，不改变信任边界。
3. **要真正自动接续**，只有两条路，均需另行授权：
   - **C**：MAA 侧（或受信中间层）提供回执通道，回执绑定本轮实例并携带 taskid/错误/停止；V3 只在回执完整且无错误时放行“任务链完成”。这需要改 MAA 或加插件/中间层，属第三方改动。
   - **H**：V3 用官方 C API 自建宿主，自己持有实例与回调；需要资源、账号/模拟器配置与真实运行授权，且会改变用户现有 GUI 操作方式。
4. **不建议**把“RM 采样 + 日志文字”包装成自动成功：§9.3 S4 已给出可复现的误放行反例，属于放宽安全暂停。

### 9.6 本轮证据缺口（未执行即标 `[待核实]`）

- 真实 MAA 的 `asst.log` 实际内容、是否偶含中文、滚动/写失败的真实行为：未采集。
- GUI 与 MaaCore 是否同进程、Serilog 是否写 `debug/gui.log`：未在真实进程上核实。
- RM 在真实 MAA 上是否稳定列出持有者、采样开销与权限：未测。
- 6.23.1 是否与 6.22.0 同构（版本门限未动）：未核实。
- 更新/重启、多日无人值守、休眠恢复、真实游戏与真实账号：全部未执行。
- S3/S4 的“采样间隙”结论是逻辑与夹具事实；真实 MAA 场景下攻击者是否具备同等写入权限，取决于同机权限模型，本轮未做权限对抗。

### 9.7 本轮实跑（2026-11-23，Windows 本机；`src/` 零改动）

| 项目 | 命令 | 结果 | 日志/证据 |
| --- | --- | --- | --- |
| 构建 | `dotnet build tests/Runner.Checks/Runner.Checks.csproj -c Debug` | 0 警告 0 错误，退出码 0 | 控制台 |
| Holder 矩阵 | `Runner.Checks.exe probe-maa-holder-matrix` | 13 PASS / 0 FAIL，退出码 0 | `.build\checks-*\evidence-maa-holder-matrix.txt` |
| 受影响专项（14 条） | 见下 | 全部退出码 0、0 FAIL、0 SKIP | `.build\probes-20261123-2035.log` |
| 无参数完整回归 | `Runner.Checks.exe`（无参数） | **500 PASS / 0 FAIL / 0 SKIP，退出码 0** | `.build\full-regression-20261123-2045.log` |
| `probe-regression-local`（会跳过案例） | `Runner.Checks.exe probe-regression-local` | 485 PASS / 0 FAIL / 1 SKIP，退出码 0（SKIP：`controlled resume: run probe-controlled-resume separately with local pipe access`） | `.build\regression-local-20261123-2056.log` |

受影响专项逐项（均 `Failures: 0`）：`probe-maa-session-rule` 9、`probe-maa-event-model` 5、`probe-maa-log-exclusive` 3、`probe-maa-writer-identity` 3、`probe-maa-instance-evidence` 15、`probe-maa-external-writer` 13、`probe-maa-evidence-counterexamples` 4、`probe-maa-channel-boundary` 5、`probe-job-events` 8、`probe-short-lived-processes` 8、`probe-maa-start` 17、`probe-maa-resume` 140、`probe-restart-candidates` 20、`probe-controlled-success` 1。

说明：本轮首次运行探针时 S1 曾因并发读同一 `StreamReader` 崩溃（`InvalidOperationException: The stream is currently in use by a previous operation on the stream.`），改为单线程 `LinePump` 后通过；另修正两处夹具缺陷（截断打在滚动后旧句柄、S6 漏发 step 信号）后才得到上表结果。失败与修正过程保留在此，未删除断言迁就结果。
