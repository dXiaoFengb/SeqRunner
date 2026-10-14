# 交接快照

> 本文件是覆盖式交接快照，不是任务状态、发布事实或授权的来源。更新时机见 `AGENTS.md`；未命中该时机且没有实质交接信息变化时，无需写入。

## 交接信息

- 交接时间：2026-10-14T19:43:00+08:00
- 执行者 / 会话标识：Codex
- 对应任务：见 `TASK.md`「v0.4.0 专用脚本配置与多软件计划管理」。

## 最近实际改动

- 配置架构升级为 v2：首次迁移先创建 `data/backups/config-before-v2-*.json` 恢复副本，再清空旧单软件时间槽；旧软件脚本、预设与历史保持不变。多软件计划独立保存，要求至少两个软件、同刻冲突拒绝且全局只允许一个启动时运行计划；执行器忙碌时计划直接跳过并写入运行/历史摘要。
- MAA 编辑页仅保存安装目录与安卓模拟器 ADB 预检信息；预检失败写警告但仍启动 MAA GUI，绝不改写 MAA 或模拟器配置。MaaEnd 仅支持受管 Windows 游戏→等待→MaaEnd，按开关收尾本轮创建的游戏 PID 与子树。
- 运行中心改为整合状态徽标，显示运行来源、计划、当前软件、进度、失败详情和跳过计划；脚本管理不再提供定时入口，计划管理成为唯一的多软件定时页。
- 验证包构建脚本的验证专用配置采用 `compression: store`，避免受限会话中长时间压缩子进程被中断；正式发布构建配置未改变。已生成独立无控制台验证包：`portable-validation-v040\\electron\\顺序任务调度器-v0.4.0-portable.exe`，未覆盖既有验证包或正式产物。

## 验证与证据

- 回归：项目内临时目录的后端 pytest **40 passed, 1 warning**，`ruff check backend`、Vue typecheck、Vitest **5 文件 / 14 项**、生产构建与开发模式 Electron 健康检查均通过。最初受限会话的 Chromium 单实例锁失败，已在正常权限会话复测通过；未启动真实业务软件。
- 构建与健康：`corepack npm run package:validation` 完成 Vite、PyInstaller 无控制台后端和 Windows portable 封装；包内存在 `portable-validation-v040\\electron\\win-unpacked\\resources\\backend\\sequential-runner-backend.exe`。最终 EXE 大小 **322,983,367** 字节，SHA-256 `B56C3175D0D935A65A874116203BB4FC3054C5DC87D085217EAEF17EA3FA7229`；`RUNNER_HEALTH_CHECK=1` 隐藏启动退出码为 0。
- Git：开发提交版本已从 `v0.3.5` 推进为 `v0.4.0`；待本轮精确暂存、缓存差异审查和本地提交。不得推送或发布。

## 本次交接周期内的高风险操作

- 无（发生时逐项记录时间、对象、结果和当次确认来源）

> 本节只记录本次交接周期内已发生的对象、结果和当次确认来源；下一次交接更新时会被覆盖，不构成后续会话的授权。长期预先授权只看 `CONSTRAINTS.md`，发布事实只看 `VERSION.md`；需要长期留存时，按 `CONSTRAINTS.md` 已选方式另行完成。

## 下一步

1. 由用户使用 `portable-validation-v040\\electron\\顺序任务调度器-v0.4.0-portable.exe` 按 `docs/manual-acceptance.md` 进行 v0.4.0 桌面可视验收，重点确认多软件计划冲突/跳过提示、MAA/MaaEnd 专用字段、运行中心状态条与窄窗口布局；真实业务软件仍不启动。
