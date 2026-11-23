# 当前任务

## 最小任务卡

- 任务名称 / 标识：MAA 自动接续证据与安全判定 / v3-maa-continuation-evidence。
- 任务类型：普通开发。
- 目标：研究并解决 MAA 受管子进程身份不明导致无法接续；证据不足时保留暂停。
- 背景与输入：`对话记录/计划书/v3-maa-continuation-evidence-实施计划.md`；本轮用户认可 `对话记录/计划书/v3-maa-log-source-实施计划.md`；本轮专项计划 `对话记录/计划书/v3-maa-continuation-evidence-实例证据研究计划.md`（2026-11-23 用户认可隔离研究范围）；2026-11-23 研究轮计划 `对话记录/计划书/v3-maa-log-exclusive-实施计划.md`；2026-11-23 续查二计划 `对话记录/计划书/v3-maa-holder-matrix-实施计划.md`。
- 允许修改：计划内的隔离探针、判定及记录、受控测试、版本、验收与项目记录；是否修改产品以证据充分为前提。
- 禁止修改：不接管或强制结束身份不明的组外进程，不修改 MAA 配置，不自行运行真实 MAA、游戏，不打包、发布或替换正式产品；保留未知来源的 `验收工具/`。
- 需再次确认：具体真实 MAA 或游戏验收由验收方执行前须取得授权；其他受限操作按项目规则处理。
- 完成标准：按计划给出可验证的安全接续实现与证据；若证据不足，保留暂停并报告未解决的阻塞。
- 验证方式：隔离受控探针和适用专项、完整回归、源码预览；真实业务另行授权。
- 风险与回退：不以计数、文件指纹或未知进程用途推定成功；身份或业务事实不明时暂停。
- 当前状态：执行中；v1.20.2 已收紧日志来源不明时的关闭和成功判定；2026-11-23 已有三轮隔离研究（方案 E 前提核对、事件模型与四条探针、持有者矩阵与两处结论纠正），方案 E 前提被源码否定，方案 G 的“RM 认证写入者”被 S4 反例证伪，未恢复自动接续，`src/` 零改动，等待用户决定是否走方案 C（目标回执）或 H（官方 C API 自建宿主）。真实 MAA 和人工验收仍待核实。

## 已确认决定与待决问题

- 用户确认先解决 MAA 自动接续阻塞，再推进其他内容；开发版本按项目规则正常推进。
- 用户明确认可本轮计划并要求实施；真实 MAA、游戏和真实更新需按具体内容另行授权。
- 用户认可细化计划并要求仅修改对应功能、不触碰红线；不新增自动重试或重放。
- 真实 MAA 更新后接续、多日无人值守及休眠恢复 [待核实]。
- `MAA自动接续证据核查.md` 记录本轮探针及限制；需研究能可靠关联本轮业务、更新和进程身份的证据来源后再判断是否可安全实施。
- 用户报告 `人工验收流程.md` 中证据反例复核三条通过，含源码预览与页面切换；该结果不代表真实业务通过。
- 用户报告证据反例、Job/MAA 回归和源码预览三条全部通过；真实 MAA、游戏与自动接续未测试。
- 用户确认本轮计划并选择开发版本 v1.20.1，新增线索只阻止自动接续，不能推定无更新或证明业务完成。
- MAA 6.22.0 核心源码的 `asst.log` 包含带进程 ID 的结构化回调，是后续候选线索；日志完整性与本轮实例关联 [待核实]。
- 完整回归首次 428 PASS、1 FAIL，视觉画面变化夹具失败；用户反馈当时不慎点击受控窗口，原提交基线和当前版本的同专项各一次复查通过；完整回归复跑 429 PASS、0 FAIL，首次结果仍保留。
- 2026-11-23 受控证据：`probe-maa-external-writer` 中组外夹具追加“开始任务/任务已全部完成”后，V3 输出 `maa 成功` 并启动后项，Job 累计进程数与加入/退出通知均为 1，既有两道路阻断都未触发。原“外部写入一定暂停”由 [待核实] 改为“会误判”，产品未改，详见 `MAA自动接续证据核查.md`。
- 2026-11-23 受控证据：`probe-short-lived-processes` 在 20ms 采样下 7 个进程的句柄、创建时间与组归属基本可取得；按产品 200ms 节奏时仅 1 个可核实、6 个 `process-open-failed`。事件数量吻合不等于身份核实，也不等于用途已知。
- 用户报告：v1.20.1 受控专项、完整回归和源码预览通过；真实 6.23.1 在“MAA日志”模式下被版本门限在启动前阻止；测试用 6.21.4 副本已由 MAA 自身更新到 6.23.1；真实 6.22.0 更新时 V3 是否实际识别新增更新线索仍 [待核实]。
- 用户确认 v1.20.2 收紧方案：日志来源无法核实时不凭 MAA 日志自动关闭或判成功；继续研究可核对的实例及业务证据。
- 本轮构建 0 警告 0 错误；MAA 87 PASS、Job 8 PASS、更新关键词 14 PASS，完整回归 447 PASS / 0 FAIL；独立源码预览起窗并烟测退出 0，人工操作未验收。
- 2026-11-23 验证：`dotnet build tests\Runner.Checks\Runner.Checks.csproj -c Debug` 0 警告 0 错误；`probe-maa-external-writer`、`probe-short-lived-processes`、`probe-maa-evidence-counterexamples`、`probe-job-events`、`probe-maa-resume` 均退出码 0、0 失败；完整回归 `probe-regression-local` 第三次 431 PASS / 0 FAIL（退出码 0）。前两次失败（视觉夹具 2 项、读日志文件被占用 1 项）均为本机桌面焦点与既有竞态所致，与本轮新增代码无关，重跑通过。
- 2026-11-23 源码预览：`验证审查器.cmd` 实际启动，窗口标题“顺序化软件执行工具 V3”出现后由我关闭；页面操作、隔离数据检查与人工验收未执行，不能记为功能通过。
- 2026-11-23 实例证据研究：`probe-maa-instance-evidence` 15 PASS（6 条回调断言 + 9 案矩阵），9 案均 `paused=True/advanced=False/closeCalls=0`；`probe-maa-resume` 102 PASS、`probe-maa-external-writer` 13 PASS、`probe-short-lived-processes` 8 PASS、`probe-maa-evidence-counterexamples` 4 PASS、`probe-maa622-clues` 13 PASS、`probe-job-events` 8 PASS，均退出码 0、`Failures: 0`。完整回归三次均未全过：第一次在本机既有 race 文件移动处异常，第二、三次均为 446 PASS / 1 FAIL / 1 SKIP，失败项为受控窗口识别；窗口项单独运行 17 PASS，不能宣称完整回归通过。
- 2026-11-23 结论：`asst.log` 的 `Px` 只是写日志进程的 PID，不能认证本轮实例；日志格式允许“错误后仍出现完成、完成后仍出现停止、`finished_tasks` 含失败任务”，这三点改由合成夹具独立验证；涉及只读样本的统计值已从本卡移除并标 [待核实]，只保留路径引用。当前解析器不识别英文回调且忽略 PID 前缀。短命进程按产品 200ms 节奏多数无法在查询前核实身份。证据不足以恢复自动接续，未改产品判定。
- 2026-11-23 参考样本边界与通道证据：测试与可编辑报告中的只读样本原值已替换或移除；历史记录 `对话记录/04-问题、风险与恢复.md:1227`、Git 历史 `8800fbb` 及更早、旧备份仍含相关内容，未改写，待用户决定。新增 `probe-maa-channel-boundary` 实测：按 PID 绑定证据行可被组外进程伪造、受管 stdout 对写文件的 MAA 无证据、命名管道回执可绑定根 PID 与创建时间（需目标程序配合）。构建 0 警告 0 错误，通道专项 5 PASS，完整回归 467 PASS / 0 FAIL。方案 E（独占工作目录 + 结构化回调解析 + `finished_tasks`/错误交叉 + Job 核对）已写入 `MAA自动接续证据核查.md`，未实施，等用户认可；产品零改动，自动接续仍暂停。
- 2026-11-23 续查二（纠正与反例）：①产品日志路径来自 `task.LogPath`（`src/Runner/RunnerEngine.cs:225`、`:239`），界面提示选 `gui.log`（`src/Runner/MainWindow.xaml:38`），因此“产品固定读 `asst.log`、所以读错文件”作废，正确表述是“中文 GUI 日志与 `asst.log` 结构化英文回调是两种格式，任务指向后者的当前解析器不支持”。②`RmGetList` 官方语义只覆盖“current using”，`RM_PROCESS_INFO` 无读写/时间区间字段；新增 `probe-maa-holder-matrix` 13 PASS，实测 S4：MAA 式 deny-write 写者关闭后外部追加“开始任务/任务已全部完成”，RM 仅列只读的根进程，候选规则仍判“成功”，故“RM 能证明每字节写入者”作废，只能表述为“采样时未见他者”。其余：S3 采样间隙漏掉整个写窗口、S2 只读打开者也被列出、S6 根退出后追加仍被解析但规则暂停、S7 不存在路径 RM 返回成功空表、S9 回执带停止/缺 taskid 均暂停。构建 0/0，受影响专项均退出码 0、0 失败，报告新增 §9（含 A/B/C/D/G/H 比较与推荐路径）。产品零改动，自动接续仍暂停。
- 2026-11-23 研究轮（方案 E 前提核对）：官方源码 `v6.22.0` 否定方案 E 的两个前提——①`Bootstrapper.OnStart` 在任何 MaaCore 加载前执行 `Directory.SetCurrentDirectory(AppContext.BaseDirectory)`，`Logger` 目录来自 `UserDir`（`UserDir/debug/asst.log`），`Logger::set_directory` 已整段注释，6.22.0 无改日志目录的 API 或配置；②现有 `MaaLogEvidence` 读 `asst.log` 却匹配 GUI 本地化中文（`开始任务:`/`任务已全部完成！`），真实 `asst.log` 只有英文 `Assistant::append_callback | <Event> {json}`，中文进的是 GUI 日志。事件模型补全：`TaskChainError`（`details.error` 五类，非致命后仍继续跑）、`TaskChainStopped`（完成回调后仍可能出现；顶层 catch 的 details 为空）、`AllTasksCompleted` 的 `finished_tasks` **含失败任务**，均不能当业务成功。新增四条隔离探针：`probe-maa-log-exclusive` 3 PASS（deny-write 独占期间不可写/截断/改名/删除、可读；退出后追加对锚点不可见）、`probe-maa-writer-identity` 3 PASS（Restart Manager 可绑定持有者 PID+创建时间，但只读打开者也会被列出）、`probe-maa-event-model` 5 PASS（回调行对中文解析器不可见、`TaskChainError` 单独出现被判成功、空 details 停止不被识别、`finished_tasks` 从不检查）、`probe-maa-session-rule` 9 PASS（测试内判定函数，9 分支只作对照，不进产品）。构建 0/0；受影响专项均退出码 0；完整回归 486 PASS / 1 FAIL / 0 SKIP（失败项 `MAA controlled start refuses another process window`，单独跑 17 PASS，属本机桌面焦点既有偶发）；`probe-regression-local` 472 PASS / 0 FAIL / 1 SKIP。产品零改动，替代方案 G 只到提案。

## 需求变更

- 无。
