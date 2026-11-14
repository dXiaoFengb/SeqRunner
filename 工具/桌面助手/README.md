# 本地桌面助手

这个工具用于在 Windows 上识别指定进程的窗口、查看无障碍控件，并在明确指定目标时调用控件或发送鼠标与键盘输入。它使用项目目录内的 Python 依赖，不修改全局 Python 环境，也不会自动启动虚拟机、程序、下载或联网。

## 使用前

需要 Windows 和 Python 3.13。当前机器上的依赖保存在同目录 `vendor/`，安装包保存在 `wheels/`，二者保留在本机、不提交 Git。直接运行以下命令检查：

```powershell
python '工具/桌面助手/desktop_assistant.py' status
```

按具体任务获得目标进程 PID 后，先列出可见窗口，然后使用输出的精确 handle：

```powershell
python '工具/桌面助手/desktop_assistant.py' windows --pid <进程ID>
python '工具/桌面助手/desktop_assistant.py' inspect --pid <进程ID> --handle <窗口句柄>
```

`inspect` 只读取所选窗口的无障碍控件，控件名称可能包含窗口中的用户内容；不要把其输出上传或不必要地保存。

需要操作时，先人工或通过可靠程序确认当前目标及其状态，再显式指定 `--execute`：

```powershell
python '工具/桌面助手/desktop_assistant.py' invoke --pid <进程ID> --handle <窗口句柄> --automation-id <控件ID> --execute
python '工具/桌面助手/desktop_assistant.py' invoke --pid <进程ID> --handle <窗口句柄> --name <控件名称> --execute
python '工具/桌面助手/desktop_assistant.py' click --pid <进程ID> --handle <窗口句柄> --x <距左侧像素> --y <距顶部像素> --execute
python '工具/桌面助手/desktop_assistant.py' keys --pid <进程ID> --handle <窗口句柄> --keys <按键表达式> --execute
```

`invoke` 必须精确匹配唯一的控件；`click` 和 `keys` 要求目标窗口已经位于前台，且 `click` 检查位置在该窗口内、鼠标实际到位和窗口未被覆盖。不能通过这个助手绕过系统权限；进程 ID 或句柄可能变化，操作前必须重新识别，不能复用旧值。坐标受窗口布局、缩放和遮挡影响，需先查看实际界面。不要在参数中放密码、密钥或其他敏感数据。

`keys` 使用 pywinauto 的按键语法，默认 Unicode 输入；可明确添加 `--scan-codes` 使用扫描码，但扫描码可能受输入法影响。工具报告“已发送”只表示请求已发出，不证明目标收到正确内容；必须通过目标窗口或受控结果核实。高权限窗口、虚拟机捕获输入和客户机屏幕可能无法接收此类输入，不自动尝试提权或绕过限制。

## 来源与当前验证

- [pywinauto 0.6.9](https://pypi.org/project/pywinauto/0.6.9/)：BSD 3-Clause。
- 本地依赖：comtypes 1.5.0（MIT）、pywin32 312（许可见其软件包元数据）、six 1.17.0（MIT）。
- 四个下载 wheel 的 SHA-256 已与对应 PyPI 版本元数据核对；依赖仅从本地 wheel 安装到 `vendor/`。
- 自建 WinForms 窗口已验证：窗口与控件识别、按钮调用、默认 Unicode 输入和鼠标点击；进程不匹配以及缺少 `--execute` 均会拒绝操作。
- VMware 主机界面已验证可识别，并可打开、取消关闭虚拟机设置窗口；本轮未修改设置或启动虚拟机。
- 扫描码在本机中文输入法开启时出现字符转换；默认 Unicode 输入在该受控窗口复测正常。此前环境中 VMware 客户机键盘输入受限，**本工具能否向 VMware 客户机可靠输入仍待验证**。VMware 主窗口可识别不代表客户机可操作。
- 真正运行 MAA 前，仍需先完成经用户认可的隔离检查。不要把账户、原 MAA 配置、完整日志或截图纳入版本控制或发往外部。

wheel SHA-256：

```text
pywinauto-0.6.9-py2.py3-none-any.whl 5924b3072864a1d730c5546bbeb17cf4063ba518b618dbc5e43c18276c7c9356
comtypes-1.5.0-py3-none-any.whl c648ed3b243b1eb3b7cb893a873d1b35104ca1c8922061f4b1efafeb255bacb5
pywin32-312-cp313-cp313-win_amd64.whl c53e878d15a1c44788082bfe712a905433473aa38f86375b7cf8b45e3acbaaf9
six-1.17.0-py2.py3-none-any.whl 4721f391ed90541fddacab5acf947aa0d3dc7d27b2e1e8eda2be8970586c3274
```
