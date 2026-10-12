# 交接快照

> 本文件是覆盖式交接快照，不是任务状态、发布事实或授权的来源。更新时机见 `AGENTS.md`；未命中该时机且没有实质交接信息变化时，无需写入。

## 交接信息

- 交接时间：2026-10-12T19:42:18+08:00
- 执行者 / 会话标识：Codex
- 对应任务：见 `TASK.md`

## 最近实际改动

- 后端改为随机回环端口与每次启动实例标识；`/health` 校验本次实例，加入最小 CORS 白名单，配置写入错误回报中文且保持原文件。
- 新增经证据核验的五个预设元数据：MAA、绝区零一条龙采用多条日志终态规则；MaaEnd、三月七小助手、BetterGI 均为“路径 + 人工确认”，不把普通退出伪装为成功。
- 本地参考证据固定为 AUTO-MAS 提交 `699de5af57b7955a19ed4585f1fe64f9430e1e29`、AUTO-MAS-docs 提交 `a386be2d5e73e48cced5d45639e5c7eae7eda282`；官方链接记录访问日期，不复制上游内容。
- Electron 使用 `contextIsolation=true`、`sandbox=true`、`nodeIntegration=false`，preload 只暴露连接信息和 `.exe` 选择；主进程以 `windowsHide` 启动后端，目标进程以 `CREATE_NO_WINDOW` 启动。已加入 PyInstaller 和 portable electron-builder 配置。
- 前端改为深石板蓝的四区工作台（队列/计划/历史/设置），任务卡可折叠、拖拽排序、窄屏单列、独立完成信号区与折叠诊断。
- 修复 portable 空白页：Vite 生产构建改用相对 `./assets/` 地址，避免 Electron 的 `file://` 加载将 `/assets/` 解析到磁盘根目录。

## 验证与证据

- 自动化测试 / 构建：已在受限沙箱外执行 `corepack npm run build`，Vite 生产构建通过，另以生成的 HTML 与 `release/win-unpacked/resources/app.asar` 确认 JS/CSS 均引用 `./assets/...`。随后完整 `corepack npm run package:win` 完成前端、PyInstaller 后端和 electron-builder 的打包阶段；初次尝试因用户正在运行旧 EXE 而挂起在最终替换阶段，关闭后改由已重建的 `win-unpacked` 生成独立修复包。此前的 Ruff、pytest（20 passed）、类型检查、Vitest（2 passed）、生产构建和开发模式 Electron 健康检查结论保持不变。受限环境仍拒绝 Electron 运行时单实例锁，因此 portable 启动健康检查只能人工执行。
- 产物或发布核对：包结构已核验，含 `release/win-unpacked/resources/backend/sequential-runner-backend.exe`。供本轮人工验收的本地被忽略产物为 `release/顺序任务调度器-0.1.0-portable-fixed.exe`，95,076,735 字节，SHA-256 `A666C1A3304B3DE0DF66E4A6289A176403EE5A6AC8B3F05030AB7D65B92E7DE3`。未发布、未替换任何正式产物。
- 人工验收：修复包等待用户在普通 Windows 桌面会话执行；首项为无黑框启动、已加载工作台、后端连接状态与日志目录确认。
- 证据位置：`docs/state-machine.md`、`docs/manual-acceptance.md`；本地命令输出见本次执行记录。
- 未验证项与限制：未启动真实业务程序；修复包的用户 Windows 可视验收仍待完成。当前环境不允许 Electron 单实例锁/进程通道，故不能以自动方式完成 portable EXE 的启动健康检查；修复后的生成、包内资源路径、结构和 SHA-256 核验已完成。

## 本次交接周期内的高风险操作

- 无（发生时逐项记录时间、对象、结果和当次确认来源）

> 本节只记录本次交接周期内已发生的对象、结果和当次确认来源；下一次交接更新时会被覆盖，不构成后续会话的授权。长期预先授权只看 `CONSTRAINTS.md`，发布事实只看 `VERSION.md`；需要长期留存时，按 `CONSTRAINTS.md` 已选方式另行完成。

## 下一步

1. 用户按 `docs/manual-acceptance.md` 的第 1 项双击 `portable-fixed.exe`，确认无黑框、工作台加载、后端连接及 `data/logs`；如失败，提供折叠诊断和日志末尾内容。
2. 完成后续队列、日志、定时、停止、预设及窄窗口验收，并提供真实程序路径、参数及完成信号后另行安排真实程序验收。
