const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('desktopRunner', {
  getConnection: () => ipcRenderer.invoke('runner:get-connection'),
  getBackendStatus: () => ipcRenderer.invoke('runner:get-backend-status'),
  getBuildInfo: () => ipcRenderer.invoke('runner:get-build-info'),
  getSettings: () => ipcRenderer.invoke('runner:get-settings'),
  saveSettings: (settings) => ipcRenderer.invoke('runner:save-settings', settings),
  exportArchive: (request) => ipcRenderer.invoke('runner:export-archive', request),
  openDevTools: () => ipcRenderer.invoke('runner:open-devtools'),
  showNotification: (request) => ipcRenderer.invoke('runner:show-notification', request),
  selectExecutable: (presetId) => ipcRenderer.invoke('runner:select-executable', presetId || null),
  selectDirectory: () => ipcRenderer.invoke('runner:select-directory'),
  copyText: (text) => ipcRenderer.invoke('runner:copy-text', text),
  exportFile: (reference) => ipcRenderer.invoke('runner:export-file', reference),
  showInFolder: (reference) => ipcRenderer.invoke('runner:show-in-folder', reference),
  quitApp: () => ipcRenderer.invoke('runner:quit-app'),
});
