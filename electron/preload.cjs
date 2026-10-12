const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('desktopRunner', { getConnection: () => ipcRenderer.invoke('runner:get-connection'), selectExecutable: (presetId) => ipcRenderer.invoke('runner:select-executable', presetId || null) });
