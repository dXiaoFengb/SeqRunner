const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('desktopRunner', {
  getConnection: () => ipcRenderer.invoke('runner:get-connection'),
  getBackendStatus: () => ipcRenderer.invoke('runner:get-backend-status'),
  getBuildInfo: () => ipcRenderer.invoke('runner:get-build-info'),
  selectExecutable: (presetId) => ipcRenderer.invoke('runner:select-executable', presetId || null),
  selectDirectory: () => ipcRenderer.invoke('runner:select-directory'),
});
