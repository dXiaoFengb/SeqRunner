# 交接快照

> 本文件是覆盖式交接快照，不是任务状态、发布事实或授权的来源。更新时机见 `AGENTS.md`；未命中该时机且没有实质交接信息变化时，无需写入。

## 交接信息

- 交接时间：2026-10-12T20:10:00+08:00
- 执行者 / 会话标识：Codex
- 对应任务：见 `TASK.md`

## 最近实际改动

- 前端连接逻辑抽至 `useRunnerConnection.ts`：REST 与实时状态独立、初始化/连接/重连单飞、指数退避上限与诊断、20 秒 `ping`/45 秒超时、实时断开时 5 秒降级轮询。保存、脚本运行和停止只依赖 REST 在线状态。
- 后端 `/ws` 同时订阅事件与接收客户端消息，回应 `ping` 为 `pong`；断开时取消接收/订阅任务并释放订阅。新增 WebSocket ping 与订阅清理测试。
- 工作台正式把队列呈现为脚本管理：搜索、全部展开/收起、紧凑卡片、编辑/运行/更多、复制不复制定时槽；计划、历史和设置页同步采用脚本语义，但 API 与 JSON 的 `queues`/`tasks` 保持兼容。
- Electron 通过只读 IPC 提供后台状态诊断；关闭路径改为只对本进程持有的后端根 PID 使用 Windows `/T` 受控收尾，以覆盖 PyInstaller 窗口隐藏启动器的子进程，未按名称扫描。

## 验证与证据

- 自动化测试 / 构建：受限沙箱外已执行 `pytest backend\\tests`（21 passed）、`ruff check backend`、`npm run typecheck`、`npm run test:frontend`（3 files / 4 tests）、`npm run build`、PyInstaller 后端回环 `/health`（实例标识匹配）、Electron 开发健康检查和打包 `win-unpacked` 健康检查（退出码 0）。首次 portable 打包的 Electron 与 NSIS 缓存存在跨盘重命名错误；将构建缓存固定到项目忽略的临时目录并使用项目本地 Electron 运行时后成功完成。
- 产物或发布核对：包结构已核验，含 `release/win-unpacked/resources/backend/sequential-runner-backend.exe`；生产 HTML 引用 `./assets/...`，`app.asar` 含最新 `index-nF0AQmgg.js`。供本轮人工验收的本地被忽略产物为 `release/顺序任务调度器-0.1.0-portable-connection.exe`，SHA-256 `FCA2E3E786D783E0B819EC78A10C2281A95BB1ADFF52B42248EA0829EF89DC7D`。未发布、未替换正式产物。
- 人工验收：连接修复包等待用户在普通 Windows 桌面会话执行；首项为三分钟空闲连接观察，其后验证脚本管理操作、无黑框、定时与进程行为。
- 证据位置：`docs/state-machine.md`、`docs/manual-acceptance.md`；本地命令输出见本次执行记录。
- 未验证项与限制：未启动真实业务程序；新包的用户 Windows 可视验收仍待完成。自动健康检查证明隐藏窗口启动/退出和后端回收，但不替代三分钟真实桌面连接观察、文件对话框、任务卡拖拽和窄窗口视觉验收。

## 本次交接周期内的高风险操作

- 无（发生时逐项记录时间、对象、结果和当次确认来源）

> 本节只记录本次交接周期内已发生的对象、结果和当次确认来源；下一次交接更新时会被覆盖，不构成后续会话的授权。长期预先授权只看 `CONSTRAINTS.md`，发布事实只看 `VERSION.md`；需要长期留存时，按 `CONSTRAINTS.md` 已选方式另行完成。

## 下一步

1. 用户按 `docs/manual-acceptance.md` 的第 1–3 项双击 `portable-connection.exe`，完成三分钟空闲连接观察和脚本管理基本操作；如失败，提供折叠诊断和日志末尾内容。
2. 完成后续脚本、日志、定时、停止、预设及窄窗口验收，并提供真实程序路径、参数及完成信号后另行安排真实程序验收。
