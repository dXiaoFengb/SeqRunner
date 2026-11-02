const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const matrix = JSON.parse(fs.readFileSync(path.join(root, 'tools/feature-preview/audit-coverage.json'), 'utf8'));
const errors = [];
if (matrix.version !== 2 || !matrix.areas || typeof matrix.areas !== 'object') errors.push('测试映射格式无效');
for (const [area, tests] of Object.entries(matrix.areas || {})) {
  if (!Array.isArray(tests) || tests.length === 0) { errors.push(`${area} 没有测试文件`); continue; }
  for (const test of tests) {
    const target = path.resolve(root, test);
    if (!target.startsWith(root + path.sep) || !fs.existsSync(target) || !fs.statSync(target).isFile()) errors.push(`${area} 缺少测试: ${test}`);
  }
}
if (errors.length) { for (const error of errors) console.error(error); process.exitCode = 1; }
else console.log(`测试文件映射检查通过：${Object.keys(matrix.areas).length} 个领域；此检查不执行测试或代表人工验收。`);
