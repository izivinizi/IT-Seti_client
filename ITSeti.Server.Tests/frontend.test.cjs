const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const elements = new Map();
const created = [];
function element() {
  return { children: [], rows: [], value: '', textContent: '', className: '', title: '', disabled: false, handlers: {},
    classList: { toggle() {}, remove() {}, add(name) { this.owner.className += ' ' + name; } },
    setAttribute(name, value) { this[name] = value; }, addEventListener(name, callback) { this.handlers[name] = callback; },
    replaceChildren(...children) { this.children = children; }, append(...children) { this.children.push(...children); } };
}
function createElement() { const item = element(); item.classList.owner = item; created.push(item); return item; }
const context = vm.createContext({ console, URLSearchParams, Headers, setTimeout, clearTimeout,
  document: {
    querySelector: () => ({ content: 'test' }),
    getElementById: id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
    querySelectorAll: () => [],
    createElement
  }
});
const source = fs.readFileSync(path.join(__dirname, '../ITSeti.Server/wwwroot/app.js'), 'utf8');
const html = fs.readFileSync(path.join(__dirname, '../ITSeti.Server/wwwroot/index.html'), 'utf8');
const server = fs.readFileSync(path.join(__dirname, '../ITSeti.Server/Program.cs'), 'utf8');
assert.match(html, /data-view="application-errors"/);
assert.match(source, /\/api\/admin\/application-errors\?/);
assert.match(server, /admin\.MapPost\("\/process-policy\/allow"/);
vm.runInContext(source.slice(0, source.indexOf("for (const button of document.querySelectorAll('[data-view]'))")), context);
vm.runInContext('globalThis.actualRenderRun = renderRun; globalThis.pending = []; api = () => new Promise((resolve, reject) => pending.push({resolve,reject})); renderRun = run => { globalThis.rendered = run.id; };', context);
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
  vm.runInContext(`api = async path => { globalThis.requestedPath = path; return {hasMore:true,items:[{id:'1',occurredAt:'2026-10-13T08:00:00Z',computer:'PC-1',inventory:'1234',company:'ИТ-Сети',site:'Офис',component:'Служба',operation:'Сбор',summary:'Ошибка чтения',exceptionType:'IOException',technicalDetails:'fixture details',version:'2.4.3'}]}; }`, context);
  await vm.runInContext('loadApplicationErrors()', context);
  assert.match(vm.runInContext('requestedPath', context), /\/api\/admin\/application-errors\?/);
  assert.equal(elements.get('app-errors-list').children.length, 1, 'application error rows render');
  assert.equal(elements.get('app-errors-next').disabled, false, 'application error paging follows hasMore');
  vm.runInContext('renderRun = actualRenderRun;', context);
  vm.runInContext("actualRenderRun({kind:'Проверка',startedAt:'2026-10-14T10:00:00Z',details:{trigger:'Запуск пользователем из приложения',requestedByWindowsSessionId:4,windowsSessionId:0}})", context);
  assert.ok(elements.get('run-detail').children.some(item => item.textContent.includes('запрос из сеанса Windows 4') &&
    item.textContent.includes('процесс проверки в сеансе Windows 0')), 'run detail distinguishes requesting and service process sessions');
  let policyRequest;
  context.api = async (path, options) => { policyRequest = { path, options }; return { allowedNames: ['unknown.exe'] }; };
  vm.runInContext(`renderRun({id:'process-test',details:{full:{processes:[{name:'unknown.exe',publisher:'Fixture signer',signature:'Valid',path:'C:/tools/unknown.exe'}]}},unknownProcesses:[{name:'unknown.exe',publisher:'Fixture signer',signature:'Valid',path:'C:/tools/unknown.exe'}]})`, context);
  const processButton = created.find(item => item.className === 'process-allow-check');
  assert.ok(processButton, 'process detail exposes a compact allow button');
  assert.match(processButton['aria-label'], /Разрешить процесс/);
  await processButton.handlers.click();
  assert.equal(policyRequest.path, '/api/admin/process-policy/allow');
  assert.equal(policyRequest.options.method, 'POST');
  assert.equal(JSON.parse(policyRequest.options.body).name, 'unknown.exe');
  assert.equal(processButton.textContent, '✓');
  console.log('PASS: temperatures, stale responses, cleared selection, application errors and compact process approval');
}
test().catch(error => { console.error(error); process.exitCode = 1; });
