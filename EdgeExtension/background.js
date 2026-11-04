// 在 YouTube 页面和本机的玻璃收纳之间传话。网页里的脚本不能直接连本机，所以由这里转一下。
// 玻璃收纳没开着时请求会失败，什么都不做。
const GLASSBOX = 'http://127.0.0.1:48613/';

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  // 只听自己装在 YouTube 页面里的那段脚本（别的扩展、网页发不进来，这里再确认一遍）
  if (!message || !sender || sender.id !== chrome.runtime.id || !sender.tab || !/^https:\/\/(www|m|music)\.youtube\.com\//.test(sender.url || '')) return;
  if (message.type === 'click') {
    // YouTube 只认真的鼠标点的“跳过”：借浏览器自己的输入通道在按钮上点一下（跟手点一样），
    // 点完马上放开。点的时候 Edge 顶上会闪一下“正在调试此浏览器”的提示条。
    trustedClick(sender.tab && sender.tab.id, message.mark).then(
      steps => reply({ ok: true, steps }),
      error => reply({ ok: false, error: String((error && error.message) || error) }),
    );
    return true;
  }
  if (message.type !== 'ad' && message.type !== 'hello' && message.type !== 'still') return;
  fetch(GLASSBOX + message.type, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      tab: (sender.tab && sender.tab.id) || 0,
      skippable: !!message.skippable,
      ad: !!message.ad,
      version: message.version || '',
      result: message.result || '',
      diag: (message.diag || '').slice(0, 500),
    }),
  })
    .then(response => (response.ok ? response.json() : {}))
    .then(answer => reply(answer || {}), () => reply({}));
  return true; // 异步回话
});

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

// 每一步最多等一会儿：浏览器没回话（比如标签页在后台）就当这一步失败，绝不卡住
function within(ms, step, promise) {
  return Promise.race([promise, sleep(ms).then(() => { throw new Error(step + ' timeout'); })]);
}

// 按钮位置在挂上输入通道之后才量：Edge 顶上弹出提示条会把网页往下挤，先量的位置会点偏。
// 按钮不在可视范围（页面往下翻了）就先翻过去，点完翻回来。按钮由网页那边打上随机的 data-glassbox-skip 记号。
const MEASURE = mark => `(() => {
  const b = document.querySelector('[data-glassbox-skip="${mark}"]');
  if (!b) return null;
  const sx = scrollX, sy = scrollY;
  let r = b.getBoundingClientRect();
  if (r.bottom <= 0 || r.top >= innerHeight || r.right <= 0 || r.left >= innerWidth) {
    b.scrollIntoView({ block: 'center', inline: 'center' });
    r = b.getBoundingClientRect();
  }
  return { x: r.left + r.width / 2, y: r.top + r.height / 2, sx, sy, moved: scrollX !== sx || scrollY !== sy, hidden: document.hidden };
})()`;

async function trustedClick(tabId, mark) {
  if (!tabId) throw new Error('no tab');
  if (typeof mark !== 'string' || !/^[0-9a-f]{16}$/.test(mark)) throw new Error('bad mark'); // 只认网页脚本生成的那种记号，拼进表达式前先查
  const target = { tabId };
  const send = (method, params, ms = 1500) => within(ms, method, chrome.debugger.sendCommand(target, method, params));
  const attaching = chrome.debugger.attach(target, '1.3');
  try {
    await within(2000, 'attach', attaching);
  } catch (error) {
    attaching.then(() => chrome.debugger.detach(target)).catch(() => {}); // 晚到的也放开，别让提示条一直挂着
    throw error;
  }
  const steps = [];
  try {
    await sleep(120); // 等提示条挤完
    // 后台标签页：让网页以为自己在前台、有焦点，不然浏览器可能不处理点击
    await send('Emulation.setFocusEmulationEnabled', { enabled: true }).then(() => steps.push('focus'), () => {});
    await send('Page.setWebLifecycleState', { state: 'active' }).then(() => steps.push('active'), () => {});
    const measured = await send('Runtime.evaluate', { expression: MEASURE(mark), returnByValue: true });
    const at = measured && measured.result && measured.result.value;
    if (!at) throw new Error('button gone');
    if (at.hidden) steps.push('hidden');
    const mouse = params => send('Input.dispatchMouseEvent', { x: at.x, y: at.y, ...params }, 1200);
    try {
      await mouse({ type: 'mouseMoved' });
      await mouse({ type: 'mousePressed', button: 'left', buttons: 1, clickCount: 1 });
      await mouse({ type: 'mouseReleased', button: 'left', buttons: 0, clickCount: 1 });
      steps.push('clicked');
    } catch (error) {
      // 鼠标点不进去（后台标签页浏览器不处理鼠标）：以“你按的”名义按一下按钮
      steps.push('mouse ' + String((error && error.message) || error));
      await send('Runtime.evaluate', {
        expression: `(() => { const b = document.querySelector('[data-glassbox-skip="${mark}"]'); if (b) b.click(); return !!b; })()`,
        returnByValue: true,
        userGesture: true,
      }).then(() => steps.push('gesture'), () => {});
    }
    // 广告停在第 0 秒没加载（后台标签页常见）：以“你按的”名义让它放起来
    await sleep(250);
    await send('Runtime.evaluate', {
      expression: `(() => { const v = document.querySelector('#movie_player video'); if (v && v.paused) { v.play().catch(() => {}); return true; } return false; })()`,
      returnByValue: true,
      userGesture: true,
    }).then(r => { if (r && r.result && r.result.value) steps.push('play'); }, () => {});
    if (at.moved) await send('Runtime.evaluate', { expression: `scrollTo(${at.sx}, ${at.sy})` }).catch(() => {});
  } finally {
    await send('Emulation.setFocusEmulationEnabled', { enabled: false }, 500).catch(() => {});
    await within(1000, 'detach', chrome.debugger.detach(target)).catch(() => {});
  }
  return steps.join(',');
}
