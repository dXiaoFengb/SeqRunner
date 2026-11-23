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
- 本轮只读的隔离副本：`参考/MAA-isolated-v170-a603afd/debug/asst.log`，42,047,747 字节，最后写入 2026-11-06 19:08:32；`asst.bak.log` 67,109,014 字节。该文件是参考目录里的隔离副本，不是用户实时日志，不能当作真实验收。
- 同一文件内计数：`append_callback` 7,470；`AllTasksCompleted` 19；`TaskChainStart` 51；`TaskChainCompleted` 52；`TaskChainStopped` 5；`SubTaskError` 54；涉及 18 个不同 PID；唯一 `uuid` 只有 `bd203674fb05e1de`，跨日期和 PID 不变。中文“开始任务/任务已全部完成/任务出错/已停止”计数均为 0。
- 典型完成行：
  `[2026-09-28 12:30:46.726][INF][Px21788][Tx14905] Assistant::append_callback | AllTasksCompleted {"finished_tasks":[1,2,...,19],"taskchain":"Award","taskid":19,"uuid":"bd203674fb05e1de"}`
- 错误后仍完成：同一轮 Mall 的 taskid 16 连续出现 7 次 `SubTaskError`（本地副本行 10242–10519），随后行 10566 `TaskChainCompleted`、行 26664 `AllTasksCompleted`，其 `finished_tasks` 仍包含 16。说明“出现 `AllTasksCompleted`”不能推出“所有任务成功”。
- 完成可后接停止：行 198498 `TaskChainCompleted`、198499 `AllTasksCompleted`（Px13872/Tx23371/taskid 1），84ms 后行 198502 `TaskChainStopped`（同 PID/Tx/taskid）。只看到完成回调就判定成功会漏掉迟到的停止。
- 实例关联：`Px` 是写这一行的进程 PID；同一 `asst.log` 头部是 Px21788、尾部是 Px37244，说明文件跨多次进程运行追加。`taskid` 在会话内递增、重启后可能复位；本样本 `uuid` 固定，不能充当每轮随机 nonce。三者都无法单独证明“这一行属于 V3 本轮的根进程实例”。`taskid`/`uuid` 的源码语义仍 [待核实]。
- 当前 `MaaLogEvidence` 只匹配中文 GUI 文本（`开始任务:`、`任务已全部完成！`、`任务出错:`、`已停止`），不解析 `Assistant::append_callback`。本轮合成行断言：英文回调对当前解析器四态均为 false；带 `[Px100]/[Px999]` 前缀的中文“开始任务/任务已全部完成”仍被判 `Started+Succeeded`，证明当前解析器完全不看 PID 来源。
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
