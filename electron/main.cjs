const { app, BrowserWindow, clipboard, dialog, ipcMain, Menu, shell } = require('electron');
const { spawn, execFile } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { promisify } = require('node:util');
const { prepareDataDirectory } = require('./portable-data.cjs');

let backend; let windowRef; let connection; let quitting = false; let backendState = 'starting'; let backendDetail = '';
const sourceRoot = path.resolve(__dirname, '..');
function developmentVersion() {
  const versionFile = app.isPackaged ? path.join(app.getAppPath(), 'VERSION.md') : path.join(sourceRoot, 'VERSION.md');
  try { return fs.readFileSync(versionFile, 'utf8').match(/^\s*- 开发提交版本：\s*(v\d+\.\d+\.\d+)\s*$/m)?.[1] || 'v0.0.0'; } catch (_) { return 'v0.0.0'; }
}
const executableNames = { maa: ['maa.exe'], zzz_onedragon: ['onedragon-runtimelauncher.exe', 'onedragon-launcher.exe'], maaend: ['maaend.exe'], march7th: ['march7th assistant.exe'], bettergi: ['bettergi.exe'] };
function frontendEntry() { return app.isPackaged ? path.join(app.getAppPath(), 'frontend-dist', 'index.html') : path.join(sourceRoot, 'frontend-dist', 'index.html'); }
function runtimeDataDirectory() { return prepareDataDirectory({ packaged: app.isPackaged, sourceRoot, execPath: process.execPath, environment: process.env }); }
app.setPath('userData', path.join(runtimeDataDirectory(), 'electron'));
function backendCommand() { if (app.isPackaged) return { command: path.join(process.resourcesPath, 'backend', 'sequential-runner-backend.exe') }; const base = path.join(sourceRoot, 'backend'); return { command: path.join(base, '.venv', 'Scripts', 'python.exe'), args: ['-m', 'app.runner_server'], cwd: base }; }
function startBackend() {
  const target = backendCommand(); const token = crypto.randomUUID();
  backendState = 'starting'; backendDetail = '';
  backend = spawn(target.command, [...(target.args || []), '--data-dir', runtimeDataDirectory(), '--instance-id', token], { cwd: target.cwd || path.dirname(target.command), stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  return new Promise((resolve, reject) => {
    let output = ''; const timer = setTimeout(() => reject(new Error('后端未在 15 秒内就绪。请检查依赖、端口与 data 目录权限。')), 15000);
    const fail = (detail) => { backendState = 'failed'; backendDetail = detail; clearTimeout(timer); reject(new Error(`本地后端启动失败：${detail}`)); };
    backend.once('error', (error) => fail(error.message));
    backend.once('exit', (code) => { if (!quitting) { backendState = 'stopped'; backendDetail = `后端进程已退出（${code ?? '未知'}）。`; connection = undefined; if (windowRef) windowRef.webContents.send('runner:backend-state', { state: backendState, detail: backendDetail }); } if (!connection) fail(backendDetail); });
    backend.stdout.on('data', (chunk) => { output += chunk.toString(); const ready = output.match(/RUNNER_READY (\{[^\r\n]+\})/); if (!ready) return; try { const info = JSON.parse(ready[1]); connection = { apiBase: `http://127.0.0.1:${info.port}`, wsBase: `ws://127.0.0.1:${info.port}`, instanceId: token }; backendState = 'ready'; clearTimeout(timer); resolve(); } catch (error) { fail(`就绪信息无效：${error.message}`); } });
    backend.stderr.on('data', (chunk) => { output = `${output}\n${chunk}`.slice(-4000); backendDetail = output; });
  });
}
async function verifyBackend() { const response = await fetch(`${connection.apiBase}/health`); const body = await response.json(); if (!response.ok || body.instanceId !== connection.instanceId) throw new Error('未确认本次启动的后端实例。'); }
async function stopBackend() {
  if (!backend) return;
  const backendPid = backend.pid;
  try { if (connection) await fetch(`${connection.apiBase}/api/stop`, { method: 'POST', signal: AbortSignal.timeout(2500) }); } catch (_) {}
  // PyInstaller's windowless bootloader can keep a child alive after its launcher exits.
  // Scope taskkill to this Electron-created root PID and its tree; never search by name.
  if (process.platform === 'win32' && backendPid) {
    try { await promisify(execFile)('taskkill', ['/PID', String(backendPid), '/T', '/F'], { windowsHide: true }); } catch (_) {}
  } else if (!backend.killed) backend.kill();
  backend = undefined;
  connection = undefined;
}
function shutdown() {
  if (quitting) return;
  quitting = true;
  void (async () => {
    await stopBackend();
    app.exit(0);
  })();
}
ipcMain.handle('runner:get-connection', () => connection || null);
ipcMain.handle('runner:get-backend-status', () => ({ state: backendState, detail: backendDetail }));
ipcMain.handle('runner:get-build-info', () => ({ version: developmentVersion() }));
ipcMain.handle('runner:select-executable', async (_event, presetId) => { const picked = await dialog.showOpenDialog(windowRef, { properties: ['openFile'], filters: [{ name: 'Windows 可执行程序', extensions: ['exe'] }] }); if (picked.canceled || !picked.filePaths[0]) return { canceled: true }; const selected = picked.filePaths[0]; const expected = executableNames[presetId] || []; return { canceled: false, path: selected, matchesPreset: !presetId || expected.includes(path.basename(selected).toLowerCase()) }; });
ipcMain.handle('runner:select-directory', async () => { const picked = await dialog.showOpenDialog(windowRef, { properties: ['openDirectory'] }); return picked.canceled || !picked.filePaths[0] ? { canceled: true } : { canceled: false, path: picked.filePaths[0] }; });
function externalFile(source) {
  if (typeof source !== 'string' || !path.isAbsolute(source) || !fs.existsSync(source) || !fs.statSync(source).isFile()) throw new Error('外部日志文件不存在或不可读取。');
  return source;
}
async function authorizedHistoryFile(reference) {
  if (!connection || !reference || typeof reference.runId !== 'string' || typeof reference.taskId !== 'string' || !Number.isInteger(reference.index)) throw new Error('外部日志引用无效。');
  const endpoint = `${connection.apiBase}/api/history/${encodeURIComponent(reference.runId)}/external-logs/${encodeURIComponent(reference.taskId)}`;
  const response = await fetch(endpoint);
  if (!response.ok) throw new Error('外部日志引用已失效。');
  const body = await response.json();
  const file = body.files?.find((candidate) => candidate.index === reference.index && candidate.available);
  if (!file?.path) throw new Error('外部日志文件不存在或不可读取。');
  return externalFile(file.path);
}
ipcMain.handle('runner:copy-text', (_event, text) => { if (typeof text !== 'string') throw new Error('复制内容无效。'); if (Buffer.byteLength(text, 'utf8') > 10 * 1024 * 1024) throw new Error('日志内容超过 10 MiB，请使用导出。'); clipboard.writeText(text); return { accepted: true }; });
ipcMain.handle('runner:export-file', async (_event, reference) => { const file = await authorizedHistoryFile(reference); const picked = await dialog.showSaveDialog(windowRef, { defaultPath: path.basename(file) }); if (picked.canceled || !picked.filePath) return { canceled: true }; fs.copyFileSync(file, picked.filePath, fs.constants.COPYFILE_EXCL); return { canceled: false, path: picked.filePath }; });
ipcMain.handle('runner:show-in-folder', async (_event, reference) => { shell.showItemInFolder(await authorizedHistoryFile(reference)); return { accepted: true }; });
ipcMain.handle('runner:quit-app', () => { shutdown(); return { accepted: true }; });
async function createWindow() { await startBackend(); await verifyBackend(); Menu.setApplicationMenu(null); const healthCheck = process.env.RUNNER_HEALTH_CHECK === '1'; windowRef = new BrowserWindow({ width: 1280, height: 820, minWidth: 1280, minHeight: 820, show: !healthCheck, backgroundColor: '#0b1722', title: `顺序任务调度器 ${developmentVersion()}`, webPreferences: { contextIsolation: true, sandbox: true, nodeIntegration: false, preload: path.join(__dirname, 'preload.cjs') } }); windowRef.webContents.setWindowOpenHandler(() => ({ action: 'deny' })); const devServer = process.env.VITE_DEV_SERVER_URL; if (devServer) await windowRef.loadURL(devServer); else await windowRef.loadFile(frontendEntry()); if (healthCheck) setTimeout(shutdown, 1000); }
const lock = app.requestSingleInstanceLock(); if (!lock) app.quit();
app.on('second-instance', () => { if (windowRef) { if (windowRef.isMinimized()) windowRef.restore(); windowRef.focus(); } });
app.whenReady().then(createWindow).catch(async (error) => { await dialog.showMessageBox({ type: 'error', title: '启动失败', message: error.message }); app.quit(); });
app.on('before-quit', (event) => { if (quitting) return; event.preventDefault(); shutdown(); });
app.on('window-all-closed', () => app.quit());
