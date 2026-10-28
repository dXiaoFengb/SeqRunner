async function dispatchNotifications({ system, webhooks, qq }) {
  const [systemResult, hookResult, qqResult] = await Promise.allSettled([Promise.resolve().then(system), Promise.resolve().then(webhooks), Promise.resolve().then(qq)]);
  return {
    shown: systemResult.status === 'fulfilled' && systemResult.value === true,
    systemFailed: systemResult.status === 'rejected',
    webhookFailures: hookResult.status === 'fulfilled' ? hookResult.value : 1,
    qqFailed: qqResult.status === 'rejected',
  };
}
module.exports = { dispatchNotifications };
