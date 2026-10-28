const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { displayedVersion } = require('./version.cjs');

test('preview version is separate from packaged and submitted version', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-version-'));
  try {
    fs.writeFileSync(path.join(root, 'VERSION.md'), '- 开发提交版本：v0.16.1\n');
    fs.writeFileSync(path.join(root, 'TASK.md'), '- 预览显示版本：v0.17.0\n');
    assert.equal(displayedVersion({ packaged: false, sourceRoot: root }), 'v0.17.0（预览）');
    assert.equal(displayedVersion({ packaged: true, sourceRoot: root, appPath: root }), 'v0.16.1');
    fs.writeFileSync(path.join(root, 'VERSION.md'), '- 开发提交版本：v0.17.0\n');
    assert.equal(displayedVersion({ packaged: false, sourceRoot: root }), 'v0.17.0');
    fs.writeFileSync(path.join(root, 'VERSION.md'), '- 开发提交版本：v0.16.1\n');
    fs.writeFileSync(path.join(root, 'TASK.md'), '- 预览显示版本：无效\n');
    assert.equal(displayedVersion({ packaged: false, sourceRoot: root }), 'v0.16.1');
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
