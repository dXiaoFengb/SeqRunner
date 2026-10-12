const { app, BrowserWindow, dialog, ipcMain } = require('electron');
const { spawn, execFile } = require('node:child_process');
const crypto = require('node:crypto');
const path = require('node:path');
const { promisify } = require('node:util');

let backend; let windowRef; let connection; let quitting = false; let backendState = 'starting'; let backendDetail = '';
const sourceRoot = path.resolve(__dirname, '..');
const executableNames = { maa: ['maa.exe'], zzz_onedragon: ['onedragon-runtimelauncher.exe', 'onedragon-launcher.exe'], maaend: ['maaend.exe'], march7th: ['march7th assistant.exe'], bettergi: ['bettergi.exe'] };
function runtimeDataDirectory() { return !app.isPackaged ? path.join(sourceRoot, 'data') : path.join(process.env.PORTABLE_EXECUTABLE_DIR || path.dirname(process.execPath), 'data'); }
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
ipcMain.handle('runner:select-executable', async (_event, presetId) => { const picked = await dialog.showOpenDialog(windowRef, { properties: ['openFile'], filters: [{ name: 'Windows 可执行程序', extensions: ['exe'] }] }); if (picked.canceled || !picked.filePaths[0]) return { canceled: true }; const selected = picked.filePaths[0]; const expected = executableNames[presetId] || []; return { canceled: false, path: selected, matchesPreset: !presetId || expected.includes(path.basename(selected).toLowerCase()) }; });
async function createWindow() { await startBackend(); await verifyBackend(); const healthCheck = process.env.RUNNER_HEALTH_CHECK === '1'; windowRef = new BrowserWindow({ width: 1280, height: 820, minWidth: 960, minHeight: 680, show: !healthCheck, backgroundColor: '#0b1722', title: '顺序任务调度器', webPreferences: { contextIsolation: true, sandbox: true, nodeIntegration: false, preload: path.join(__dirname, 'preload.cjs') } }); windowRef.webContents.setWindowOpenHandler(() => ({ action: 'deny' })); const devServer = process.env.VITE_DEV_SERVER_URL; if (devServer) await windowRef.loadURL(devServer); else await windowRef.loadFile(path.join(sourceRoot, 'frontend-dist', 'index.html')); if (healthCheck) setTimeout(shutdown, 1000); }
const lock = app.requestSingleInstanceLock(); if (!lock) app.quit();
app.on('second-instance', () => { if (windowRef) { if (windowRef.isMinimized()) windowRef.restore(); windowRef.focus(); } });
app.whenReady().then(createWindow).catch(async (error) => { await dialog.showMessageBox({ type: 'error', title: '启动失败', message: error.message }); app.quit(); });
app.on('before-quit', (event) => { if (quitting) return; event.preventDefault(); shutdown(); });
app.on('window-all-closed', () => app.quit());
