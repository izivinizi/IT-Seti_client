const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const elements = new Map();
const created = [];
function element() {
  return { children: [], rows: [], dataset: {}, value: '', textContent: '', className: '', title: '', disabled: false, handlers: {},
    classList: { toggle() {}, remove() {}, add(name) { this.owner.className += ' ' + name; } },
    setAttribute(name, value) { this[name] = value; }, addEventListener(name, callback) { this.handlers[name] = callback; },
    replaceChildren(...children) { this.children = children; }, append(...children) { children.forEach(child=>child.parent=this); this.children.push(...children); },
    remove() { if(this.parent)this.parent.children=this.parent.children.filter(child=>child!==this); },
    get childElementCount() { return this.children.length; }, showModal() { this.open = true; }, close() { this.open = false; } };
}
function createElement() { const item = element(); item.classList.owner = item; created.push(item); return item; }
const context = vm.createContext({ console, URLSearchParams, Headers, setTimeout, clearTimeout,
  document: {
    querySelector: () => ({ content: 'test' }),
    getElementById: id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
    querySelectorAll: () => [],
    createElement,
    createElementNS: () => { const item = element(); item.namespaceURI = 'http://www.w3.org/2000/svg'; item.classList.owner = item; return item; }
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
  vm.runInContext("notifyError('Проверка ошибки'); notifyError('Проверка ошибки');", context);
  assert.equal(elements.get('notifications').children.length,1,'duplicate failures share a notification');
  vm.runInContext("for(let i=0;i<5;i++)notifyError('Ошибка '+i);",context);
  assert.equal(elements.get('notifications').children.length,4,'notifications have a bounded queue');
  const toast=elements.get('notifications').children[0];
  assert.equal(toast.role,'alert'); toast.children[1].handlers.click();
  assert.equal(elements.get('notifications').children.length,3,'notification close removes the message');

  vm.runInContext("state.remoteAccess={fixture:{rms:'123-456',anyDesk:'987654'}}; globalThis.accessLine=remoteAccessLine({deviceId:'fixture'});",context);
  assert.deepEqual(Array.from(context.accessLine.children,item=>item.textContent),['RMS 123-456','AnyDesk 987654']);
  assert.ok(context.accessLine.children.every(item=>item.className !== 'button'),'remote IDs are plain text without copy controls');
  let stopped=false;context.accessLine.handlers.click({stopPropagation(){stopped=true;}});
  assert.equal(stopped,true,'selecting remote IDs must not open the computer card');

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
  vm.runInContext(`api = async path => { globalThis.requestedPath = path; return {hasMore:true,items:[{deviceId:'device-1',latestErrorAt:'2026-10-13T08:00:00Z',computer:'PC-1',inventory:'1234',company:'ИТ-Сети',site:'Офис',errorCount:2,hasMoreErrors:false,errors:[{id:'1',occurredAt:'2026-10-13T08:00:00Z',component:'Служба',operation:'Сбор',summary:'Ошибка чтения',exceptionType:'IOException',technicalDetails:'fixture details',version:'2.4.3',sensorHistory:[{capturedAtUtc:'2026-10-13T07:59:00Z',cpuLoadPercent:20,memoryLoadPercent:40,sensors:[{name:'CPU',value:55,unit:'°C',category:'CpuTemperature'}]},{capturedAtUtc:'2026-10-13T08:00:00Z',cpuLoadPercent:30,memoryLoadPercent:41,sensors:[{name:'CPU',value:65,unit:'°C',category:'CpuTemperature'}]}]},{id:'2',occurredAt:'2026-10-13T07:00:00Z',component:'Служба',operation:'Опрос',summary:'Вторая ошибка'}]}]}; }`, context);
  await vm.runInContext('loadApplicationErrors()', context);
  assert.match(vm.runInContext('requestedPath', context), /\/api\/admin\/application-errors\?/);
  assert.equal(elements.get('app-errors-list').children.length, 1, 'application errors are grouped into one computer card');
  assert.equal(elements.get('app-errors-list').children[0].children.find(item => item.className === 'application-error-items').children.length, 2,
    'a computer card contains all its errors');
  const graphButton = created.find(item => item.className.includes('telemetry-button'));
  assert.ok(graphButton, 'error history exposes a chart button');
  graphButton.handlers.click({ stopPropagation() {} });
  assert.equal(elements.get('telemetry-dialog').open, true, 'chart opens in the telemetry dialog');
  assert.ok(elements.get('telemetry-chart').children.length, 'chart contains an SVG visualization');
  assert.equal(elements.get('app-errors-next').disabled, false, 'application error paging follows hasMore');
  vm.runInContext('renderRun = actualRenderRun;', context);
  vm.runInContext("actualRenderRun({kind:'Проверка',startedAt:'2026-10-14T10:00:00Z',details:{trigger:'Запуск пользователем из приложения',requestedByWindowsSessionId:4,windowsSessionId:0}})", context);
  assert.ok(elements.get('run-detail').children.some(item => item.textContent.includes('Запуск: Запуск пользователем')), 'run detail retains launch reason without internal session remarks');
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
  vm.runInContext(`renderRun({kind:'Постоянный мониторинг',startedAt:'2026-10-16T10:00:00Z',details:{diagnosticIssues:[{title:'Disk latency',detail:'0 C: 150 ms',severity:'Critical'}]},hardwareProfile:{cpuModel:'Saved CPU',smartDisks:[{model:'SSD',status:'Good'}],smartAtUtc:'2026-10-16T01:00:00Z',benchmark:{drive:'C:',read:500,write:450,engine:'DiskSpd'},benchmarkAtUtc:'2026-10-16T01:00:00Z'}})`, context);
  const textOf = item => [item.textContent || '', ...item.children.map(textOf)].join(' ');
  assert.doesNotMatch(textOf(elements.get('run-detail')), /Нет данных|не передан|Локальные администраторы|Журнал Windows/,
    'a monitoring event must not render empty check sections or null CPU as zero');
  assert.match(textOf(elements.get('device-profile')), /Saved CPU.*Накопители.*SSD.*SMART.*DiskSpd.*500/s,
    'monitoring keeps the saved configuration, SMART and benchmark above event details');
  vm.runInContext(`renderRunNavigation([{id:'m',kind:'Постоянный мониторинг'},{id:'f',kind:'Плановая проверка'}],false)`,context);
  assert.equal(elements.get('run-list').children.length,1,'checks can be opened without scanning monitoring history');
  assert.equal(elements.get('run-list').children[0].dataset.runId,'f');
  console.log('PASS: temperatures, stale responses, cleared selection, application errors and compact process approval');
}
test().catch(error => { console.error(error); process.exitCode = 1; });
