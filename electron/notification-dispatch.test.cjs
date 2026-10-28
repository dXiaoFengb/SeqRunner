const test = require('node:test');
const assert = require('node:assert/strict');
const { dispatchNotifications } = require('./notification-dispatch.cjs');

test('failed notification channels never prevent other channels', async () => {
  for (const failed of ['system', 'webhooks', 'qq']) {
    const called = [];
    const channel = (name, value) => async () => { called.push(name); if (failed === name) throw new Error('fixture failure'); return value; };
    const result = await dispatchNotifications({system:channel('system',true),webhooks:channel('webhooks',0),qq:channel('qq',undefined)});
    assert.deepEqual(called.sort(), ['qq','system','webhooks']);
    assert.equal(result.systemFailed, failed === 'system');
    assert.equal(result.webhookFailures, failed === 'webhooks' ? 1 : 0);
    assert.equal(result.qqFailed, failed === 'qq');
  }
});
