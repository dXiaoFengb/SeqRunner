const { app, BrowserWindow, dialog } = require('electron');
const { spawn } = require('node:child_process');
const path = require('node:path');

let backend;
let windowRef;
const root = path.resolve(__dirname, '..');
const backendCwd = path.join(root, 'backend');
const python = process.platform === 'win32'
  ? path.join(backendCwd, '.venv', 'Scripts', 'python.exe')
  : path.join(backendCwd, '.venv', 'bin', 'python');

function startBackend() {
  backend = spawn(python, ['-m', 'uvicorn', 'app.main:app', '--host', '127.0.0.1', '--port', '38163'], {
    cwd: backendCwd,
    stdio: 'pipe',
    windowsHide: true,
  });
  backend.stderr.on('data', (chunk) => console.error(`[backend] ${chunk}`));
}

async function waitForBackend() {
  for (let attempt = 0; attempt < 60; attempt += 1) {
    try {
      const response = await fetch('http://127.0.0.1:38163/health');
      if (response.ok) return;
    } catch (_) {}
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error('本地后端未能在 15 秒内启动。请检查 backend/.venv 与依赖。');
}

function stopBackend() {
  if (backend && !backend.killed) backend.kill();
  backend = undefined;
}

async function createWindow() {
  startBackend();
  await waitForBackend();
  const healthCheck = process.env.RUNNER_HEALTH_CHECK === '1';
  windowRef = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 960,
    minHeight: 680,
    backgroundColor: '#10242b',
    title: '顺序任务调度器',
    show: !healthCheck,
    webPreferences: { contextIsolation: true, sandbox: true },
  });
  const devServer = process.env.VITE_DEV_SERVER_URL;
  if (devServer) await windowRef.loadURL(devServer);
  else await windowRef.loadFile(path.join(root, 'dist', 'index.html'));
  if (healthCheck) setTimeout(() => app.quit(), 1000);
}

app.whenReady().then(createWindow).catch(async (error) => {
  await dialog.showMessageBox({ type: 'error', title: '启动失败', message: error.message });
  app.quit();
});
app.on('before-quit', stopBackend);
app.on('window-all-closed', () => app.quit());
