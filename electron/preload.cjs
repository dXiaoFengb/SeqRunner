const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('desktopRunner', {
  getConnection: () => ipcRenderer.invoke('runner:get-connection'),
  getBackendStatus: () => ipcRenderer.invoke('runner:get-backend-status'),
  selectExecutable: (presetId) => ipcRenderer.invoke('runner:select-executable', presetId || null),
});
