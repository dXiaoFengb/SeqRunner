const { readFileSync } = require('node:fs');
const { spawnSync } = require('node:child_process');
const { resolve } = require('node:path');

const root = resolve(__dirname, '..');
const match = readFileSync(resolve(root, 'VERSION.md'), 'utf8').match(/^\s*- 开发提交版本：\s*v(\d+\.\d+\.\d+)\s*$/m);
if (!match) throw new Error('VERSION.md 中未找到格式正确的开发提交版本。');

const suffix = match[1].replace(/\./g, '');
const python = resolve(root, 'backend', '.venv', 'Scripts', 'python.exe');
const args = [
  '-m', 'PyInstaller', '--noconfirm', '--clean',
  '--distpath', `portable-validation-v${suffix}/backend`,
  '--workpath', `build/pyinstaller-v${suffix}`,
  'backend/sequential_runner_backend.spec',
];
const result = spawnSync(python, args, { cwd: root, stdio: 'inherit', shell: false });
if (result.error) throw result.error;
process.exit(result.status ?? 1);
