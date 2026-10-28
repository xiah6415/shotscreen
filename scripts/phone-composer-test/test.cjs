// Exercise the actual embedded page script without touching real clipboard contents or a Mac server.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('Sources/SuYanShot/Clipboard/PhoneSharePage.swift', 'utf8');
const script = source.match(/<script>([\s\S]*?)<\/script>/)[1];
class Element {
  constructor() {
    this.value = ''; this.children = []; this.style = {}; this.listeners = {};
    this.classList = { add() {}, remove() {}, toggle() {} };
    this.selectionStart = this.selectionEnd = 0; this.scrollHeight = 36;
  }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  setAttribute(name, value) { this[name] = value; }
  addEventListener(name, fn) { this.listeners[name] = fn; }
  querySelector() { return new Element(); }
  getBoundingClientRect() { return { height: 190 }; }
  setRangeText(text, start, end) { this.value = this.value.slice(0, start) + text + this.value.slice(end); }
}
function setup() {
  const nodes = new Map();
  const get = id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); };
  const requests = [], revoked = [], results = [];
  let sequence = 0;
  const context = vm.createContext({
    console, Intl, Date, Array, Object, Error, encodeURIComponent,
    setTimeout() { return 1; }, clearTimeout() {}, setInterval() {},
    navigator: {}, location: { reload() {} }, confirm: () => true,
    window: { addEventListener() {} },
    URL: { createObjectURL: () => 'blob:test-' + (++sequence), revokeObjectURL: url => revoked.push(url) },
    document: { getElementById: get, querySelector: () => get('footer'),
      createElement: () => new Element(), createTextNode: text => text,
      addEventListener() {}, visibilityState: 'visible' },
    fetch: async (path, options) => {
      if (path.startsWith('/api/items')) return { status: 200, json: async () => ({ items: [], tags: [], version: '1' }) };
      requests.push({ path, options });
      const next = results.shift();
      if (next instanceof Error) throw next;
      return { ok: next !== false, status: next === false ? 500 : 200 };
    }
  });
  vm.runInContext(script, context);
  const image = name => ({ name, type: 'image/png', size: 1024 });
  function paste(files, { text = '', fallback = false } = {}) {
    const event = { prevented: false, preventDefault() { this.prevented = true; }, clipboardData: {
      items: fallback ? [] : files.map(file => ({ kind: 'file', type: file.type, getAsFile: () => file })),
      files, getData: () => text
    } };
    get('draft').listeners.paste(event);
    return event;
  }
  return { get, requests, revoked, results, image, paste,
    count: () => vm.runInContext('pendingImages.length', context),
    send: () => get('sendText').onclick() };
}
(async () => {
  let page = setup();
  const first = page.image('pasted.png');
  assert.equal(page.paste([first]).prevented, true);
  assert.equal(page.count(), 1);
  assert.equal(page.get('attachments').children.length, 1);
  assert.equal(page.requests.length, 0, 'Pasting must not upload before Send');
  await page.send();
  assert.equal(page.requests[0].options.body, first);
  assert.equal(page.requests[0].options.headers['Content-Type'], 'image/png');
  assert.equal(page.requests[0].options.headers['X-Requested-With'], 'glean');
  assert.equal(page.requests[0].options.credentials, 'same-origin');
  assert.equal(page.count(), 0);
  assert.equal(page.revoked.length, 1);

  page = setup();
  assert.equal(page.paste([], { text: 'ordinary text' }).prevented, false, 'Keep native text paste');
  page.paste([page.image('fallback.png')], { fallback: true });
  assert.equal(page.count(), 1, 'Support clipboardData.files fallback');
  page.get('attachments').children[0].children[1].onclick();
  assert.equal(page.count(), 0, 'Remove attachment before send');
  assert.equal(page.revoked.length, 1);

  page = setup();
  page.get('draft').value = 'hello';
  page.get('draft').selectionStart = page.get('draft').selectionEnd = 5;
  page.paste([page.image('one.png'), page.image('two.png')], { text: ' world' });
  assert.equal(page.get('draft').value, 'hello world');
  await page.send();
  assert.equal(page.requests.length, 3, 'Send text and both images');
  assert.equal(page.requests[0].options.body, 'hello world');
  assert.equal(page.count(), 0);
  assert.equal(page.get('draft').value, '');

  page = setup();
  page.get('draft').value = 'keep me';
  page.paste([page.image('keep.png')]);
  page.results.push(new Error('offline'));
  await page.send();
  assert.equal(page.get('draft').value, 'keep me');
  assert.equal(page.count(), 1);
  assert.equal(page.get('sendText').disabled, false);
  assert.equal(page.get('draft').readOnly, false);

  page = setup();
  const remaining = page.image('second.png');
  page.paste([page.image('first.png'), remaining]);
  page.results.push(true, false);
  await page.send();
  assert.equal(page.count(), 1, 'Retain only unsent attachments after partial failure');
  await page.send();
  assert.equal(page.requests[2].options.body, remaining, 'Retry does not duplicate successful uploads');
  assert.equal(page.count(), 0);

  page = setup();
  page.paste([page.image('single.png')]);
  const sending = page.send();
  await page.send();
  assert.equal(page.paste([page.image('blocked.png')]).prevented, true);
  await sending;
  assert.equal(page.requests.length, 1, 'Prevent duplicate send and attachment changes during transfer');

  page = setup();
  page.paste([{ name: 'huge.png', type: 'image/png', size: 21 * 1024 * 1024 },
              { name: 'empty.png', type: 'image/png', size: 0 }]);
  assert.equal(page.count(), 0);
  page.paste(Array.from({ length: 11 }, (_, i) => page.image(i + '.png')));
  assert.equal(page.count(), 10);
  console.log('Phone composer checks passed: image paste, file fallback, text/multiple images, remove, size/count limits, retry and duplicate-send protection.');
})().catch(error => { console.error(error); process.exitCode = 1; });
