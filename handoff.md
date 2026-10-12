# 交接快照

> 本文件是覆盖式交接快照，不是任务状态、发布事实或授权的来源。更新时机见 `AGENTS.md`；未命中该时机且没有实质交接信息变化时，无需写入。

## 交接信息

- 交接时间：2026-10-12T15:25:00+08:00
- 执行者 / 会话标识：Codex
- 对应任务：见 `TASK.md`

## 最近实际改动

- 后端改为随机回环端口与每次启动实例标识；`/health` 校验本次实例，加入最小 CORS 白名单，配置写入错误回报中文且保持原文件。
- 新增经证据核验的五个预设元数据：MAA、绝区零一条龙采用多条日志终态规则；MaaEnd、三月七小助手、BetterGI 均为“路径 + 人工确认”，不把普通退出伪装为成功。
- Electron 使用 `contextIsolation=true`、`sandbox=true`、`nodeIntegration=false`，preload 只暴露连接信息和 `.exe` 选择；主进程以 `windowsHide` 启动后端，目标进程以 `CREATE_NO_WINDOW` 启动。已加入 PyInstaller 和 portable electron-builder 配置。
- 前端改为深石板蓝的四区工作台（队列/计划/历史/设置），任务卡可折叠、拖拽排序、窄屏单列、独立完成信号区与折叠诊断。

## 验证与证据

- 自动化测试 / 构建：`ruff check backend` 通过；`pytest backend/tests` 20 passed；`corepack npm run typecheck` 通过；`corepack npm run test:frontend` 2 passed；`corepack npm run build` 通过；`corepack npm run electron:health` 命令正常退出。
- 产物或发布核对：PyInstaller 6.16.0 后端已生成并健康启动，SHA-256 为 `9AC01D1B79E6F7CD368168CDC95C31AF3F8A728D56E5E82E3DE6C0B39E088E8C`，位置为被忽略的 `portable/backend/sequential-runner-backend.exe`。electron-builder 使用固定版本的 `npm exec` 配置，但本机 npm 策略拒绝其间接 Git 依赖，未生成 Windows portable EXE、未发布。
- 人工验收：待用户按 `docs/manual-acceptance.md` 在 Windows 上执行。
- 证据位置：`docs/state-machine.md`、`docs/manual-acceptance.md`；本地命令输出见本次执行记录。
- 未验证项与限制：未启动真实业务程序；用户 Windows 可视验收仍待完成。需允许 electron-builder 的受限依赖后，执行 `npm run package:win`、portable EXE 启停和 SHA-256 核验。

## 本次交接周期内的高风险操作

- 无（发生时逐项记录时间、对象、结果和当次确认来源）

> 本节只记录本次交接周期内已发生的对象、结果和当次确认来源；下一次交接更新时会被覆盖，不构成后续会话的授权。长期预先授权只看 `CONSTRAINTS.md`，发布事实只看 `VERSION.md`；需要长期留存时，按 `CONSTRAINTS.md` 已选方式另行完成。

## 下一步

1. 在 npm 策略允许 electron-builder 间接依赖后执行 `corepack npm run package:win`，核验完整 portable EXE 内容、启停和 SHA-256。
2. 用户按 `docs/manual-acceptance.md` 完成可视验收，并提供真实程序路径、参数及完成信号后另行安排真实程序验收。
