# 交接快照

> 本文件是覆盖式交接快照，不是任务状态、发布事实或授权的来源。更新时机见 `AGENTS.md`；未命中该时机且没有实质交接信息变化时，无需写入。

## 交接信息

- 交接时间：2026-10-13T14:15:00+08:00
- 执行者 / 会话标识：Codex
- 对应任务：见 `TASK.md`「v0.2.2 预设完整填充与末项完成流程修复」。

## 最近实际改动

- 手动输入 EXE 与原生文件选择共用带令牌的预设同步：名称精确匹配时才调用本地 `/api/presets/{id}/apply`，返回参数、工作目录、日志、多条终态规则和证据；旧异步响应不会覆盖后续路径或预设。失配路径被保留、清除预设证据并阻止运行；MaaEnd、三月七小助手和 BetterGI 保持人工确认回退。
- 脚本卡现只显示摘要、排序、运行和脚本级操作；“编辑脚本”进入同窗口独立完整编辑页。预设自动规则以独立面板显示，包含日志、成功/失败规则、完成方式和证据。卡片“更多”菜单经 Teleport 置于顶层，可点击外部或 Esc 关闭并回归焦点。
- 左侧导航改为固定全高栏，主内容独立滚动；Electron 原生标题、左侧品牌和设置页通过受限 IPC 读取 `VERSION.md` 的开发版本。打包配置将同一版本纳入 app.asar，并新增读取版本的 portable 包装器与 v0.2.1 隔离输出目录。
- v0.2.2 将预设的 600 秒超时显式写入本地证据表，并暴露“是否需要日志规则”元数据。路径匹配的旧任务在加载后自动补齐证据/规则；路径失配时保留路径、清除规则并保持运行保护。默认“新程序”改为预设名，已改名的任务不改名。
- 执行器现仅在还有后续程序时冷却十秒：末项成功或失败均先执行受管收尾，再直接记录整轮完成并清空冷却截止。编辑页返回栏改为正常文档流，不再覆盖路径和规则区域；运行中心仍保持粘性。

## 验证与证据

- 后端：`backend\\.venv\\Scripts\\python.exe -m pytest backend\\tests` 为 **32 passed, 1 warning**；`backend\\.venv\\Scripts\\python.exe -m ruff check backend` 为 **All checks passed**。首次沙箱运行被临时目录 `WinError 5` 阻断，切换项目内临时目录并在普通 Windows 终端复测通过。
- 前端：`corepack npm run typecheck`、`corepack npm run test:frontend`（**5 files / 11 tests**）、`corepack npm run build` 均通过；`corepack npm run electron:health` 隐藏窗口启动/停止通过。首次 Vitest 在受限沙盒因 `spawn EPERM` 无法启动 esbuild，普通 Windows 终端复测通过。
- PyInstaller：新的无控制台后端 EXE 已生成，随机回环端口 `/health` 返回本次实例 `v021-backend-health`，预设接口返回 HTTP 200。最终后端 EXE SHA-256 为 `FB08DA48DFAA4B89B98FBFBF9AE157E74F9D2F936D81C8D0371DCA5DD906D13F`。
- Portable：2026-10-13 以普通 Windows 终端重新执行 `corepack npm run package:validation`。输出 `portable-validation-v021\\electron\\顺序任务调度器-v0.2.1-portable.exe`，大小 95,382,239 字节，SHA-256 为 `BEC73EFB07A1EAED5DD8C9EDAB0FC28BEA97D04A185C87BD87523A4B9FBFD1CB`。设置 `RUNNER_HEALTH_CHECK=1` 后隐藏启动退出码为 0；退出后未发现位于验证目录的后端残留。未发布、未替换既有产物。
- Portable：v0.2.2 重新生成于 `portable-validation-v022\\electron\\顺序任务调度器-v0.2.2-portable.exe`，大小 95,382,102 字节，SHA-256 为 `0A87402EC9D7F10F018B5B2FCD30015B139D101463F891BD6C6BF01A5672BEC1`。设置 `RUNNER_HEALTH_CHECK=1` 后隐藏启动退出码为 0；退出后未发现位于验证目录的 Electron 或后端残留。未发布、未替换既有产物。
- Git：开发提交版本已按用户确认由 v0.2.1 更新至 v0.2.2；验证包、构建和最小自动化已完成。2026-10-13 当前受限执行环境写入 `.git/index.lock` 被拒绝，且普通终端的授权通道不可用，故尚未暂存或创建本地提交；未推送。

## 本次交接周期内的高风险操作

- 无（发生时逐项记录时间、对象、结果和当次确认来源）

> 本节只记录本次交接周期内已发生的对象、结果和当次确认来源；下一次交接更新时会被覆盖，不构成后续会话的授权。长期预先授权只看 `CONSTRAINTS.md`，发布事实只看 `VERSION.md`；需要长期留存时，按 `CONSTRAINTS.md` 已选方式另行完成。

## 下一步

1. 按 `docs/manual-acceptance.md` 完成 v0.2.2 可视验收：版本显示、手输/选择预设、手工规则分界、旧任务补齐、两项任务中间冷却/末项直达、编辑页滚动和停止/计划；真实业务软件仍不启动。
2. 在可写入 Git 索引的普通 Windows 终端中，精确暂存本轮列出的文件，审查缓存差异后创建本地提交 `v0.2.2 修复:完善预设规则与末项完成流程`，不推送；完成后再据实记录人工验收结果。
