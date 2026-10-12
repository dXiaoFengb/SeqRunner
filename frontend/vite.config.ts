import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { resolve } from 'node:path'

export default defineConfig({
  root: __dirname,
  base: './',
  plugins: [vue()],
  build: { outDir: resolve(__dirname, '../frontend-dist'), emptyOutDir: true },
})
