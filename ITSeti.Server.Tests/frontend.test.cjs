const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const elements = new Map();
function element() {
  return { children: [], rows: [], value: '', textContent: '', classList: { toggle() {}, remove() {} },
    replaceChildren(...children) { this.children = children; }, append(...children) { this.children.push(...children); } };
}
const context = vm.createContext({ console, URLSearchParams, Headers, setTimeout, clearTimeout,
  document: {
    querySelector: () => ({ content: 'test' }),
    getElementById: id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
    querySelectorAll: () => [],
    createElement: () => element()
  }
});
const source = fs.readFileSync(path.join(__dirname, '../ITSeti.Server/wwwroot/app.js'), 'utf8');
vm.runInContext(source.slice(0, source.indexOf("for (const button of document.querySelectorAll('[data-view]'))")), context);
vm.runInContext('globalThis.pending = []; api = () => new Promise((resolve, reject) => pending.push({resolve,reject})); renderRun = run => { globalThis.rendered = run.id; };', context);
async function test() {
  context.noiseNote = 'Без доступа к исполняемому файлу процессов: 8';
  assert.equal(vm.runInContext('visibleNote(noiseNote)', context), false);
  assert.equal(vm.runInContext("visibleNote('SMART: неисправность')", context), true);
  for (const [value, expected] of [[62.37500762939453, '62 °C'], [62.8, '63 °C'], [0, '0 °C'], [null, 'Нет данных'], ['', 'Нет данных'], ['bad', 'Нет данных'], [Infinity, 'Нет данных'], [true, 'Нет данных']]) {
    context.temperatureValue = value;
    assert.equal(vm.runInContext('temperature(temperatureValue)', context), expected);
  }
  const first = vm.runInContext("selectRun({id:'old'})", context);
  const second = vm.runInContext("selectRun({id:'new'})", context);
  context.pending[1].resolve({ id: 'new' }); await second;
  context.pending[0].resolve({ id: 'old' }); await first;
  assert.equal(context.rendered, 'new', 'late response must not replace selected run');
  const abandoned = vm.runInContext("selectRun({id:'abandoned'})", context);
  vm.runInContext('clearDeviceSelection()', context);
  context.pending[2].resolve({ id: 'abandoned' }); await abandoned;
  assert.equal(elements.get('run-detail').children.length, 0, 'clearing selection invalidates pending detail');
  const staleError = vm.runInContext("selectRun({id:'failed'})", context);
  const fresh = vm.runInContext("selectRun({id:'fresh'})", context);
  context.pending[4].resolve({ id: 'fresh' }); await fresh;
  context.pending[3].reject(new Error('late failure')); await staleError;
  assert.equal(context.rendered, 'fresh', 'stale error must not replace current data');
  console.log('PASS: rounded temperatures, out-of-order run responses, cleared selection, stale errors');
}
test().catch(error => { console.error(error); process.exitCode = 1; });
