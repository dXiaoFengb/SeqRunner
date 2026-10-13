# 交接快照

> 本文件是覆盖式交接快照，不是任务状态、发布事实或授权的来源。更新时机见 `AGENTS.md`；未命中该时机且没有实质交接信息变化时，无需写入。

## 交接信息

- 交接时间：2026-10-13T12:26:00+08:00
- 执行者 / 会话标识：Codex
- 对应任务：见 `TASK.md`「v0.1.2 连接稳定性与路径选择修复」。

## 最近实际改动

- 后端固定声明 `websockets==15.0.1`，启动前验证协议实现、显式以 Uvicorn `websockets` 协议运行；`/ws` 对异常记录后端日志、受控关闭，并持续保持首帧、`ping/pong`、事件订阅和断开清理。
- PyInstaller spec 显式收集 Uvicorn WebSocket 与 `websockets` 子模块。后端 EXE 使用无控制台 bootloader；Electron 继续以 `windowsHide` 启动它，窗口关闭只回收本应用持有的 PID 树。
- 前端保存改为带本地修订号的单通道快照队列，过期响应不再回写旧空路径。选择 EXE 会立即回填路径和工作目录；离线时保留草稿；预设不匹配时保留路径、禁止运行，并要求重新选择或显式切换通用程序。
- 脚本页顶部只保留 API/实时状态与停止操作；完整中文诊断移入“设置”页并默认折叠。样式分离运行按钮与运行记录，保持脚本、计划、历史、设置的紧凑工作台布局。
- 新增隔离验证构建配置和项目内 Electron 缓存，避免本机缓存跨卷重命名失败。所有本轮验证产物均在 Git 忽略的 `portable-validation-v012-*` 下，未覆盖 `portable/`、`release/` 或既有 EXE。

## 验证与证据

- 后端：`backend\\.venv\\Scripts\\python.exe -m pytest backend\\tests` 为 **23 passed, 1 warning**；`backend\\.venv\\Scripts\\python.exe -m ruff check backend` 为 **All checks passed**。真实 Uvicorn 子进程测试完成首帧和 `ping/pong`，事件广播与订阅释放由新增用例覆盖。
- 打包后端：`portable-validation-v012\\backend\\sequential-runner-backend.exe` 实际输出 `RUNNER_READY`，随后回环 WebSocket 收到 `runtime` 首帧和 `pong`。隐藏观察器维持一个会话 180.1 秒、十次心跳均为 `pong`，未记录 1006 或重连。
- 前端：`corepack npm run typecheck`、`corepack npm run test:frontend`（**3 files / 6 tests**）、`corepack npm run build` 均通过；`corepack npm run electron:health` 隐藏窗口启动/停止通过。
- portable：`corepack npm run package:validation` 完成。包内未打包目录已核验含 `resources\\backend\\sequential-runner-backend.exe`；单文件 portable 以 `RUNNER_HEALTH_CHECK=1` 隐藏启动并退出码 **0**。验证包为 `portable-validation-v012-r3\\electron\\顺序任务调度器-0.1.0-portable.exe`，SHA-256 为 `0BF038DA8DA2CD5C038E5C610B4ACBD1D265E59AE3F0853EF0B3B02D6AC97582`；未发布。
- 已知构建环境事实：首次验证打包因 Electron 全局缓存跨卷重命名失败；将缓存设为项目内忽略目录后完成。npm 的 `global-ignore-file` 提示为配置弃用警告，不影响构建结果。
- Git：核心修复已创建本地提交 `aa552a7`（`v0.1.2 修复:修正实时连接与程序路径保存`），未推送。随后发现 pytest 生成的 `backend/.test-tmp*` 未被规则覆盖，已补充精确忽略项并将按自动留痕规则形成独立修订提交。
- 人工验收与限制：普通 Windows 桌面可视验收仍待用户执行，步骤见 `docs/manual-acceptance.md`。需核对实际窗口无黑框、原生文件对话框、窄窗口布局、保存路径和预设不匹配处理；未启动真实业务程序。

## 本次交接周期内的高风险操作

- 无（发生时逐项记录时间、对象、结果和当次确认来源）

> 本节只记录本次交接周期内已发生的对象、结果和当次确认来源；下一次交接更新时会被覆盖，不构成后续会话的授权。长期预先授权只看 `CONSTRAINTS.md`，发布事实只看 `VERSION.md`；需要长期留存时，按 `CONSTRAINTS.md` 已选方式另行完成。

## 下一步

1. 用户双击 `portable-validation-v012-r3\\electron\\顺序任务调度器-0.1.0-portable.exe`，按 `docs/manual-acceptance.md` 完成 3 分钟桌面观察、路径/预设、脚本运行、停止和计划验收。
2. 用户提供真实程序路径与完成信号后，另建任务安排真实业务程序验收；本轮不启动它们。
