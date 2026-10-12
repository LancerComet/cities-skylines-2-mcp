/** 批次中途停止时保留已执行前缀；身份核验失败的当前步骤明确标为未发送。 */
export function validateBatchSteps(steps) {
  // 先核整批形状，避免 args 等拼写被静默丢弃，也避免坏后缀留下施工前缀。
  if (!Array.isArray(steps)) throw new TypeError('施工批次必须是数组');
  for (const [index, step] of steps.entries()) {
    if (!step || typeof step !== 'object' || Array.isArray(step))
      throw new TypeError(`第${index + 1}步必须是对象`);
    const unknown = Object.keys(step).filter(key => !['tool', 'arguments'].includes(key));
    if (unknown.length) throw new TypeError(`第${index + 1}步未知字段：${unknown.join(', ')}；参数使用 arguments`);
    if (typeof step.tool !== 'string' || !step.tool.startsWith('cs2_'))
      throw new TypeError(`第${index + 1}步缺少有效工具名称`);
    if (step.arguments !== undefined && (!step.arguments || typeof step.arguments !== 'object' || Array.isArray(step.arguments)))
      throw new TypeError(`第${index + 1}步 arguments 必须是对象`);
  }
}
export async function runVerifiedBatch(steps, {checkCity, execute}) {
  validateBatchSteps(steps);
  const results = [];
  let completed = 0;
  for (let index = 0; index < steps.length; index++) {
    const step = steps[index];
    let result;
    try {
      await checkCity();
    } catch (error) {
      result = {isError: true, content: [{type: 'text', text: JSON.stringify({
        error: error instanceof Error ? error.message : String(error), code: error?.code, failurePhase: 'identity_check',
        requestSent: false, mayHaveApplied: false, safeToRetry: false,
      })}]};
    }
    if (!result) {
      try {
        result = await execute(step.tool, step.arguments || {});
      } catch (error) {
        // 执行或落日志期间异常：不能推定施工尚未发生，也不丢弃此前返回的步骤。
        result = {isError: true, content: [{type: 'text', text: JSON.stringify({
          error: error instanceof Error ? error.message : String(error), failurePhase: 'execution', mayHaveApplied: true, safeToRetry: false,
        })}]};
      }
    }
    results.push({tool: step.tool, arguments: step.arguments || {}, result});
    if (result.isError) {
      let outcomeUnknown = true;
      try { outcomeUnknown = JSON.parse(result.content.find(block => block.type === 'text').text).mayHaveApplied !== false; }
      catch { /* 旧版纯文本错误没有结果保证，保留未知状态。 */ }
      return {results, completed, total: steps.length, failedAt: index, tool: step.tool,
        error: result, partialChangesRemain: completed > 0 || outcomeUnknown};
    }
    completed++;
  }
  return {results, completed, total: steps.length};
}
