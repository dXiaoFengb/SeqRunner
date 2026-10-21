const { spawnSync } = require('node:child_process');
const { relative, resolve } = require('node:path');
const {
  assertPreparationTargetsAvailable,
  getValidationPackagePaths,
} = require('./validation-package-paths.cjs');

const root = resolve(__dirname, '..');
const paths = getValidationPackagePaths(root);
assertPreparationTargetsAvailable(paths);
const python = resolve(root, 'backend', '.venv', 'Scripts', 'python.exe');
const args = [
  '-m', 'PyInstaller', '--noconfirm', '--clean',
  '--distpath', relative(root, paths.backendDirectory),
  '--workpath', 'build/pyinstaller-validation-v' + paths.version,
  'backend/sequential_runner_backend.spec',
];
const result = spawnSync(python, args, { cwd: root, stdio: 'inherit', shell: false });
if (result.error) throw result.error;
process.exit(result.status ?? 1);
