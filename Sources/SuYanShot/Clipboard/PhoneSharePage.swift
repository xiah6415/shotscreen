/// The single-page web app the phone opens. Everything it shows is built with textContent (never innerHTML)
/// because clipboard text is arbitrary and may contain markup.
enum PhoneSharePage {
    static let html = #"""
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<title>素顏 Shot 剪貼簿</title>
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
  #composer { display: flex; gap: 8px; align-items: flex-end; }
  #draft { flex: 1; font: 16px -apple-system, sans-serif; border-radius: 10px; border: 0; padding: 9px 12px; background: var(--card); color: var(--text); resize: none; max-height: 120px; outline: none; }
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
  <h1>剪貼簿</h1>
  <input id="search" type="search" placeholder="搜尋" autocomplete="off">
  <div id="chips"></div>
</header>
<main id="list"></main>
<footer>
  <div id="composer">
    <textarea id="draft" rows="1" placeholder="傳文字到 Mac"></textarea>
    <button id="pickImage" class="round" aria-label="傳照片">照片</button>
    <button id="sendText" class="round">傳送</button>
  </div>
  <input id="file" type="file" accept="image/*" hidden>
</footer>
<div id="toast"></div>
<div id="viewer"><img alt=""><p>長按圖片可以儲存或複製・點一下關閉</p></div>
<script>
const token = new URLSearchParams(location.search).get('t') || '';
const state = { items: [], tags: [], version: '', filter: 'all', query: '', expired: false };
const $ = (id) => document.getElementById(id);

function api(path, options = {}) {
  const sep = path.includes('?') ? '&' : '?';
  return fetch(path + sep + 't=' + encodeURIComponent(token), options);
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
    if (response.status === 403) { state.expired = true; showExpired(); return; }
    const data = await response.json();
    if (!data.unchanged) { state.items = data.items; state.tags = data.tags; state.version = data.version; render(); }
  } catch (e) { /* Mac asleep or off the network; try again on the next tick */ }
}

function showExpired() {
  const list = $('list');
  list.replaceChildren(el('div', 'empty', '連結已失效或分享已停止。請在 Mac 上重新打開「在手機上開啟」並掃描 QR code。'));
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
    list.replaceChildren(el('div', 'empty', state.items.length ? '沒有符合的項目' : '還沒有紀錄。在 Mac 上複製東西，或從下面傳文字過去。'));
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
    img.src = '/api/items/' + item.id + '/image?thumb=1&t=' + encodeURIComponent(token);
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
  viewer.querySelector('img').src = '/api/items/' + item.id + '/image?t=' + encodeURIComponent(token);
  viewer.classList.add('show');
}
$('viewer').onclick = (e) => { if (e.target.tagName !== 'IMG') $('viewer').classList.remove('show'); };
$('viewer').querySelector('img').onclick = () => $('viewer').classList.remove('show');

$('search').oninput = (e) => { state.query = e.target.value; render(); };

const draft = $('draft');
draft.oninput = () => { draft.style.height = 'auto'; draft.style.height = Math.min(draft.scrollHeight, 120) + 'px'; };
$('sendText').onclick = async () => {
  const text = draft.value;
  if (!text.trim()) { toast('先輸入一些文字'); return; }
  const r = await api('/api/send', { method: 'POST', headers: { 'Content-Type': 'text/plain; charset=utf-8' }, body: text });
  if (r.ok) { draft.value = ''; draft.oninput(); toast('已傳到 Mac'); state.version = ''; load(); } else { toast('傳送失敗'); }
};
$('pickImage').onclick = () => $('file').click();
$('file').onchange = async (e) => {
  const file = e.target.files[0];
  if (!file) return;
  toast('傳送中…');
  const r = await api('/api/send', { method: 'POST', headers: { 'Content-Type': file.type || 'image/jpeg' }, body: file });
  e.target.value = '';
  if (r.ok) { toast('已傳到 Mac'); state.version = ''; load(); } else { toast('傳送失敗'); }
};

load();
setInterval(() => { if (document.visibilityState === 'visible') load(); }, 5000);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') load(); });
</script>
</body>
</html>
"""#
}
