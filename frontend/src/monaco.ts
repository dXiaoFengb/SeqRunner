import loader from '@monaco-editor/loader'
import { VueMonacoEditor } from '@guolao/vue-monaco-editor'
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api'
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker'

// Monaco's default loader fetches its runtime from a CDN.  The portable
// application must remain offline, so Vite owns the worker and the editor
// instance is supplied directly to the wrapper.
;(self as typeof self & { MonacoEnvironment?: { getWorker: () => Worker } }).MonacoEnvironment = {
  getWorker: () => new editorWorker(),
}

loader.config({ monaco })

export { VueMonacoEditor }
