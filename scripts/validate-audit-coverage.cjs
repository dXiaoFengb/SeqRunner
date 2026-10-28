const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const matrixPath = path.join(root, 'tools', 'feature-preview', 'audit-coverage.json');
const matrix = JSON.parse(fs.readFileSync(matrixPath, 'utf8'));
const errors = [];
const batchStatePath = path.join(root, 'docs', '审核批次状态.md');

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

if (!fs.existsSync(batchStatePath)) {
  errors.push('缺少跨任务审核累计状态：docs/审核批次状态.md');
} else {
  const state = fs.readFileSync(batchStatePath, 'utf8');
  const count = Number(state.match(/已累计功能改动：\s*(\d+)/)?.[1]);
  const next = Number(state.match(/下次 EXE 提示阈值：\s*(\d+)/)?.[1]);
  const alerted = state.match(/已提示阈值：\s*([^\r\n]+)/)?.[1]?.trim();
  if (!Number.isInteger(count) || count < 0) errors.push('审核累计状态缺少非负整数“已累计功能改动”。');
  const expectedNext = Number.isInteger(count) && count >= 0 ? (Math.floor(count / 5) + 1) * 5 : null;
  if (next !== expectedNext) errors.push('审核累计状态的下次 EXE 提示阈值必须是当前累计后的第一个 5 的倍数。');
  const expectedAlerted = Number.isInteger(count) && count >= 0
    ? Array.from({ length: Math.floor(count / 5) }, (_, index) => (index + 1) * 5)
    : [];
  const alertedValues = alerted === '无' ? [] : (alerted && /^\d+(?:、\d+)*$/.test(alerted) ? alerted.split('、').map(Number) : null);
  if (!alertedValues || alertedValues.some((value, index) => value !== expectedAlerted[index]) || alertedValues.length !== expectedAlerted.length) {
    errors.push('审核累计状态的已提示阈值必须恰好记录已达到的 5、10、15…项，且每项仅一次。');
  }
  const detailRows = state.split(/\r?\n/).filter((line) => /^\|\s*\d+\s*\|/.test(line));
  const detailNumbers = detailRows.map((line) => Number(line.match(/^\|\s*(\d+)\s*\|/)?.[1]));
  if (Number.isInteger(count) && (detailNumbers.length !== count || detailNumbers.some((value, index) => value !== index + 1))) {
    errors.push('审核累计明细必须与累计数一致，并按 1…N 连续编号。');
  }
  const previewEvidencePaths = [...state.matchAll(/`(产物\/预览\/[^`]+\/运行\/[^`]+)\/?`/g)]
    .map((match) => match[1].replace(/[\\/]+$/, ''));
  if (Number.isInteger(count) && count > 0 && !previewEvidencePaths.length) errors.push('已累计改动必须引用至少一份预览证据目录。');
  for (const relative of previewEvidencePaths) {
    const evidenceDirectory = path.resolve(root, relative);
    const previewRoot = `${path.join(root, '产物', '预览')}${path.sep}`;
    if (!evidenceDirectory.startsWith(previewRoot) || !fs.existsSync(evidenceDirectory)) {
      errors.push(`审核累计引用的预览证据目录不存在或越出预览根：${relative}`);
      continue;
    }
    for (const file of ['结果.json', '在线验收.json', '进程收尾.json', '源码状态.json', '在线验收截图.png']) {
      if (!fs.existsSync(path.join(evidenceDirectory, file))) errors.push(`预览证据缺少 ${file}：${relative}`);
    }
  }
}

for (const relative of ['tools/ui-acceptance/run-feature-preview.cjs', 'tools/feature-preview/Run-FeaturePreview.ps1']) {
  if (!fs.existsSync(path.join(root, relative))) errors.push(`预览在线验收缺少工具：${relative}`);
}
const previewAcceptancePackage = JSON.parse(fs.readFileSync(path.join(root, 'tools', 'ui-acceptance', 'package.json'), 'utf8'));
if (previewAcceptancePackage.devDependencies?.playwright !== '1.64.1' || previewAcceptancePackage.devDependencies?.['@playwright/cli']) {
  errors.push('预览在线验收必须使用固定的 playwright@1.64.1，且不得保留 alpha @playwright/cli。');
}

if (errors.length) {
  for (const error of errors) console.error(`ERROR: ${error}`);
  process.exitCode = 1;
} else {
  console.log(`审核覆盖矩阵通过：${Object.keys(matrix.areas).length} 个领域，所有引用均存在。`);
}
