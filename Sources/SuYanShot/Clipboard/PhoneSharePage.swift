/// The single-page web app the phone opens. Everything it shows is built with textContent (never innerHTML)
/// because clipboard text is arbitrary and may contain markup. The session lives in an HttpOnly cookie set by
/// the login page, so URLs carry no secret.
enum PhoneSharePage {
    static let html = #"""
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="拾光">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="manifest" href="/manifest.webmanifest">
<title>拾光剪貼簿</title>
<style>
  :root {
    --bg: #f2f2f7; --card: #ffffff; --text: #1c1c1e; --muted: #8e8e93; --line: rgba(60,60,67,.12);
    --accent: #0a84ff; --chip: rgba(120,120,128,.12); --toast: rgba(28,28,30,.92);
  }
  @media (prefers-color-scheme: dark) {
    :root { --bg: #000; --card: #1c1c1e; --text: #f2f2f7; --muted: #98989f; --line: rgba(84,84,88,.6); --chip: rgba(120,120,128,.24); }
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body { margin: 0; background: var(--bg); color: var(--text); font: 16px -apple-system, BlinkMacSystemFont, "PingFang TC", sans-serif; }
  header { position: sticky; top: 0; z-index: 5; background: color-mix(in srgb, var(--bg) 88%, transparent);
           backdrop-filter: blur(20px); -webkit-backdrop-filter: blur(20px); padding: calc(env(safe-area-inset-top) + 10px) 16px 8px; }
  h1 { font-size: 22px; margin: 0 0 10px; }
  #search { width: 100%; font-size: 16px; padding: 9px 12px; border-radius: 10px; border: 0; background: var(--chip); color: var(--text); outline: none; }
  #chips { display: flex; gap: 8px; overflow-x: auto; padding: 10px 0 2px; scrollbar-width: none; }
  #chips::-webkit-scrollbar { display: none; }
  .chip { flex: none; border: 0; border-radius: 999px; padding: 6px 12px; font-size: 14px; background: var(--chip); color: var(--text); display: flex; align-items: center; gap: 6px; }
  .chip.on { background: var(--accent); color: #fff; }
  .dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; flex: none; }
  main { padding: 8px 16px calc(env(safe-area-inset-bottom) + 150px); display: grid; gap: 10px; }
  .card { background: var(--card); border-radius: 14px; padding: 12px 14px; display: grid; gap: 8px; }
  .meta { display: flex; align-items: center; gap: 8px; font-size: 12px; color: var(--muted); }
  .meta .kind { font-weight: 600; color: var(--text); }
  .meta .grow { flex: 1; }
  .star { color: #ffcc00; }
  .body { white-space: pre-wrap; word-break: break-word; font-size: 15px; line-height: 1.4; max-height: 9.8em; overflow: hidden; }
  .body.open { max-height: none; }
  .thumb { width: 100%; max-height: 260px; object-fit: contain; border-radius: 8px; background: var(--chip); }
  .files { font-size: 14px; display: grid; gap: 2px; }
  .tags { display: flex; gap: 10px; flex-wrap: wrap; font-size: 12px; color: var(--muted); }
  .tags span { display: inline-flex; align-items: center; gap: 4px; }
  .actions { display: flex; gap: 8px; }
  .actions button { flex: 1; border: 0; border-radius: 9px; padding: 8px; font-size: 14px; background: var(--chip); color: var(--accent); font-weight: 600; }
  .empty { text-align: center; color: var(--muted); padding: 60px 20px; }
  footer { position: fixed; left: 0; right: 0; bottom: 0; z-index: 5; padding: 10px 16px calc(env(safe-area-inset-bottom) + 10px);
           background: color-mix(in srgb, var(--bg) 90%, transparent); backdrop-filter: blur(20px); -webkit-backdrop-filter: blur(20px); border-top: 1px solid var(--line); }
  #attachments { display: flex; gap: 10px; overflow-x: auto; max-height: 110px; margin-bottom: 8px; }
  #attachments:empty { display: none; }
  .attachment { position: relative; flex: none; width: 96px; height: 96px; border-radius: 10px; background: var(--chip); }
  .attachment img { width: 100%; height: 100%; object-fit: contain; border-radius: 10px; }
  .attachment button { position: absolute; right: 2px; top: 2px; width: 28px; height: 28px; border: 0; border-radius: 50%; background: var(--toast); color: #fff; font-size: 18px; }
  button:disabled { opacity: .5; }
  #composer { display: flex; gap: 8px; align-items: flex-end; }
  #draft { flex: 1; min-width: 0; font: 16px -apple-system, sans-serif; border-radius: 10px; border: 0; padding: 9px 12px; background: var(--card); color: var(--text); resize: none; max-height: 120px; outline: none; }
  .round { border: 0; border-radius: 10px; padding: 10px 12px; font-size: 15px; font-weight: 600; }
  #sendText { background: var(--accent); color: #fff; }
  #pickImage { background: var(--chip); color: var(--accent); }
  #toast { position: fixed; left: 50%; bottom: calc(env(safe-area-inset-bottom) + 90px); transform: translateX(-50%); background: var(--toast); color: #fff;
           padding: 10px 16px; border-radius: 12px; font-size: 14px; opacity: 0; transition: opacity .2s; pointer-events: none; z-index: 20; white-space: nowrap; }
  #toast.show { opacity: 1; }
  #viewer { position: fixed; inset: 0; background: rgba(0,0,0,.92); display: none; align-items: center; justify-content: center; z-index: 30; flex-direction: column; gap: 14px; }
  #viewer.show { display: flex; }
  #viewer img { max-width: 100%; max-height: 80vh; -webkit-touch-callout: default; }
  #viewer p { color: #ddd; font-size: 13px; margin: 0; }
</style>
</head>
<body>
<header>
  <div style="display:flex;align-items:center;justify-content:space-between"><h1>剪貼簿</h1><button id="logout" class="chip">登出</button></div>
  <input id="search" type="search" placeholder="搜尋" autocomplete="off">
  <div id="chips"></div>
</header>
<main id="list"></main>
<footer>
  <div id="attachments" aria-label="待傳送圖片"></div>
  <div id="composer">
    <textarea id="draft" rows="1" placeholder="輸入文字或貼上圖片"></textarea>
    <button id="pickImage" class="round" aria-label="傳照片">照片</button>
    <button id="sendText" class="round">傳送</button>
  </div>
  <input id="file" type="file" accept="image/*" multiple hidden>
</footer>
<div id="toast"></div>
<div id="viewer"><img alt=""><p>長按圖片可以儲存或複製・點一下關閉</p></div>
<script>
const state = { items: [], tags: [], version: '', filter: 'all', query: '', expired: false };
const $ = (id) => document.getElementById(id);

// The session cookie goes along automatically; POSTs also carry a header cross-site forms can't add.
async function api(path, options = {}) {
  const headers = Object.assign({ 'X-Requested-With': 'glean' }, options.headers || {});
  const response = await fetch(path, Object.assign({}, options, { headers, credentials: 'same-origin' }));
  if (response.status === 401) { location.reload(); throw new Error('signed out'); }
  return response;
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

let toastTimer;
function toast(message) {
  const t = $('toast');
  t.textContent = message;
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), 1600);
}

async function load() {
  if (state.expired) return;
  try {
    const response = await api('/api/items?v=' + encodeURIComponent(state.version));
    const data = await response.json();
    if (!data.unchanged) { state.items = data.items; state.tags = data.tags; state.version = data.version; render(); }
  } catch (e) { /* Mac asleep or off the network; try again on the next tick */ }
}


const builtIn = [['all', '全部'], ['favorites', '常用'], ['text', '文字'], ['image', '圖片'], ['file', '檔案']];
const kindNames = { text: '文字', richText: '格式文字', image: '圖片', file: '檔案' };

function matches(item) {
  const f = state.filter;
  if (f === 'favorites' && !item.pinned) return false;
  if (f === 'text' && !(item.kind === 'text' || item.kind === 'richText')) return false;
  if (f === 'image' && item.kind !== 'image') return false;
  if (f === 'file' && item.kind !== 'file') return false;
  if (f.startsWith('tag:') && !item.tags.includes(f.slice(4))) return false;
  const q = state.query.trim().toLowerCase();
  if (!q) return true;
  const tagNames = item.tags.map(id => (state.tags.find(t => t.id === id) || {}).name || '').join(' ');
  const hay = [item.text || '', (item.files || []).join(' '), tagNames, kindNames[item.kind]].join(' ').toLowerCase();
  return hay.includes(q);
}

const relative = new Intl.RelativeTimeFormat('zh-TW', { numeric: 'auto', style: 'short' });
function ago(ms) {
  const seconds = Math.round((ms - Date.now()) / 1000);
  const steps = [[60, 'second'], [3600, 'minute', 60], [86400, 'hour', 3600], [604800, 'day', 86400], [Infinity, 'week', 604800]];
  for (const [limit, unit, size] of steps) {
    if (Math.abs(seconds) < limit) return relative.format(Math.round(seconds / (size || 1)), unit);
  }
  return '';
}

function renderChips() {
  const chips = $('chips');
  const all = builtIn.map(([id, name]) => ({ id, name })).concat(state.tags.map(t => ({ id: 'tag:' + t.id, name: t.name, color: t.color })));
  if (!all.some(c => c.id === state.filter)) state.filter = 'all';
  chips.replaceChildren(...all.map(c => {
    const b = el('button', 'chip' + (c.id === state.filter ? ' on' : ''));
    if (c.color) { const d = el('span', 'dot'); d.style.background = c.color; b.append(d); }
    b.append(document.createTextNode(c.name));
    b.onclick = () => { state.filter = c.id; render(); };
    return b;
  }));
}

function render() {
  renderChips();
  const list = $('list');
  const items = state.items.filter(matches);
  if (!items.length) {
    list.replaceChildren(el('div', 'empty', state.items.length ? '沒有符合的項目' : '還沒有紀錄。在 Mac 上複製東西，或從下面傳文字、圖片過去。'));
    return;
  }
  list.replaceChildren(...items.map(card));
}

function card(item) {
  const c = el('div', 'card');
  const meta = el('div', 'meta');
  meta.append(el('span', 'kind', kindNames[item.kind] || ''));
  if (item.source) meta.append(el('span', '', item.source));
  meta.append(el('span', 'grow'));
  if (item.pinned) meta.append(el('span', 'star', '★'));
  meta.append(el('span', '', ago(item.createdAt)));
  c.append(meta);

  if (item.kind === 'image') {
    const img = el('img', 'thumb');
    img.loading = 'lazy';
    img.src = '/api/items/' + item.id + '/image?thumb=1';
    img.onclick = () => openViewer(item);
    c.append(img);
  } else if (item.kind === 'file') {
    const files = el('div', 'files');
    (item.files || []).forEach(name => files.append(el('div', '', '📄 ' + name)));
    c.append(files);
  } else {
    const body = el('div', 'body', item.text || '');
    body.onclick = () => body.classList.toggle('open');
    c.append(body);
  }

  const tags = item.tags.map(id => state.tags.find(t => t.id === id)).filter(Boolean);
  if (tags.length) {
    const row = el('div', 'tags');
    tags.forEach(t => { const s = el('span'); const d = el('span', 'dot'); d.style.background = t.color; s.append(d, document.createTextNode(t.name)); row.append(s); });
    c.append(row);
  }

  const actions = el('div', 'actions');
  if (item.kind === 'text' || item.kind === 'richText') {
    const copy = el('button', '', '複製');
    copy.onclick = () => copyText(item);
    actions.append(copy);
  } else if (item.kind === 'image') {
    const open = el('button', '', '查看');
    open.onclick = () => openViewer(item);
    actions.append(open);
  }
  const toMac = el('button', '', '放到 Mac 剪貼簿');
  toMac.onclick = async () => {
    const r = await api('/api/items/' + item.id + '/to-mac', { method: 'POST' });
    toast(r.ok ? '已放到 Mac 剪貼簿' : '沒有成功，請再試一次');
  };
  actions.append(toMac);
  c.append(actions);
  return c;
}

// Plain http pages can't use navigator.clipboard, so fall back to a selected textarea + execCommand.
function copyText(item) {
  const text = item.text || '';
  let ok = false;
  if (navigator.clipboard && window.isSecureContext) {
    navigator.clipboard.writeText(text).then(() => toast('已複製'), () => toast('複製失敗'));
    return;
  }
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.setAttribute('readonly', '');
  ta.style.position = 'fixed';
  ta.style.top = '-1000px';
  document.body.append(ta);
  ta.select();
  ta.setSelectionRange(0, text.length);
  try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
  ta.remove();
  toast(ok ? (item.truncated ? '已複製（太長，只複製前 10 萬字）' : '已複製') : '複製失敗，請長按文字自行複製');
}

function openViewer(item) {
  const viewer = $('viewer');
  viewer.querySelector('img').src = '/api/items/' + item.id + '/image';
  viewer.classList.add('show');
}
$('viewer').onclick = (e) => { if (e.target.tagName !== 'IMG') $('viewer').classList.remove('show'); };
$('viewer').querySelector('img').onclick = () => $('viewer').classList.remove('show');

$('search').oninput = (e) => { state.query = e.target.value; render(); };

const draft = $('draft');
const pendingImages = [];
const maxImageBytes = 20 * 1024 * 1024;
let sending = false;

function fitComposer() {
  draft.style.height = 'auto';
  draft.style.height = Math.min(draft.scrollHeight, 120) + 'px';
  $('list').style.paddingBottom = (document.querySelector('footer').getBoundingClientRect().height + 16) + 'px';
}
draft.oninput = fitComposer;

function renderAttachments() {
  $('attachments').replaceChildren(...pendingImages.map(entry => {
    const preview = el('div', 'attachment');
    const img = el('img');
    img.src = entry.url;
    img.alt = entry.file.name || '待傳送圖片';
    const remove = el('button', '', '×');
    remove.setAttribute('aria-label', '移除圖片');
    remove.disabled = sending;
    remove.onclick = () => {
      if (sending) return;
      const index = pendingImages.indexOf(entry);
      if (index < 0) return;
      pendingImages.splice(index, 1);
      URL.revokeObjectURL(entry.url);
      renderAttachments();
    };
    preview.append(img, remove);
    return preview;
  }));
  fitComposer();
}

function addImages(files) {
  if (sending) { toast('正在傳送，請稍候'); return; }
  for (const file of files) {
    if (!file || !file.type.startsWith('image/')) continue;
    if (!file.size) { toast('圖片沒有內容，請重新複製'); continue; }
    if (file.size > maxImageBytes) { toast('單張圖片不能超過 20 MB'); continue; }
    if (pendingImages.length >= 10) { toast('一次最多傳送 10 張圖片'); break; }
    pendingImages.push({ file, url: URL.createObjectURL(file) });
  }
  renderAttachments();
}

// A user-initiated paste works without the secure-context-only Clipboard API.
// Read image files only: never load arbitrary image URLs from pasted HTML.
draft.addEventListener('paste', event => {
  const data = event.clipboardData;
  if (!data) return;
  const files = Array.from(data.items || [])
    .filter(item => item.kind === 'file' && item.type.startsWith('image/'))
    .map(item => item.getAsFile()).filter(Boolean);
  if (!files.length) files.push(...Array.from(data.files || []).filter(file => file.type.startsWith('image/')));
  if (!files.length) return; // Plain-text paste keeps the browser's default behavior.
  event.preventDefault();
  if (sending) { toast('正在傳送，請稍候'); return; }
  addImages(files);
  const text = data.getData('text/plain');
  if (text) {
    draft.setRangeText(text, draft.selectionStart, draft.selectionEnd, 'end');
    fitComposer();
  }
});

$('pickImage').onclick = () => $('file').click();
$('file').onchange = event => {
  addImages(Array.from(event.target.files || []));
  event.target.value = '';
};

$('sendText').onclick = async () => {
  if (sending) return;
  if (!draft.value.trim() && !pendingImages.length) { toast('先輸入文字或貼上圖片'); return; }
  sending = true;
  draft.readOnly = true;
  $('sendText').disabled = true;
  $('sendText').textContent = '傳送中';
  $('pickImage').disabled = true;
  renderAttachments();
  let sent = false;
  try {
    // Each item becomes a history entry; the most recently sent item is on the Mac clipboard.
    if (draft.value.trim()) {
      const response = await api('/api/send', { method: 'POST', headers: { 'Content-Type': 'text/plain; charset=utf-8' }, body: draft.value });
      if (!response.ok) throw new Error('send failed');
      draft.value = '';
      sent = true;
    }
    while (pendingImages.length) {
      const entry = pendingImages[0];
      const response = await api('/api/send', { method: 'POST', headers: { 'Content-Type': entry.file.type }, body: entry.file });
      if (!response.ok) throw new Error('send failed');
      pendingImages.shift();
      URL.revokeObjectURL(entry.url);
      sent = true;
      renderAttachments();
    }
    toast('已傳到 Mac');
  } catch (error) {
    toast('傳送失敗，未送出的內容已保留');
  } finally {
    sending = false;
    draft.readOnly = false;
    $('sendText').disabled = false;
    $('sendText').textContent = '傳送';
    $('pickImage').disabled = false;
    renderAttachments();
    if (sent) { state.version = ''; load(); }
  }
};
window.addEventListener('resize', fitComposer);
if (window.visualViewport) window.visualViewport.addEventListener('resize', fitComposer);
if (window.ResizeObserver) new ResizeObserver(fitComposer).observe(document.querySelector('footer'));
fitComposer();

$('logout').onclick = async () => {
  if (!confirm('要登出這台裝置嗎？下次要重新輸入密碼。')) return;
  await api('/api/logout', { method: 'POST' }).catch(() => {});
  location.reload();
};

load();
setInterval(() => { if (document.visibilityState === 'visible') load(); }, 5000);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') load(); });
</script>
</body>
</html>
"""#

    /// Shown until this device signs in. Works the same on every Mac running 拾光, so one Home Screen shortcut fits all.
    static let loginHTML = #"""
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="拾光">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="manifest" href="/manifest.webmanifest">
<title>登入拾光</title>
<style>
  :root { --bg: #f2f2f7; --card: #fff; --text: #1c1c1e; --muted: #8e8e93; --accent: #ff7a59; }
  @media (prefers-color-scheme: dark) { :root { --bg: #000; --card: #1c1c1e; --text: #f2f2f7; --muted: #98989f; } }
  * { box-sizing: border-box; }
  body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; background: var(--bg); color: var(--text);
         font: 16px -apple-system, BlinkMacSystemFont, "PingFang TC", sans-serif; padding: 24px; }
  form { width: 100%; max-width: 340px; background: var(--card); border-radius: 18px; padding: 28px 22px; display: grid; gap: 14px; text-align: center; }
  img { width: 72px; height: 72px; margin: 0 auto; border-radius: 16px; }
  h1 { font-size: 22px; margin: 0; }
  p { margin: 0; color: var(--muted); font-size: 14px; line-height: 1.5; }
  input { font-size: 17px; padding: 12px 14px; border-radius: 12px; border: 1px solid rgba(128,128,128,.3); background: transparent; color: var(--text); width: 100%; }
  button { font-size: 17px; font-weight: 600; padding: 12px; border: 0; border-radius: 12px; background: var(--accent); color: #fff; }
  #error { color: #ff453a; min-height: 1.2em; }
</style>
</head>
<body>
<form id="form" autocomplete="on">
  <img src="/apple-touch-icon.png" alt="">
  <h1>拾光剪貼簿</h1>
  <p>輸入你在 Mac 上「手機分享設定」設的密碼。這台裝置之後就不用再輸入。</p>
  <input id="password" type="password" autocomplete="current-password" placeholder="密碼" required>
  <input id="device" type="text" autocomplete="off" placeholder="這台裝置的名字（例如：我的 iPhone）">
  <button type="submit">登入</button>
  <p id="error"></p>
</form>
<script>
const guess = /iPhone/.test(navigator.userAgent) ? 'iPhone' : /iPad/.test(navigator.userAgent) ? 'iPad' : /Android/.test(navigator.userAgent) ? 'Android 手機' : '瀏覽器';
document.getElementById('device').value = guess;
document.getElementById('form').onsubmit = async (e) => {
  e.preventDefault();
  const error = document.getElementById('error');
  error.textContent = '';
  try {
    const r = await fetch('/api/login', { method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'glean' },
      body: JSON.stringify({ password: document.getElementById('password').value, device: document.getElementById('device').value }) });
    const data = await r.json();
    if (data.ok) { location.replace('/'); return; }
    if (data.error === 'locked') error.textContent = '錯太多次了，請 ' + data.seconds + ' 秒後再試。';
    else error.textContent = '密碼不對' + (data.remaining > 0 ? '，還可以再試 ' + data.remaining + ' 次。' : '。');
  } catch (err) { error.textContent = '連不到 Mac，請確認 Mac 開著、拾光的手機分享有打開，而且在同一個網路。'; }
};
</script>
</body>
</html>
"""#
}
