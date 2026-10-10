const csrf = document.querySelector('meta[name="csrf-token"]').content;
const $ = id => document.getElementById(id);
const state = { companies: [], sites: [], devices: [], deviceOptions: [], selectedDevice: null, devicePage: 0,
  monitorPage: 0, ticketPage: 0, deviceRequest: 0, deviceOptionsRequest: 0, monitorRequest: 0, ticketRequest: 0, runRequest: 0, selectionRequest: 0, view: 'computers' };

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.method && options.method !== 'GET') headers.set('X-CSRF-TOKEN', csrf);
  const response = await fetch(path, { credentials: 'same-origin', ...options, headers });
  if (response.status === 401) { location.assign('/login'); throw Error('Сеанс завершён.'); }
  if (!response.ok) {
    let message = 'Ошибка ' + response.status;
    try { message = (await response.json()).error || message; } catch { /* No JSON response. */ }
    throw Error(message);
  }
  return response.status === 204 ? null : response.json();
}

function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (className) element.className = className;
  return element;
}

function status(id, message, kind = '') {
  const element = $(id);
  element.textContent = message;
  element.className = 'status ' + kind;
}

function date(value) { return value ? new Date(value).toLocaleString('ru-RU') : 'ещё не было'; }
function temperature(value) {
  if (value == null || value === '' || typeof value === 'boolean') return 'Нет данных';
  const number = Number(value);
  return Number.isFinite(number) && number >= 0 && number <= 120 ? Math.round(number) + ' °C' : 'Нет данных';
}
function visibleNote(value) {
  const text = String(value).trim().toLowerCase();
  return !text.startsWith('без доступа к исполняемому файлу процессов') && !text.startsWith('ограничение интерфейса');
}
function property(object, ...names) {
  if (!object || typeof object !== 'object') return null;
  for (const name of names) {
    const key = Object.keys(object).find(item => item.toLowerCase() === name.toLowerCase());
    if (key) return object[key];
  }
  return null;
}
function list(value) { return Array.isArray(value) ? value : []; }
function short(value, max = 120) { const text = String(value ?? ''); return text.length > max ? text.slice(0, max - 1) + '…' : text; }

function showView(view) {
  state.view = view;
  $('page-title').textContent = { computers: 'Компьютеры', monitoring: 'Мониторинг', tickets: 'Заявки', packages: 'ПО и обновления' }[view];
  for (const item of document.querySelectorAll('.nav-item')) {
    const selected = item.dataset.view === view;
    item.classList.toggle('active', selected);
    if (selected) item.setAttribute('aria-current', 'page'); else item.removeAttribute('aria-current');
  }
  for (const item of document.querySelectorAll('.view')) item.classList.toggle('active', item.id === 'view-' + view);
  if (view === 'monitoring') loadMonitoring();
  if (view === 'tickets') loadTickets();
  if (view === 'packages') loadPackages();
}

function fillCompanies(selectId) {
  const select = $(selectId), previous = select.value;
  select.replaceChildren(new Option('Все компании', ''));
  for (const company of state.companies) select.add(new Option(company.name, company.id));
  if ([...select.options].some(option => option.value === previous)) select.value = previous;
  refreshSearchSelect(select);
}

function fillSites(prefix, reset = false) {
  const company = $(prefix + '-company').value, select = $(prefix + '-site');
  const previous = reset ? '' : select.value;
  select.replaceChildren(new Option('Все объекты', ''));
  if (prefix === 'device') select.add(new Option('Без объекта', 'none'));
  for (const site of state.sites.filter(item => !company || String(item.companyId) === company)) {
    select.add(new Option(site.address ? site.name + ' · ' + site.address : site.name, site.id));
  }
  if ([...select.options].some(option => option.value === previous)) select.value = previous;
  refreshSearchSelect(select);
}

function refreshSearchSelect(select) {
  if (!select.searchInput) {
    const input = node('input', undefined, 'field'), choices = node('datalist');
    choices.id = select.id + '-choices';
    input.id = select.id + '-search'; input.type = 'text'; input.autocomplete = 'off';
    input.setAttribute('list', choices.id);
    input.setAttribute('aria-label', select.parentElement.querySelector('span')?.textContent || 'Поиск');
    select.hidden = true; select.after(input, choices);
    select.searchInput = input; select.searchChoices = choices;
    input.addEventListener('focus', () => input.select());
    input.addEventListener('change', () => {
      const match = [...choices.children].find(option => option.value === input.value);
      if (match) select.value = match.dataset.value;
      else if (!input.value.trim()) select.value = '';
      else return;
      select.dispatchEvent(new Event('change', { bubbles: true }));
    });
    input.addEventListener('keydown', event => {
      if (event.key === 'Escape') input.value = select.selectedOptions[0]?.textContent || '';
    });
    select.addEventListener('change', () => refreshSearchSelect(select));
  }
  const options = [...select.options];
  select.searchChoices.replaceChildren(...options.map(option => {
    const display = option.textContent + (options.filter(other => other.textContent === option.textContent).length > 1 ? ' · #' + option.value : '');
    const choice = node('option'); choice.value = display; choice.dataset.value = option.value; return choice;
  }));
  const current = [...select.searchChoices.children].find(option => option.dataset.value === select.value);
  select.searchInput.value = select.value ? current?.value || '' : '';
  select.searchInput.placeholder = options[0]?.textContent || 'Поиск';
}

async function loadCatalog() {
  const catalog = await api('/api/admin/catalog');
  state.companies = catalog.companies || [];
  state.sites = catalog.sites || [];
  for (const prefix of ['device', 'monitor', 'ticket']) {
    fillCompanies(prefix + '-company');
    fillSites(prefix);
  }
}

async function loadOverview() {
  const overview = await api('/api/admin/overview');
  $('stat-all').textContent = overview.computers;
  $('stat-online').textContent = overview.online24h;
  $('stat-checks').textContent = overview.checks24h;
  $('stat-companies').textContent = overview.companies;
  $('stat-sites').textContent = overview.sites;
  $('monitor-total').textContent = overview.newAlerts7d;
  const badge = $('monitor-badge');
  badge.textContent = overview.newAlerts7d > 99 ? '99+' : overview.newAlerts7d;
  badge.classList.toggle('hidden', overview.newAlerts7d === 0);
}

function deviceParameters() {
  const params = new URLSearchParams({ page: String(state.devicePage) });
  const query = $('device-search').value.trim();
  if (query) params.set('q', query);
  if ($('device-company').value) params.set('companyId', $('device-company').value);
  if ($('device-site').value === 'none') params.set('unassignedSite', 'true');
  else if ($('device-site').value) params.set('siteId', $('device-site').value);
  return params;
}

async function loadDevices() {
  const requestId = ++state.deviceRequest;
  status('device-status', 'Загружаю список…');
  try {
    const company = $('device-company').value;
    const tree = await api('/api/admin/computer-tree' + (company ? '?companyId=' + encodeURIComponent(company) : ''));
    if (requestId !== state.deviceRequest) return;
    const showTree = !$('device-search').value.trim() && (!company || (!$('device-site').value && tree.items.length > 1));
    if (showTree) {
      state.devices = [];
      $('computer-list-title').textContent = company ? 'Объекты' : 'Компании';
      $('computer-columns').replaceChildren(node('th', company ? 'Объект' : 'Компания'), node('th', 'Компьютеры'), node('th', ''));
      const body = $('device-rows'); body.replaceChildren();
      for (const item of tree.items) {
        const row = node('tr', undefined, 'clickable'); row.tabIndex = 0;
        const name = node('td'); name.append(node('span', item.name, 'cell-main'));
        if (item.address) name.append(node('span', item.address, 'cell-sub'));
        row.append(name, node('td', String(item.computers)), node('td', '›'));
        const open = () => {
          clearDeviceSelection(); state.devicePage = 0;
          if (company) $('device-site').value = item.id == null ? 'none' : String(item.id);
          else { $('device-company').value = String(item.id); fillSites('device', true); }
          refreshSearchSelect($('device-company')); refreshSearchSelect($('device-site'));
          loadDevices();
        };
        row.addEventListener('click', open);
        row.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(); } });
        body.append(row);
      }
      $('device-empty').classList.toggle('hidden', !!tree.items.length);
      $('device-count').textContent = 'Показано ' + tree.items.length;
      $('device-page').textContent = company ? 'Объекты компании' : 'Все компании';
      $('device-prev').disabled = true; $('device-next').disabled = true;
      status('device-status', ''); return;
    }
    $('computer-list-title').textContent = 'Компьютеры';
    $('computer-columns').replaceChildren(node('th', 'Инв. №'), node('th', 'Состояние'), node('th', 'Последняя проверка'));
    const result = await api('/api/admin/devices?' + deviceParameters());
    if (requestId !== state.deviceRequest) return;
    state.devices = result.items || [];
    const body = $('device-rows'); body.replaceChildren();
    for (const device of state.devices) {
      const row = node('tr', undefined, 'clickable');
      row.tabIndex = 0;
      row.classList.toggle('selected', state.selectedDevice?.id === device.id);
      const serialLabel = device.inventory ? 'Инв. № ' + device.inventory : 'Инвентарник не указан';
      const serial = node('td'); serial.append(node('span', serialLabel, 'cell-main'));
      const health = node('td', {normal:'Норма',warning:'Требует внимания',critical:'Критическое',unknown:'Нет проверки'}[device.health] || 'Нет проверки',
        {normal:'signal-ok',warning:'signal-warn',critical:'signal-error'}[device.health] || 'muted');
      if (device.remoteAttention) health.append(node('span', 'Нет RMS/OCS', 'meta signal-warn'));
      row.append(serial, health, node('td', date(device.lastCheck)));
      row.addEventListener('click', () => selectDevice(device));
      row.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); selectDevice(device); } });
      body.append(row);
    }
    $('device-empty').classList.toggle('hidden', state.devices.length !== 0);
    $('device-count').textContent = state.devices.length ? `Показано ${state.devices.length}` : '';
    $('device-page').textContent = 'Страница ' + (state.devicePage + 1);
    $('device-prev').disabled = state.devicePage === 0;
    $('device-next').disabled = !result.hasMore;
    status('device-status', '');
  } catch (error) { if (requestId === state.deviceRequest) status('device-status', error.message, 'error'); }
}

async function selectDevice(device, requestedRunId = null) {
  const requestId = ++state.selectionRequest;
  ++state.runRequest;
  state.selectedDevice = device;
  showView('computers');
  for (const row of $('device-rows').rows) row.classList.remove('selected');
  const selectedIndex = state.devices.findIndex(item => item.id === device.id);
  if (selectedIndex >= 0) $('device-rows').rows[selectedIndex].classList.add('selected');
  $('selected-title').textContent = device.inventory ? 'Инв. № ' + device.inventory : 'Инвентарник не указан';
  $('selected-meta').textContent = [device.inventory ? 'Инв. № ' + device.inventory : null,
    device.company, device.site, device.os, device.osVersion].filter(Boolean).join(' · ');
  $('run-list').replaceChildren(); $('run-detail').replaceChildren();
  try {
    const runs = await api('/api/admin/devices/' + device.id + '/runs');
    if (requestId !== state.selectionRequest) return;
    if (!runs.length) { $('run-detail').append(node('p', 'Проверок пока нет.', 'detail-empty')); return; }
    for (const run of runs) {
      const button = node('button', date(run.startedAt) + ' · ' + run.kind + ' · ' +
        (property(run.summary, 'result') || 'Результат сохранён'), 'run-item');
      button.type = 'button'; button.dataset.runId = run.id;
      button.addEventListener('click', () => selectRun(run));
      $('run-list').append(button);
    }
    await selectRun(runs.find(run => run.id === requestedRunId) || runs[0]);
  } catch (error) { if (requestId === state.selectionRequest) $('run-detail').replaceChildren(node('p', error.message, 'signal-error')); }
}

function clearDeviceSelection() {
  ++state.selectionRequest;
  ++state.runRequest;
  state.selectedDevice = null;
  $('selected-title').textContent = 'Выберите ПК';
  $('selected-meta').textContent = '';
  $('run-list').replaceChildren();
  $('run-detail').replaceChildren();
  for (const row of $('device-rows').rows) row.classList.remove('selected');
}

async function selectRun(run) {
  const requestId = ++state.runRequest;
  for (const button of $('run-list').children) button.classList.toggle('active', button.dataset.runId === run.id);
  $('run-detail').replaceChildren(node('p', 'Загружаю проверку…', 'detail-empty'));
  try {
    const result = await api('/api/admin/runs/' + run.id);
    if (requestId === state.runRequest) renderRun(result);
  }
  catch (error) { if (requestId === state.runRequest) $('run-detail').replaceChildren(node('p', error.message, 'signal-error')); }
}


function detailSection(root, title, values, emptyText, collapsed = false) {
  const container = collapsed ? node('details', undefined, 'report-section') : root;
  container.append(node(collapsed ? 'summary' : 'h3', title + (collapsed ? ' · ' + values.length : '')));
  if (!values.length) container.append(node('p', emptyText, 'detail-empty'));
  else for (const value of values) container.append(node('div', value.text, value.className || 'detail-row'));
  if (collapsed) root.append(container);
}

function eventIdentity(event) {
  const provider = property(event, 'provider', 'source') || '';
  const message = property(event, 'message') || '';
  const bugcheck = String(provider).toLowerCase().includes('kernel-power') && String(property(event, 'id')) === '41'
    ? (String(message).match(/\bBugcheckCode\s*[:=]\s*(\d+)\b/i)?.[1] || '') : '';
  return [property(event, 'log'), provider, property(event, 'id'), property(event, 'level') || 2, bugcheck].join('|').toLowerCase();
}

function renderRun(run) {
  const root = $('run-detail'); root.replaceChildren();
  const report = run.details || {}, full = property(report, 'full') || {};
  root.append(node('h3', 'Результат проверки'));
  root.append(node('span', date(run.startedAt) + ' · ' + run.kind, 'meta'));
  const identifiers = [property(report, 'inventoryNumber') && 'Инв. № ' + property(report, 'inventoryNumber'),
    property(report, 'rmsId') && 'RMS ' + property(report, 'rmsId'),
    property(report, 'anyDeskId') && 'AnyDesk ' + property(report, 'anyDeskId')].filter(Boolean);
  if (identifiers.length) root.append(node('div', identifiers.join(' · '), 'detail-row'));
  const stats = node('div', undefined, 'detail-stats');
  const items = [
    ['Процессор', property(report, 'cpuPercent') == null ? 'Нет данных' : property(report, 'cpuPercent') + '%'],
    ['Температура', temperature(property(report, 'cpuTemperatureC') ?? property(property(report, 'full'), 'cpuTemperatureC'))],
    ['ОЗУ', property(report, 'memoryUsedPercent') == null ? 'Нет данных' : Number(property(report, 'memoryUsedPercent')).toFixed(0) + '%'],
    ['Windows', [property(report, 'windowsEdition', 'osName'), property(report, 'windowsRelease', 'osVersion')].filter(Boolean).join(' ') || 'Нет данных']
  ];
  for (const [label, value] of items) { const cell = node('div'); cell.append(node('span', label), node('strong', String(value))); stats.append(cell); }
  root.append(stats);
  const issues = list(run.issues || property(report, 'diagnosticIssues'));
  const critical = issues.filter(issue => Number(property(issue, 'severity')) === 1 || property(issue, 'severity') === 'Critical');
  const warnings = issues.filter(issue => Number(property(issue, 'severity')) === 2 || property(issue, 'severity') === 'Warning');
  const issueRows = values => values.map(issue => ({text: [property(issue, 'title'), property(issue, 'detail')].filter(Boolean).join(' · '), className: 'detail-row ' + (critical.includes(issue) ? 'critical' : 'warning')}));
  detailSection(root, 'Критические проблемы', issueRows(critical), 'Критических проблем не найдено.');
  detailSection(root, 'Предупреждения', issueRows(warnings), 'Предупреждений нет.', true);
  const disks = list(property(report, 'disks'));
  const smart = list(property(full, 'smartDisks') || property(report, 'smartDisks'));
  detailSection(root, 'Накопители', disks.map(disk => {
    const free = property(disk, 'freeBytes'), total = property(disk, 'totalBytes');
    return { text: `${property(disk, 'name') || 'Диск'} · ${free != null && total != null ? (free / 1073741824).toFixed(1) + ' из ' + (total / 1073741824).toFixed(1) + ' ГБ свободно' : 'нет данных о свободном месте'}` };
  }).concat(smart.map(disk => ({ text: [property(disk, 'model'), property(disk, 'status', 'health'), property(disk, 'smartWarnings')].filter(Boolean).join(' · ') }))), 'Данные о дисках не переданы.');
  const previous = run.previous || {}, prevFull = property(previous, 'full') || {};
  const processes = list(run.unknownProcesses || property(full, 'processes') || property(report, 'processes'));
  const oldProcesses = new Set(list(property(prevFull, 'processes') || property(previous, 'processes'))
    .map(process => [property(process, 'name', 'processName'), property(process, 'path')].join('|').toLowerCase()));
  detailSection(root, 'Процессы', processes.slice(0, 100).map(process => ({
    text: [property(process, 'name', 'processName'), property(process, 'publisher', 'description'), property(process, 'path')].filter(Boolean).join(' · '),
    className: run.previous && !oldProcesses.has([property(process, 'name', 'processName'), property(process, 'path')].join('|').toLowerCase()) ? 'detail-row new-item' : 'detail-row'
  })), 'Данные о процессах не переданы.', true);
  const events = list(property(full, 'events') || property(report, 'events'));
  const previousEvents = property(prevFull, 'events') || property(previous, 'events');
  const oldEvents = new Set(list(previousEvents).map(eventIdentity));
  const eventRows = values => values.map(event => ({
    text: [property(event, 'time'), property(event, 'provider', 'source'), '#' + (property(event, 'id') ?? '?'), property(event, 'message')].filter(Boolean).join(' · '),
    className: 'detail-row ' + (run.previous && Array.isArray(previousEvents) && !oldEvents.has(eventIdentity(event)) ? 'new-item' : '')
  }));
  detailSection(root, 'Журнал Windows · Критические', eventRows(events.filter(event => Number(property(event, 'level')) === 1)), 'Критических событий нет.', true);
  detailSection(root, 'Журнал Windows · Ошибки', eventRows(events.filter(event => Number(property(event, 'level') ?? 2) === 2)), 'Ошибок нет.', true);
  const admins = list(property(report, 'adminAccounts', 'localAdminAccounts', 'administrators') || property(full, 'adminAccounts', 'localAdminAccounts', 'administrators'));
  detailSection(root, 'Локальные администраторы', admins.map(account => ({ text: typeof account === 'string' ? account : property(account, 'name', 'account') || JSON.stringify(account) })), 'Список не передан клиентом.');
  detailSection(root, 'Журнал выполнения', list(property(report, 'notes', 'findings')).filter(visibleNote).map(text => ({ text: String(text) })), 'Журнал не передан.', true);
}

function monitoringParameters() {
  const params = new URLSearchParams({ page: String(state.monitorPage), days: $('monitor-days').value });
  params.set('severity', $('monitor-severity').value || '1');
  if ($('monitor-category').value) params.set('category', $('monitor-category').value);
  if ($('monitor-company').value) params.set('companyId', $('monitor-company').value);
  if ($('monitor-site').value) params.set('siteId', $('monitor-site').value);
  return params;
}

async function loadMonitoring() {
  const requestId = ++state.monitorRequest;
  status('monitor-status', 'Загружаю события…');
  try {
    const params = monitoringParameters();
    const connectionsParams = new URLSearchParams({ days: $('monitor-days').value });
    if ($('monitor-company').value) connectionsParams.set('companyId', $('monitor-company').value);
    if ($('monitor-site').value) connectionsParams.set('siteId', $('monitor-site').value);
    const [result, connections] = await Promise.all([
      api('/api/admin/monitoring?' + params),
      api('/api/admin/new-connections?' + connectionsParams)
    ]);
    if (requestId !== state.monitorRequest) return;
    const connectionContainer = $('monitor-connections'); connectionContainer.replaceChildren();
    for (const item of connections.items || []) {
      const row = node('div', undefined, 'monitor-connection-row');
      const title = node('strong', item.inventory ? 'Инв. № ' + item.inventory : 'Инвентарник не указан');
      const place = node('span', [item.company, item.site].filter(Boolean).join(' · ') || 'Компания/объект не указаны');
      const serial = node('span', item.serial ? 'S/N ' + item.serial : 'Серийный номер не передан', 'meta');
      const when = node('span', date(item.createdAt), 'when');
      const button = node('button', 'Открыть ПК', 'secondary compact');
      button.type = 'button';
      button.addEventListener('click', () => selectDevice({ id: item.deviceId, serial: item.serial,
        inventory: item.inventory, company: item.company, site: item.site, os: item.os, osVersion: item.osVersion }));
      const info = node('div'); info.append(title, place, serial);
      row.append(info, when, button);
      connectionContainer.append(row);
    }
    $('connections-count').textContent = connections.items?.length ? `${connections.items.length} новых` : '';
    $('monitor-connections-empty').classList.toggle('hidden', !!connections.items?.length);
    const container = $('monitor-list'); container.replaceChildren();
    for (const alert of result.items || []) {
      const row = node('div', undefined, 'monitor-row ' + (alert.severity === 1 ? 'critical' : 'warning'));
      const issue = node('div');
      issue.append(node('span', alert.category === 'event' ? 'Ошибка журнала Windows' : 'Замечание проверки', 'type'));
      const details = node('details', undefined, 'monitor-details');
      details.append(node('summary', alert.title), node('p', alert.detail));
      issue.append(details);
      const place = node('div');
      place.append(node('strong', alert.inventory ? 'Инв. № ' + alert.inventory : 'Инвентарник не указан'), node('p', [alert.company, alert.site].filter(Boolean).join(' · ')));
      if (alert.inventory) place.append(node('span', 'Инв. № ' + alert.inventory, 'meta'));
      const when = node('div', date(alert.detectedAt), 'when');
      const button = node('button', 'Открыть проверку', 'secondary compact');
      button.type = 'button';
      button.addEventListener('click', () => selectDevice({ id: alert.deviceId, serial: alert.serial, inventory: alert.inventory,
        company: alert.company, site: alert.site }, alert.runId));
      row.append(issue, place, when, button); container.append(row);
    }
    $('monitor-empty').classList.toggle('hidden', !!result.items?.length);
    $('monitor-page').textContent = 'Страница ' + (state.monitorPage + 1);
    $('monitor-prev').disabled = state.monitorPage === 0;
    $('monitor-next').disabled = !result.hasMore;
    status('monitor-status', '');
  } catch (error) { if (requestId === state.monitorRequest) status('monitor-status', error.message, 'error'); }
}

const ticketStates = { queued: 'В очереди', sending: 'Отправляется', created: 'Отправлена в Okdesk',
  rejected: 'Отклонена', unknown: 'Требует проверки' };

async function loadTickets() {
  const requestId = ++state.ticketRequest;
  const params = new URLSearchParams({ page: String(state.ticketPage) });
  if ($('ticket-status-filter').value) params.set('status', $('ticket-status-filter').value);
  if ($('ticket-company').value) params.set('companyId', $('ticket-company').value);
  if ($('ticket-site').value) params.set('siteId', $('ticket-site').value);
  status('ticket-status', 'Загружаю заявки…');
  try {
    const result = await api('/api/admin/tickets?' + params);
    if (requestId !== state.ticketRequest) return;
    const body = $('ticket-rows'); body.replaceChildren();
    for (const ticket of result.items || []) {
      const row = node('tr');
      const subject = node('td');
      subject.append(node('strong', ticket.title, 'cell-main'), node('span', ticket.service, 'cell-sub'));
      const description = node('p', ticket.description, 'ticket-description');
      subject.append(description);
      const computer = node('td');
      computer.append(node('strong', ticket.inventory ? 'Инв. № ' + ticket.inventory : 'Инвентарник не указан', 'cell-main'),
        node('span', [ticket.company, ticket.site].filter(Boolean).join(' · '), 'cell-sub'));
      const current = node('td');
      current.append(node('strong', ticketStates[ticket.status] || ticket.status,
        ticket.status === 'created' ? 'signal-ok' : ticket.status === 'queued' || ticket.status === 'sending' ? 'signal-warn' : 'signal-error'));
      if (ticket.issueId) {
        const link = node('a', 'Заявка #' + ticket.issueId);
        link.href = 'https://it-seti.okdesk.ru/issues/' + ticket.issueId;
        link.target = '_blank'; link.rel = 'noopener noreferrer'; current.append(link);
      }
      if (ticket.error) current.append(node('span', ticket.error, 'cell-sub'));
      if (ticket.workflow !== 'opened') current.append(node('span', ticket.workflow === 'completed' ? 'Выполнена' : 'Отменена', 'cell-sub'));
      if (ticket.actionState) current.append(node('span', 'Статус в Okdesk: ' + (ticketStates[ticket.actionState] || ticket.actionState), 'cell-sub'));
      if (ticket.actionError) current.append(node('span', ticket.actionError, 'signal-error'));
      const manage = node('button', 'Открыть и ответить', 'secondary compact'); manage.type = 'button';
      manage.addEventListener('click', () => openTicket(ticket)); current.append(manage);
      row.append(subject, computer, node('td', date(ticket.createdAt)), current);
      body.append(row);
    }
    $('ticket-empty').classList.toggle('hidden', !!result.items?.length);
    $('ticket-page').textContent = 'Страница ' + (state.ticketPage + 1);
    $('ticket-prev').disabled = state.ticketPage === 0;
    $('ticket-next').disabled = !result.hasMore;
    status('ticket-status', '');
  } catch (error) { if (requestId === state.ticketRequest) status('ticket-status', error.message, 'error'); }
}

let activeTicket = null, replyId = null, conversationRequest = 0;
async function loadConversation() {
  const request = ++conversationRequest, ticket = activeTicket;
  try {
    const messages = await api('/api/admin/tickets/' + ticket.requestId + '/conversation');
    if (request !== conversationRequest || activeTicket?.requestId !== ticket.requestId) return;
    const container = $('ticket-conversation'); container.replaceChildren();
    for (const message of messages) {
      const row = node('div', undefined, 'detail-row');
      row.append(node('span', date(message.createdAt) + (message.isPublic ? ' · Публичный ответ' : ' · Внутренний комментарий') + ' · ' + (ticketStates[message.state] || message.state), 'meta'),
        node('p', message.content, 'ticket-description'));
      if (message.error) row.append(node('p', message.error, 'signal-error'));
      container.append(row);
    }
    if (!messages.length) container.append(node('p', 'Сообщений пока нет.', 'muted'));
  } catch (error) { if (request === conversationRequest) status('ticket-action-status', error.message, 'error'); }
}
async function openTicket(ticket) {
  activeTicket = ticket; replyId = null;
  $('ticket-dialog-title').textContent = ticket.title;
  $('ticket-dialog-description').textContent = ticket.description;
  $('ticket-conversation').replaceChildren(); $('ticket-reply').value = ''; $('ticket-reply-public').checked = true;
  status('ticket-action-status', ''); $('ticket-dialog').showModal(); await loadConversation();
}
async function ticketAction(target) {
  if (!activeTicket || !confirm(target === 'completed' ? 'Отметить заявку выполненной и отправить статус в Okdesk?' : 'Отменить заявку и отправить статус в Okdesk?')) return;
  const button = $(target === 'completed' ? 'ticket-complete' : 'ticket-cancel'); button.disabled = true;
  try {
    await api('/api/admin/tickets/' + activeTicket.requestId + '/action', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:crypto.randomUUID(),target})});
    status('ticket-action-status', 'Действие сохранено и поставлено в очередь отправки в Okdesk.', 'ok'); await loadTickets();
  } catch (error) { status('ticket-action-status', error.message, 'error'); }
  finally { button.disabled = false; }
}

async function loadPackages() {
  try {
    const policy = await api('/api/admin/process-policy');
    $('process-policy-names').value = policy.allowedNames.join('\n');
    $('process-policy-publishers').value = policy.allowedPublishers.join('\n');
    const packages = await api('/api/admin/packages');
    const body = $('package-rows'); body.replaceChildren();
    for (const item of packages) {
      const row = node('tr');
      row.append(node('td', item.key), node('td', item.version), node('td', {windows:'Windows 10/11',win7:'Windows 7',any:'Любая'}[item.platform] || item.platform),
        node('td', (item.sizeBytes / 1048576).toFixed(1) + ' МБ'), node('td', item.sha256, 'hash'),
        node('td', item.latest ? 'Актуальная' : 'Архив', item.latest ? 'signal-ok' : 'muted'));
      const actions = node('td');
      if (!item.latest) {
        const button = node('button', 'Сделать актуальной', 'secondary compact'); button.type = 'button';
        button.addEventListener('click', async () => {
          button.disabled = true;
          try { await api('/api/admin/packages/' + item.id + '/activate', { method: 'POST' }); await loadPackages(); }
          catch (error) { status('package-status', error.message, 'error'); button.disabled = false; }
        });
        actions.append(button);
      }
      row.append(actions); body.append(row);
    }
    $('package-empty').classList.toggle('hidden', packages.length !== 0);
    $('package-count').textContent = 'Всего ' + packages.length;
  } catch (error) { status('package-status', error.message, 'error'); }
}

async function uploadPackage(event) {
  event.preventDefault();
  const form = event.currentTarget, button = form.querySelector('button[type="submit"]');
  button.disabled = true; status('package-status', 'Загружаю файл…');
  try {
    const response = await fetch('/api/admin/packages', { method: 'POST', credentials: 'same-origin',
      headers: { 'X-CSRF-TOKEN': csrf }, body: new FormData(form) });
    if (response.status === 401) { location.assign('/login'); return; }
    const result = await response.json();
    if (response.status === 409) { await loadPackages(); status('package-status', result.error || 'Такая версия уже загружена.', 'error'); return; }
    if (!response.ok) throw Error(result.error || 'Ошибка загрузки.');
    status('package-status', `Загружено: ${result.key} ${result.version}. SHA-256: ${result.sha256}`, 'ok');
    form.reset(); await loadPackages();
  } catch (error) { status('package-status', error.message, 'error'); }
  finally { button.disabled = false; }
}

for (const button of document.querySelectorAll('[data-view]')) button.addEventListener('click', () => showView(button.dataset.view));
$('process-policy-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = event.submitter; if (button) button.disabled = true;
  const lines = id => $(id).value.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
  try {
    await api('/api/admin/process-policy', {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({allowedNames:lines('process-policy-names'),allowedPublishers:lines('process-policy-publishers')})});
    status('process-policy-status', 'Список сохранён.', 'ok');
  } catch (error) { status('process-policy-status', error.message, 'error'); }
  finally { if (button) button.disabled = false; }
});
$('ticket-dialog-close').addEventListener('click', () => $('ticket-dialog').close());
$('ticket-complete').addEventListener('click', () => ticketAction('completed'));
$('ticket-cancel').addEventListener('click', () => ticketAction('cancelled'));
$('ticket-reply-form').addEventListener('submit', async event => {
  event.preventDefault(); if (!activeTicket) return;
  const content = $('ticket-reply').value.trim(); if (!content) return;
  const button = event.submitter; if (button) button.disabled = true;
  replyId ||= crypto.randomUUID();
  try {
    await api('/api/admin/tickets/' + activeTicket.requestId + '/reply', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:replyId,content,isPublic:$('ticket-reply-public').checked})});
    replyId = null; $('ticket-reply').value = '';
    status('ticket-action-status', 'Ответ сохранён. Отправка в Okdesk выполняется в фоне.', 'ok'); await loadConversation();
  } catch (error) { status('ticket-action-status', error.message, 'error'); }
  finally { if (button) button.disabled = false; }
});
$('refresh-devices').addEventListener('click', async () => { await loadOverview(); await refreshDeviceLists(); });
$('refresh-monitoring').addEventListener('click', async () => { await loadOverview(); await loadMonitoring(); });
$('refresh-tickets').addEventListener('click', loadTickets);
async function refreshDeviceLists() { await loadDevices(); }
let searchTimer;
$('device-search').addEventListener('input', () => {
  clearDeviceSelection();
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => { state.devicePage = 0; refreshDeviceLists(); }, 300);
});
$('device-company').addEventListener('change', () => { fillSites('device', true); state.devicePage = 0; clearDeviceSelection(); refreshDeviceLists(); });
$('device-site').addEventListener('change', () => { state.devicePage = 0; clearDeviceSelection(); refreshDeviceLists(); });
$('device-prev').addEventListener('click', () => { state.devicePage--; loadDevices(); });
$('device-next').addEventListener('click', () => { state.devicePage++; loadDevices(); });
for (const id of ['monitor-days', 'monitor-category', 'monitor-site', 'monitor-severity']) $(id).addEventListener('change', () => { state.monitorPage = 0; loadMonitoring(); });
$('monitor-company').addEventListener('change', () => { fillSites('monitor', true); state.monitorPage = 0; loadMonitoring(); });
$('monitor-prev').addEventListener('click', () => { state.monitorPage--; loadMonitoring(); });
$('monitor-next').addEventListener('click', () => { state.monitorPage++; loadMonitoring(); });
for (const id of ['ticket-status-filter', 'ticket-site']) $(id).addEventListener('change', () => { state.ticketPage = 0; loadTickets(); });
$('ticket-company').addEventListener('change', () => { fillSites('ticket', true); state.ticketPage = 0; loadTickets(); });
$('ticket-prev').addEventListener('click', () => { state.ticketPage--; loadTickets(); });
$('ticket-next').addEventListener('click', () => { state.ticketPage++; loadTickets(); });
$('sync-okdesk').addEventListener('click', async event => {
  const button = event.currentTarget; button.disabled = true;
  status('device-status', 'Синхронизирую компании и объекты Okdesk…');
  try {
    const result = await api('/api/admin/okdesk/sync', { method: 'POST' });
    await Promise.all([loadCatalog(), loadOverview()]);
    await refreshDeviceLists();
    status('device-status', `Обновлено: ${result.companies} компаний, ${result.sites} объектов.`, 'ok');
  } catch (error) { status('device-status', error.message, 'error'); }
  finally { button.disabled = false; }
});
$('package-form').addEventListener('submit', uploadPackage);

Promise.all([loadCatalog(), loadOverview()]).then(refreshDeviceLists).catch(error => status('device-status', error.message, 'error'));
