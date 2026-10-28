const fs = require('node:fs');
const path = require('node:path');

function displayedVersion({ packaged, sourceRoot, appPath }) {
  let version = 'v0.0.0';
  try {
    const root = packaged ? appPath : sourceRoot;
    version = fs.readFileSync(path.join(root, 'VERSION.md'), 'utf8').match(/^\s*- 开发提交版本：\s*(v\d+\.\d+\.\d+)\s*$/m)?.[1] || version;
  } catch (_) {}
  if (!packaged) {
    try {
      const task = fs.readFileSync(path.join(sourceRoot, 'TASK.md'), 'utf8');
      const preview = task.match(/^\s*- 预览显示版本：\s*(v\d+\.\d+\.\d+)\s*$/m)?.[1];
      if (preview && preview !== version) return `${preview}（预览）`;
    } catch (_) {}
  }
  return version;
}

module.exports = { displayedVersion };
