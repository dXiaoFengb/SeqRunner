import { resolve } from 'node:path'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  root: __dirname,
  plugins: [vue()],
  test: {
    environment: 'happy-dom',
    include: ['src/**/*.test.ts'],
    exclude: ['../AUTO-MAS/**', '../AUTO-MAS-docs/**'],
  },
  resolve: { alias: { '@': resolve(__dirname, 'src') } },
})
