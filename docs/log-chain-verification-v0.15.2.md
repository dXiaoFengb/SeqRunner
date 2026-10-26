# v0.15.2 日志链路核对表

本表只记录源码、夹具和自动化证据；真实业务程序、真实外部日志、Windows DPI 与人工视觉感受仍须按 `docs/manual-acceptance.md` 单独核验。

| 链路段 | 输入与输出 | 授权/边界 | 自动化证据 | 用户人工检查 |
| --- | --- | --- | --- | --- |
| 外部增量读取 | 已配置的单文件或直接子文件夹 → `ExternalLogWatcher.on_text` | 仅任务配置的路径；不递归、不读取任意前端路径 | `backend/tests/test_external_logs.py` | 临时多文件、轮转、短暂占用 |
| 本地转译与折叠 | 授权原始增量 → 中文 `ReadableEvent`、次数和首末时间 | 不保存原文；未知文本仅计数；不参与终态仲裁 | `backend/tests/test_readable_reports.py` | 中文业务日志的可读性 |
| 运行契约 | 监视器回调 → 事件、运行快照、WebSocket、任务/运行报告 | 原文只进入受限实时预览；报告只含转译事实 | `backend/tests/test_engine.py::test_external_log_increment_reaches_translation_snapshot_websocket_and_report` | 运行中网络断线恢复 |
| 前端实时区 | 快照与 `script_event` 增量 → 去重、替换、断档重同步 | 本软件运行状态与脚本转译分区；无来源选择交互 | `frontend/src/LiveLog.test.ts`、唯一 CDP 场景 | 持续滚动与视觉颜色 |
| 历史与原文 | 历史转译事件 → 独立详情；历史记录的文件引用 → 分页原文阅读器 | 仅历史项登记文件可读；复制/导出/定位仍经受限桥接 | `frontend/src/LogPage.test.ts`、唯一 CDP 场景 | 原文按钮、文件夹定位、导出对话框 |
| 验收关闭 | 副本 EXE → CDP 关闭 → 已记录进程树退出 | 只操作本轮副本及已追踪 PID；不得按进程名扫描 | `tools/ui-acceptance/acceptance-process-helpers.test.ps1`、`shutdown.json` | 确认无可见残留窗口 |

## v0.15.2 场景结论门槛

`tools/ui-acceptance/run.ps1` 只调用 `run-cdp-scenario.cjs --scenario log`。`result.txt`、`source.txt`、截图和 `shutdown.json` 必须同时存在；`shutdown.json` 的进程树状态必须为 `checked`，最终残留 PID 必须为空，且不得触发兜底终止。任一条件不满足即是自动验收失败，不以截图或单独的构建通过替代。
