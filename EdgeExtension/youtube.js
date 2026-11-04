// YouTube 页面里：播放器在放广告、而且 YouTube 自己的“跳过”按钮已经出来时，告诉玻璃收纳；
// 玻璃收纳说要跳（你在胶囊上点了，或者开了自动跳过）就跳过这条广告。只在跳过按钮已经出来时才动，
// 不屏蔽广告、不改网页。后台标签页、窗口最小化时也照样工作。
(() => {
  const SKIP = [
    '.ytp-skip-ad-button',
    '.ytp-ad-skip-button',
    '.ytp-ad-skip-button-modern',
    '.ytp-ad-skip-button-slot button',
    'button[id^="skip-button"]',
  ].join(',');
  const VERSION = chrome.runtime.getManifest().version;

  let player = null;
  let watching = null;  // 观察播放器 class 变化
  let poll = 0;         // 在放广告时每半秒看一次
  let wasSkippable = false;
  let pressing = false;
  let seekedVideo = '';  // 这条广告已经往后放过一次了（记广告视频的地址），别反复拖
  let lastPress = 0;

  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

  function send(message) {
    return new Promise(resolve => {
      try {
        chrome.runtime.sendMessage(message, answer => {
          void chrome.runtime.lastError; // 玻璃收纳没开、扩展刚更新：当作没回话
          resolve(answer || {});
        });
      } catch (e) {
        resolve({}); // 扩展被关掉或重新加载了
      }
    });
  }

  const adShowing = () => !!player && player.classList.contains('ad-showing');

  // 看得见、不是半透明、不是在倒数：真的能按了才算（YouTube 会先把按钮摆出来，倒数完才让跳）。
  // 不能按时返回原因（写进日志用），能按返回空字符串。
  function notReady(button) {
    if (button.offsetParent === null || button.offsetWidth <= 0) return 'not shown';
    if (button.disabled || button.getAttribute('aria-disabled') === 'true') return 'disabled';
    if (button.getAttribute('aria-hidden') === 'true') return 'aria-hidden';
    // visibility、pointer-events 会往下传，看按钮自己算出来的就行（外层常是 none、按钮自己再改回来）
    const own = getComputedStyle(button);
    if (own.visibility === 'hidden') return 'invisible';
    if (own.pointerEvents === 'none') return 'no pointer';
    let opacity = 1;
    for (let el = button; el && el !== player; el = el.parentElement) opacity *= parseFloat(getComputedStyle(el).opacity) || 0;
    if (opacity < 0.3) return 'faded ' + opacity.toFixed(2);
    const text = (button.textContent || '').trim();
    if (/^\d+$/.test(text)) return 'countdown'; // 按钮上只有数字：还在倒数
    return '';
  }
  const ready = button => notReady(button) === '';

  function skipButton() {
    if (!player) return null;
    for (const button of player.querySelectorAll(SKIP)) if (ready(button)) return button;
    return null;
  }

  // 没跳成时记下当时的样子，玻璃收纳写进日志，方便以后 YouTube 改版时对着修
  function describe(button) {
    const video = mainVideo();
    const html = button ? button.outerHTML.replace(/\s+/g, ' ').slice(0, 220) : 'no button';
    const v = video ? `${video.currentTime.toFixed(1)}/${Number.isFinite(video.duration) ? video.duration.toFixed(1) : '?'} paused=${video.paused}` : 'no video';
    const preview = player && player.querySelector('.ytp-preview-ad, .ytp-ad-preview-container, .ytp-ad-preview-text-modern');
    return `video ${v}; videos ${player ? player.querySelectorAll('video').length : 0}; preview ${preview ? (preview.textContent || '').trim().slice(0, 30) : '-'}; ${html}`;
  }

  // 跳过这条广告。YouTube 现在不认网页脚本“按”的按钮（只认真的鼠标），所以：先按一下按钮，
  // 按了没反应，就把这条广告直接放到结尾（跳过按钮都出来了，效果跟按跳过一样）。
  const mainVideo = () => player && (player.querySelector('video.html5-main-video') || player.querySelector('video'));

  let clickNote = '';  // 真点的过程（写进日志）

  // 请扩展后台借浏览器的输入通道，在这个按钮中间像真的鼠标一样点一下（位置、翻页由后台挂上通道后再量）。
  // 按钮上打一个每次都不一样的随机记号：网页里就算事先放了同名记号的别的东西，也点不到它。最多等 9 秒。
  async function realClick(button) {
    const mark = [...crypto.getRandomValues(new Uint8Array(8))].map(b => b.toString(16).padStart(2, '0')).join('');
    button.setAttribute('data-glassbox-skip', mark);
    try {
      return await Promise.race([send({ type: 'click', mark }), sleep(9000).then(() => ({ error: 'no answer in 9s' }))]);
    } finally {
      button.removeAttribute('data-glassbox-skip');
    }
  }

  async function trustedPress(button) {
    const answer = await realClick(button);
    clickNote = answer.ok ? 'real click ' + (answer.steps || 'ok') : 'real click failed: ' + (answer.error || 'no answer');
    return !!answer.ok;
  }

  const skipped = () => !adShowing() || !skipButton();

  async function press() {
    const button = skipButton();
    if (!button) return 'gone';
    lastPress = Date.now();
    clickNote = '';
    const trusted = await trustedPress(button);
    for (let i = 0; i < 4 && !skipped(); i++) await sleep(150);
    if (skipped()) return keepPlaying(trusted ? 'trusted' : 'click');
    // 真点没成（比如开着开发者工具，借不到输入通道）：退回老办法
    button.click();
    for (let i = 0; i < 2 && adShowing(); i++) await sleep(150);
    if (!adShowing()) return keepPlaying('click');
    const video = mainVideo();
    const id = video ? video.currentSrc || video.src || '' : '';
    // 往后放只做一次：YouTube 不让拖的时候反复拖会一顿一顿的
    // 超过 5 分钟的不拖：那可能是广告插在歌里一起放的整段视频，拖到底就把歌也跳过了
    if (video && id !== seekedVideo && Number.isFinite(video.duration) && video.duration > 0 && video.duration <= 300) {
      seekedVideo = id;
      // 停在最后一点点再让它自己放完：直接跳到最末尾，YouTube 有时收不到“广告放完了”，就卡在那
      video.currentTime = Math.max(0, video.duration - 0.15);
      if (video.paused) video.play().catch(() => {});
      for (let i = 0; i < 10 && adShowing(); i++) await sleep(150);
      if (!adShowing()) return keepPlaying('end');
    }
    return 'failed';
  }

  // 广告没了之后 YouTube 偶尔不接着放（后台标签页更常见），要回 Edge 点一下才动：帮它接上。
  // 歌在放的就接着放；广告是在一首歌放完后插的，就去下一首（YouTube 自己切了的话什么都不做）。
  async function keepPlaying(result) {
    const page = location.href;
    for (let i = 0; i < 12; i++) {
      await sleep(250);
      if (adShowing() || location.href !== page) return result; // 又来一条广告 / 已经换到下一首
      const video = mainVideo();
      if (!video) return result;
      if (!video.paused && video.readyState >= 2) return result; // 正常在放
      if (i < 4) continue; // 给 YouTube 一秒自己接上
      if (video.ended || (Number.isFinite(video.duration) && video.duration > 0 && video.duration - video.currentTime < 1)) {
        const next = player.querySelector('.ytp-next-button');
        if (next && next.offsetParent !== null) {
          next.click();
          return result + '+next';
        }
        return result;
      }
      if (video.paused) {
        video.play().catch(() => {});
        return result + '+resume';
      }
    }
    return result;
  }

  let adSince = 0;        // 这条广告什么时候出来的
  let waitingNoted = false;

  async function check() {
    if (pressing) return;
    const ad = adShowing();
    const skippable = ad && !!skipButton();
    if (!ad && !wasSkippable) {
      adSince = 0;
      stopPolling();
      return;
    }
    if (ad && !adSince) { adSince = Date.now(); waitingNoted = false; }
    // 跳过按钮在，但过了 12 秒还算“不能按”：把原因记进日志一次，方便对着修（不影响别的）
    if (ad && !skippable && !waitingNoted && Date.now() - adSince > 12000) {
      const buttons = player ? [...player.querySelectorAll(SKIP)] : [];
      if (buttons.length) {
        waitingNoted = true;
        send({ type: 'ad', ad, skippable: false, result: 'waiting', diag: buttons.map(notReady).join('/') + '; ' + describe(buttons[0]) });
      }
    }
    if (!skippable && !wasSkippable) return; // 广告还在倒数：不用说话
    wasSkippable = skippable;
    const answer = await send({ type: 'ad', ad, skippable });
    if (!skippable || !(answer.skip || answer.auto)) return;
    if (!answer.skip && Date.now() - lastPress < 1500) return; // 自动跳的：上一下没成，隔一会儿再试
    pressing = true;
    let result;
    try { result = await press(); } finally { pressing = false; }
    // 告诉玻璃收纳按的结果（它会记在日志里；没跳成的话胶囊上的“跳过”会再出来）
    const still = adShowing() && !!skipButton();
    wasSkippable = still;
    const diag = clickNote + (result === 'failed' ? '; ' + describe(skipButton()) : '');
    send({ type: 'ad', ad: adShowing(), skippable: still, result, diag });
  }

  function startPolling() {
    if (!poll) poll = setInterval(check, 500);
    check();
  }

  function stopPolling() {
    if (poll) clearInterval(poll);
    poll = 0;
  }

  // ---------- “仍在观看吗？” ----------
  // YouTube 放久了会把视频停下、弹一个“视频已暂停，要继续观看吗？”。只在视频刚停下时看一眼有没有这个框，
  // 有就问玻璃收纳（开着“自动继续”才点）。只点这个框自己的“继续/是”按钮，别的对话框不碰。
  const STILL_TEXT = /continue watching|still watching|still there|are you there|继续观看|繼續觀看|仍在观看|还在看|還在看|視聴を続け|続けて視聴|계속 시청/i;
  let stillBusy = false;

  const shown = el => !!el && el.isConnected && el.offsetParent !== null && el.offsetWidth > 0;

  function stillDialog() {
    // YouTube Music 有专门的“你还在吗”
    const music = document.querySelector('ytmusic-you-there-renderer');
    if (shown(music)) return { box: music, button: [...music.querySelectorAll('button')].find(shown) };
    for (const box of document.querySelectorAll('yt-confirm-dialog-renderer')) {
      if (!shown(box) || !STILL_TEXT.test(box.textContent || '')) continue;
      // 一个个按顺序找：一次查几个的话会先拿到外面那层（文档里它在前面）
      const confirm = ['#confirm-button button', '#confirm-button tp-yt-paper-button', '#confirm-button']
        .map(selector => box.querySelector(selector)).find(shown);
      if (shown(confirm)) return { box, button: confirm };
    }
    return null;
  }

  async function checkStill() {
    if (stillBusy) return;
    const video = mainVideo();
    if (!video || !video.paused || adShowing()) return;
    const found = stillDialog();
    if (!found || !found.button) return;
    stillBusy = true;
    try {
      const answer = await send({ type: 'still' });
      if (!answer.continue) return; // 没开“自动继续”
      found.button.click();
      await sleep(400);
      let how = 'click';
      if (shown(found.box) && shown(found.button)) {
        // 网页脚本按的不认：像真的鼠标一样点
        const real = await realClick(found.button);
        how = real.ok ? 'real click ' + (real.steps || 'ok') : 'real click failed: ' + (real.error || 'no answer');
        await sleep(300);
      }
      const v = mainVideo();
      if (v && v.paused && !shown(found.box)) {
        v.play().catch(() => {});
        how += '+play';
      }
      send({ type: 'still', result: shown(found.box) ? 'failed' : 'continued', diag: how });
    } finally {
      stillBusy = false;
    }
  }

  // 视频停下时看一眼（框是停下之后才弹的，等它一会儿）
  function onPause() {
    setTimeout(checkStill, 600);
    setTimeout(checkStill, 2000);
  }

  function attach(found) {
    if (found === player) return;
    if (watching) watching.disconnect();
    if (player) player.removeEventListener('pause', onPause, true);
    player = found;
    player.addEventListener('pause', onPause, true); // video 的 pause 不冒泡，用捕获在播放器上接住
    watching = new MutationObserver(() => {
      if (player.classList.contains('ad-showing') || wasSkippable) startPolling();
    });
    watching.observe(player, { attributes: true, attributeFilter: ['class'] });
    if (player.classList.contains('ad-showing')) startPolling();
  }

  // 播放器是 YouTube 自己后来才插进页面的，换视频时也可能换掉：等它出现
  function findPlayer() {
    const found = document.getElementById('movie_player');
    if (found) attach(found);
  }

  new MutationObserver(() => {
    if (!player || !player.isConnected) findPlayer(); // 页面一直在变：已经找到的播放器还在就不用再找
  }).observe(document.documentElement, { childList: true, subtree: true });
  findPlayer();
  send({ type: 'hello', version: VERSION }); // 让玻璃收纳知道扩展装好了、是哪一版
})();
