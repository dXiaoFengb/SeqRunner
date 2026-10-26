const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const matrixPath = path.join(root, 'tools', 'feature-preview', 'audit-coverage.json');
const matrix = JSON.parse(fs.readFileSync(matrixPath, 'utf8'));
const errors = [];

if (matrix.version !== 1 || !matrix.areas || typeof matrix.areas !== 'object') errors.push('审核覆盖矩阵必须使用 version=1 且包含 areas 对象。');
for (const [area, definition] of Object.entries(matrix.areas || {})) {
  for (const relative of [...(definition.tests || []), ...(definition.scenarios || [])]) {
    if (!fs.existsSync(path.join(root, relative))) errors.push(`领域“${area}”引用了不存在的文件：${relative}`);
  }
  if (typeof definition.preview !== 'boolean' || typeof definition.highRisk !== 'boolean') errors.push(`领域“${area}”必须明确 preview 与 highRisk。`);
  if (!Array.isArray(definition.manualAcceptance) || !definition.manualAcceptance.length) errors.push(`领域“${area}”必须列出受影响的人工验收编号。`);
}

for (const relative of ['docs/自动审核员提示词.md', 'docs/自动审核验证模板.md', 'docs/manual-acceptance.md', 'tools/ui-acceptance/README.md']) {
  const contents = fs.readFileSync(path.join(root, relative), 'utf8');
  for (const forbidden of matrix.forbiddenCurrentReferences || []) {
    if (contents.includes(forbidden)) errors.push(`当前审核文档仍引用已删除场景：${relative} -> ${forbidden}`);
  }
}

if (errors.length) {
  for (const error of errors) console.error(`ERROR: ${error}`);
  process.exitCode = 1;
} else {
  console.log(`审核覆盖矩阵通过：${Object.keys(matrix.areas).length} 个领域，所有引用均存在。`);
}
