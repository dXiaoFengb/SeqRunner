# 隔离 Electron 日志自动验收

本工具只有一个实际执行入口：`run.ps1` 启动隔离副本后调用 `run-cdp-scenario.cjs --scenario log`。它仅在功能批次完成或 EXE 门禁触发后使用；普通功能的开发态检查改用 `../../docs/审核覆盖矩阵.md` 所述 `产物/预览/` 预览流程。它将只读的 portable 验证包复制到新建的 `build/ui-acceptance/<唯一标识>/`，在该目录创建虚拟配置、受控历史记录和脱敏外部日志夹具；不读取项目 `data/`、用户数据或真实业务日志，也不启动业务软件或系统操作。

场景只通过 CDP 附着本次 Electron 渲染页面，不创建额外浏览器标签。无论通过或失败，启动器都会请求关闭该副本窗口、核验本次已记录的进程树，并将关闭请求、兜底情况和残留 PID 写入 `shutdown.json`。残留 PID 非空、进程树未知或需要兜底终止时，自动验收不得报告通过。

## 安装和运行

要求 Windows、Node.js 20 及以上、PowerShell 7，以及已经生成的独立 portable 验证包；不需要下载浏览器。运行前按 `../../REVIEW.md`、`../../GIT.md` 与 `../../TASK.md` 确认审核范围、隔离边界和“本轮验收目标 EXE”。依赖使用局部安装并跳过依赖脚本：

```powershell
corepack npm ci --prefix tools/ui-acceptance --ignore-scripts --no-audit --no-fund --cache build/ui-acceptance-npm-cache
pwsh -NoProfile -File tools/ui-acceptance/run.ps1 -Executable '<TASK.md 指定且已核验 SHA-256 的 portable EXE 路径>'
```

默认场景就是 `log`；不再存在未执行的备用 Playwright、计划或日志场景路径。脚本默认只接受本工作区 `产物/验证/vX.Y.Z/` 根目录下以 `-portable.exe` 结尾的文件。版本事务最终化前可显式传入 `-AllowWorkingCandidate`，仅验收同一工作区 `产物/工作/vX.Y.Z/软件包/` 下的 portable EXE；验收的 `source.txt` 会记录源与副本 SHA-256，最终化后必须核对主 EXE 哈希未变。该开关不接受其他工作目录、历史包或业务程序。

运行时通过 `RUNNER_USER_DATA_DIR` 使用隔离用户数据目录。成功时输出 `Evidence=...`；其中 `source.txt` 记录候选与哈希，`result.txt` 记录场景断言，`shutdown.json` 记录关闭与进程树证据，`final.png` 与 `viewport-*.png` 用于复核。

## 唯一日志场景的实际覆盖

`log` 场景使用受控虚拟历史记录和外部日志夹具，断言：

- 日期范围筛选、纵向任务列表、独立脚本详情页，以及返回后的列表滚动位置和焦点恢复；
- 固定高度的脚本可读事件阅读区；
- 仅记录在历史项中的多个外部日志引用：第一页读取、加载更多、第二文件切换和不可读文件状态；
- 运行状态摘要与脚本实时转译分层；连续折叠事件以原序号替换；WebSocket 序号断档后从受控运行快照重同步；
- 左侧实时区没有来源选择条；
- 场景结束后 CDP 关闭、受跟踪进程树退出和零残留 PID。

它不验证真实业务软件、真实外部日志、OCR/窗口可见性、Windows DPI/视觉观感、计划编辑交互、实际系统操作，或用户手动验收中的其他步骤。模拟视口不是实际 Windows 窗口宽度或 DPI 验收；这些项目仍按 `../../docs/manual-acceptance.md` 单独执行。
