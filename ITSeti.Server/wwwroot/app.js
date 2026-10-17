const csrf = document.querySelector('meta[name="csrf-token"]').content;
const $ = id => document.getElementById(id);
const state = { companies: [], sites: [], devices: [], deviceOptions: [], selectedDevice: null, devicePage: 0,
  monitorPage: 0, applicationErrorPage: 0, applicationErrorRequest: 0, ticketPage: 0, deviceRequest: 0, deviceOptionsRequest: 0, monitorRequest: 0, ticketRequest: 0, runRequest: 0, selectionRequest: 0, view: 'computers' };
const navigationFilters = ['device-search', 'device-company', 'device-site', 'monitor-days', 'monitor-company',
  'monitor-search', 'monitor-site', 'monitor-severity', 'monitor-state', 'app-errors-days', 'app-errors-company', 'app-errors-site',
  'ticket-status-filter', 'ticket-company', 'ticket-site', 'connections-days'];
let navigationReady = false, restoringNavigation = false, navigationDialog = null, navigationRevision = 0;

function navigationSnapshot() {
  return { view: state.view, device: state.selectedDevice, runId: state.selectedRunId || null,
    pages: [state.devicePage, state.monitorPage, state.applicationErrorPage, state.ticketPage],
    filters: Object.fromEntries(navigationFilters.map(id => [id, $(id)?.value || ''])),
    kinds: [...document.querySelectorAll('[data-monitor-kind]')].filter(item => item.checked).map(item => item.dataset.monitorKind),
    dialog: navigationDialog };
}

function rememberNavigation(replace = false) {
  if (!navigationReady || restoringNavigation || typeof window === 'undefined') return;
  const next = navigationSnapshot(), current = window.history.state?.itseti;
  if (JSON.stringify(next) === JSON.stringify(current)) return;
  const url = '#' + next.view + (next.device && next.view === 'computers' ? '/pc/' + next.device.id : '');
  if (replace) window.history.replaceState({ itseti: next }, '', url);
  else window.history.pushState({ itseti: next }, '', url);
}

async function restoreNavigation(saved) {
  if (!saved || !['computers', 'monitoring', 'connections', 'application-errors', 'tickets', 'packages'].includes(saved.view)) return;
  const revision = ++navigationRevision;
  restoringNavigation = true;
  try {
    for (const id of ['ticket-dialog', 'telemetry-dialog']) if ($(id).open) $(id).close();
    navigationDialog = null;
    clearDeviceSelection();
    for (const id of navigationFilters.filter(id => !id.endsWith('-site'))) if ($(id)) $(id).value = saved.filters?.[id] || '';
    for (const prefix of ['device', 'monitor', 'app-errors', 'ticket']) {
      fillSites(prefix, true);
      if ($(prefix + '-site')) $(prefix + '-site').value = saved.filters?.[prefix + '-site'] || '';
    }
    for (const item of document.querySelectorAll('[data-monitor-kind]')) item.checked = saved.kinds?.includes(item.dataset.monitorKind) ?? true;
    [state.devicePage, state.monitorPage, state.applicationErrorPage, state.ticketPage] = (saved.pages || [0,0,0,0]).map(page => Math.max(0, Number(page) || 0));
    for (const id of navigationFilters) if ($(id)?.tagName === 'SELECT') refreshSearchSelect($(id));
    showView(saved.view, false);
    if (saved.view === 'computers') {
      await loadDevices();
      if (revision !== navigationRevision) return;
      if (saved.device) await selectDevice(saved.device, saved.runId, false);
    }
    if (revision !== navigationRevision) return;
    if (saved.dialog?.kind === 'ticket') await openTicket(saved.dialog.ticket);
    if (saved.dialog?.kind === 'telemetry') showTelemetryChart(saved.dialog.history, saved.dialog.category, saved.dialog.label);
  } finally { if (revision === navigationRevision) restoringNavigation = false; }
}

function closeNavigationDialog(id) {
  $(id).close(); navigationDialog = null;
  if (typeof window !== 'undefined' && window.history.state?.itseti?.dialog) window.history.back();
  else rememberNavigation(true);
}

async function requestApi(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.method && options.method !== 'GET') headers.set('X-CSRF-TOKEN', csrf);
  let response;
  try {
    response = await fetch(path, { credentials: 'same-origin', ...options, headers,
      signal: options.signal || AbortSignal.timeout(30000) });
  } catch (error) {
    if (error?.name === 'TimeoutError' || error?.name === 'AbortError') throw Error('Сервер не ответил за 30 секунд. Повторите запрос.');
    throw Error('Не удалось связаться с сервером. Проверьте подключение и повторите запрос.');
  }
  if (response.status === 401) { location.assign('/login'); throw Error('Сеанс завершён.'); }
  if (response.redirected && new URL(response.url).pathname === '/login') {
    location.assign('/login'); throw Error('Сеанс завершён.');
  }
  if (response.status === 204) return null;
  const contentType = response.headers.get('content-type') || '';
  const text = await response.text();
  let result = null;
  if (contentType.includes('json')) {
    try { result = JSON.parse(text); } catch { throw Error(`Сервер вернул некорректный JSON (HTTP ${response.status}).`); }
  } else if (response.ok) {
    throw Error('Сервер вернул HTML вместо данных. Обновите страницу и войдите снова.');
  }
  if (!response.ok) throw Error(result?.error || `Ошибка сервера (HTTP ${response.status}).`);
  return result;
}

async function api(path, options = {}) {
  try { return await requestApi(path, options); }
  catch (error) {
    if (!options.signal?.aborted) notifyError(error.message || 'Не удалось связаться с сервером.');
    throw error;
  }
}

function notifyError(message) {
  const region = $('notifications');
  if (!region) return;
  const text = String(message || 'Не удалось выполнить действие.');
  // Repeated failures from concurrent requests share one notification.
  const existing = [...region.children].find(item => item.dataset.message === text);
  if (existing) return;
  const toast = node('section', undefined, 'notification error');
  toast.dataset.message = text; toast.setAttribute('role', 'alert');
  const body = node('div'); body.append(node('strong', 'Ошибка'), node('p', text));
  const close = node('button', '×', 'notification-close'); close.type = 'button';
  close.setAttribute('aria-label', 'Закрыть уведомление');
  close.addEventListener('click', () => toast.remove());
  toast.append(body, close);
  while (region.children.length >= 4) region.children[0].remove();
  region.append(toast);
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
  if (kind === 'error' && message) notifyError(message);
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

function sensorHistoryValues(history, category) {
  const samples = list(history);
  let sensorName = null;
  if (category !== 'CpuLoad' && category !== 'MemoryLoad') {
    const counts = new Map();
    for (const sample of samples) for (const sensor of list(property(sample, 'sensors'))) {
      if (property(sensor, 'category') !== category) continue;
      const name = property(sensor, 'name');
      if (name) counts.set(name, (counts.get(name) || 0) + 1);
    }
    sensorName = [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] || null;
  }
  return samples.flatMap(sample => {
    const capturedAt = property(sample, 'capturedAtUtc');
    let value;
    if (category === 'CpuLoad') value = property(sample, 'cpuLoadPercent');
    else if (category === 'MemoryLoad') value = property(sample, 'memoryLoadPercent');
    else value = list(property(sample, 'sensors')).find(sensor => property(sensor, 'category') === category &&
      property(sensor, 'name') === sensorName);
    const sensor = value && typeof value === 'object' ? value : null;
    if (sensor) value = property(sensor, 'value');
    if (value == null || value === '' || typeof value === 'boolean') return [];
    const numeric = Number(value);
    return capturedAt && Number.isFinite(numeric) ? [{ capturedAt: new Date(capturedAt), value: numeric, sensorName }] : [];
  }).filter(point => Number.isFinite(point.capturedAt.getTime())).sort((a, b) => a.capturedAt - b.capturedAt);
}

function showTelemetryChart(history, category, label) {
  const points = sensorHistoryValues(history, category);
  if (!points.length) return;
  $('telemetry-title').textContent = label;
  $('telemetry-subtitle').textContent = `${points[0].sensorName ? points[0].sensorName + ' · ' : ''}${date(points[0].capturedAt)} — ${date(points.at(-1).capturedAt)} · ${points.length} измерений за последние 5 минут`;
  const chart = $('telemetry-chart'); chart.replaceChildren();
  const width = 760, height = 280, left = 52, right = 18, top = 16, bottom = 42;
  const min = Math.min(...points.map(point => point.value)), max = Math.max(...points.map(point => point.value));
  const padding = Math.max((max - min) * .12, Math.abs(max || min) * .025, .05);
  const low = min - padding, high = max + padding;
  const x = index => left + (points.length < 2 ? .5 : index / (points.length - 1)) * (width - left - right);
  const y = value => top + (high - value) / (high - low) * (height - top - bottom);
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', `0 0 ${width} ${height}`); svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', `График ${label}`); svg.classList.add('telemetry-svg');
  for (let tick = 0; tick < 4; tick++) {
    const value = high - (high - low) * tick / 3, yy = y(value);
    const line = document.createElementNS(svg.namespaceURI, 'line');
    line.setAttribute('x1', String(left)); line.setAttribute('x2', String(width - right));
    line.setAttribute('y1', String(yy)); line.setAttribute('y2', String(yy)); line.classList.add('telemetry-grid'); svg.append(line);
    const text = document.createElementNS(svg.namespaceURI, 'text');
    text.setAttribute('x', String(left - 8)); text.setAttribute('y', String(yy + 4)); text.setAttribute('text-anchor', 'end');
    text.textContent = value.toFixed(Math.abs(high - low) < 2 ? 2 : 0); text.classList.add('telemetry-axis'); svg.append(text);
  }
  if (points.length > 1) {
    const polyline = document.createElementNS(svg.namespaceURI, 'polyline');
    polyline.setAttribute('points', points.map((point, index) => `${x(index)},${y(point.value)}`).join(' '));
    polyline.classList.add('telemetry-line'); svg.append(polyline);
  }
  for (const [index, point] of points.entries()) {
    const circle = document.createElementNS(svg.namespaceURI, 'circle');
    circle.setAttribute('cx', String(x(index))); circle.setAttribute('cy', String(y(point.value))); circle.setAttribute('r', '3');
    circle.classList.add('telemetry-point');
    const title = document.createElementNS(svg.namespaceURI, 'title');
    title.textContent = `${date(point.capturedAt)} · ${point.value.toFixed(2)}`; circle.append(title); svg.append(circle);
  }
  for (const [index, point] of [points[0], points.at(-1)].entries()) {
    const text = document.createElementNS(svg.namespaceURI, 'text');
    text.setAttribute('x', String(index === 0 ? left : width - right)); text.setAttribute('y', String(height - 12));
    text.setAttribute('text-anchor', index === 0 ? 'start' : 'end'); text.textContent = point.capturedAt.toLocaleTimeString('ru-RU');
    text.classList.add('telemetry-axis'); svg.append(text);
  }
  chart.append(svg);
  const average = points.reduce((sum, point) => sum + point.value, 0) / points.length;
  const suffix = category.includes('Voltage') || category.startsWith('Rail') ? ' В' : category.includes('Temperature') ? ' °C' : '%';
  $('telemetry-stats').replaceChildren(
    node('span', `Мин. ${min.toFixed(2)}${suffix}`), node('span', `Сред. ${average.toFixed(2)}${suffix}`), node('span', `Макс. ${max.toFixed(2)}${suffix}`));
  $('telemetry-dialog').showModal();
}

function telemetryButton(history, category, label) {
  if (sensorHistoryValues(history, category).length < 2) return null;
  const button = node('button', 'График · ' + label, 'secondary compact telemetry-button');
  button.type = 'button'; button.addEventListener('click', event => {
    event.stopPropagation(); showTelemetryChart(history, category, label);
  });
  return button;
}

function showView(view, remember = true) {
  state.view = view;
  $('page-title').textContent = { computers: 'Компьютеры', monitoring: 'Мониторинг', connections:'Новые подключения', 'application-errors': 'Ошибки приложения', tickets: 'Заявки', packages: 'ПО и обновления' }[view];
  for (const item of document.querySelectorAll('.nav-item')) {
    const selected = item.dataset.view === view;
    item.classList.toggle('active', selected);
    if (selected) item.setAttribute('aria-current', 'page'); else item.removeAttribute('aria-current');
  }
  for (const item of document.querySelectorAll('.view')) item.classList.toggle('active', item.id === 'view-' + view);
  if (view === 'monitoring') loadMonitoring();
  if (view === 'connections') loadConnections();
  if (view === 'application-errors') loadApplicationErrors();
  if (view === 'tickets') loadTickets();
  if (view === 'packages') loadPackages();
  if (remember) rememberNavigation();
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
  if (!select.id.endsWith("-company") && !select.id.endsWith("-site")) return;
  // Keep the native control as the form value; the popup only provides selection and search.
  if (!select.searchInput) {
    const wrapper = node('div', undefined, 'search-select');
    const input = node('input', undefined, 'field'), toggle = node('button', '▾', 'select-toggle');
    const popup = node('div', undefined, 'select-popup');
    input.id = select.id + '-search'; input.type = 'text'; input.autocomplete = 'off';
    popup.id = select.id + '-choices'; popup.hidden = true; popup.setAttribute('role', 'listbox');
    input.setAttribute('role', 'combobox'); input.setAttribute('aria-expanded', 'false');
    input.setAttribute('aria-controls', popup.id); input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-label', select.parentElement.querySelector('span')?.textContent || 'Выбор');
    toggle.type = 'button'; toggle.setAttribute('aria-label', 'Открыть список'); toggle.tabIndex = -1;
    select.hidden = true; select.after(wrapper); wrapper.append(input, toggle, popup);
    let active = -1;
    const close = () => { popup.hidden = true; input.setAttribute('aria-expanded', 'false'); input.value = select.selectedOptions[0]?.textContent || ''; };
    const choose = option => { select.value = option.value; close(); select.dispatchEvent(new Event('change', { bubbles: true })); };
    const open = (query = '') => {
      if (select.disabled) return;
      popup.replaceChildren(); active = -1;
      for (const option of [...select.options].filter(option => !option.disabled && option.textContent.toLocaleLowerCase().includes(query.toLocaleLowerCase()))) {
        const button = node('button', option.textContent, 'select-option'); button.type = 'button'; button.tabIndex = -1;
        button.setAttribute('role', 'option'); button.setAttribute('aria-selected', String(option.value === select.value));
        button.addEventListener('mousedown', event => event.preventDefault());
        button.addEventListener('click', () => choose(option)); popup.append(button);
      }
      if (!popup.children.length) popup.append(node('p', 'Ничего не найдено', 'meta'));
      popup.hidden = false; input.setAttribute('aria-expanded', 'true');
    };
    input.addEventListener('focus', () => { input.select(); open(); });
    input.addEventListener('click', () => { if (popup.hidden) open(); });
    input.addEventListener('input', () => open(input.value));
    input.addEventListener('blur', event => { if (!wrapper.contains(event.relatedTarget)) close(); });
    toggle.addEventListener('mousedown', event => event.preventDefault());
    toggle.addEventListener('click', () => { if (popup.hidden) { input.focus(); open(); } else close(); });
    document.addEventListener('pointerdown', event => { if (!wrapper.contains(event.target)) close(); });
    input.addEventListener('keydown', event => {
      if (event.key === 'Escape') { close(); return; }
      if (event.key === 'Tab') { close(); return; }
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault(); if (popup.hidden) open();
        const options = [...popup.children].filter(item => item.tagName === 'BUTTON');
        if (!options.length) return;
        active = (active + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length;
        options.forEach((item, index) => item.classList.toggle('active', index === active));
        options[active].scrollIntoView({ block: 'nearest' });
      } else if (event.key === 'Enter' && !popup.hidden) {
        event.preventDefault(); const options = [...popup.children].filter(item => item.tagName === 'BUTTON');
        if (options[active >= 0 ? active : 0]) options[active >= 0 ? active : 0].click();
      }
    });
    select.searchInput = input; select.searchChoices = popup; select.searchToggle = toggle;
    select.addEventListener('change', () => refreshSearchSelect(select));
  }
  select.searchInput.value = select.selectedOptions[0]?.textContent || '';
  select.searchInput.disabled = select.disabled; select.searchToggle.disabled = select.disabled;
}

function remoteAccessLine(device) {
  const saved = state.remoteAccess?.[device.deviceId || device.id] || {};
  const rms = saved.rms || device.rms, anyDesk = saved.anyDesk || device.anyDesk;
  const line = node('div', undefined, 'remote-access');
  line.addEventListener('click', event => event.stopPropagation());
  if (rms) line.append(node('span', 'RMS ' + rms));
  if (anyDesk) line.append(node('span', 'AnyDesk ' + anyDesk));
  return line;
}

async function loadCatalog() {
  const [catalog, access] = await Promise.all([api('/api/admin/catalog'), api('/api/admin/remote-access')]);
  state.remoteAccess = Object.fromEntries((access.items || []).map(item => [item.deviceId,item]));
  state.companies = catalog.companies || [];
  state.sites = catalog.sites || [];
  for (const prefix of ['device', 'monitor', 'ticket', 'app-errors']) {
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
      const deviceTable = document.querySelector('.device-table');
      deviceTable.classList.add('tree-view');
      deviceTable.classList.remove('computer-view');
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
          rememberNavigation();
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
    const deviceTable = document.querySelector('.device-table');
    deviceTable.classList.add('computer-view');
    deviceTable.classList.remove('tree-view');
    $('computer-list-title').textContent = 'Компьютеры';
    $('computer-columns').replaceChildren(node('th', 'PC №'), node('th', 'Состояние'), node('th', 'Привязка Okdesk'), node('th', 'Последняя проверка'),node('th','Последняя связь'));
    const result = await api('/api/admin/devices?' + deviceParameters());
    if (requestId !== state.deviceRequest) return;
    const external = await api('/api/admin/okdesk/unbound-equipment?' + deviceParameters());
    if (requestId !== state.deviceRequest) return;
    state.devices = [...(result.items || []), ...(external.items || [])];
    const body = $('device-rows'); body.replaceChildren();
    for (const device of state.devices) {
      const row = node('tr', undefined, device.equipmentOnly ? 'external-device' : 'clickable');
      if (!device.equipmentOnly) row.tabIndex = 0;
      row.classList.toggle('selected', state.selectedDevice?.id === device.id);
      const serialLabel = device.inventory ? 'PC № ' + device.inventory : device.computer || device.computerName || 'PC № не указан';
      const serial = node('td'); serial.append(node('span', serialLabel, 'cell-main'));
      serial.append(remoteAccessLine(device));
      if (device.computer) serial.append(node('span',device.computer,'meta'));
      const health = node('td', device.equipmentOnly ? 'Нет клиента' : ({normal:'Норма',warning:'Требует внимания',critical:'Критическое',unknown:'Нет проверки'}[device.health] || 'Нет проверки'),
        {normal:'signal-ok',warning:'signal-warn',critical:'signal-error'}[device.health] || 'muted');
      if (device.remoteAttention) health.append(node('span', 'Нет RMS/OCS', 'meta signal-warn'));
      const bindingText = {matched:'Совпадает',other_object:'Другой объект',missing_in_okdesk:'Нет в Okdesk',ambiguous:'Несколько совпадений',okdesk_only:'Okdesk · клиент не подключён'}[device.okdeskStatus] || 'Не сверено';
      const binding = node('td', bindingText, device.okdeskStatus === 'matched' ? 'signal-ok' : 'signal-warn');
      row.append(serial, health, binding, node('td', date(device.lastCheck)),node('td',date(device.lastSeen)));
      if (!device.equipmentOnly) {
        row.addEventListener('click', () => selectDevice(device));
        row.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); selectDevice(device); } });
      }
      body.append(row);
    }
    $('device-empty').classList.toggle('hidden', state.devices.length !== 0);
    $('device-count').textContent = state.devices.length ? `Показано ${state.devices.length}` : '';
    $('device-page').textContent = 'Страница ' + (state.devicePage + 1);
    $('device-prev').disabled = state.devicePage === 0;
    $('device-next').disabled = !(result.hasMore || external.hasMore);
    status('device-status', '');
  } catch (error) { if (requestId === state.deviceRequest) status('device-status', error.message, 'error'); }
}

async function selectDevice(device, requestedRunId = null, remember = true) {
  if (!device || device.equipmentOnly || !device.id) return;
  const requestId = ++state.selectionRequest;
  ++state.runRequest;
  state.selectedDevice = device;
  state.selectedRunId = requestedRunId;
  showView('computers', false);
  if (remember) rememberNavigation();
  for (const row of $('device-rows').rows) row.classList.remove('selected');
  const selectedIndex = state.devices.findIndex(item => item.id === device.id);
  if (selectedIndex >= 0) $('device-rows').rows[selectedIndex].classList.add('selected');
  $('selected-title').textContent = device.inventory ? 'PC № ' + device.inventory : 'PC № не указан';
  $('selected-access').replaceChildren(remoteAccessLine(device));
  $('selected-meta').textContent = [device.computer || device.computerName,
    device.company, device.site, device.os, device.osVersion].filter(Boolean).join(' · ');
  const blockButton = $('ticket-block-toggle');
  blockButton.disabled = false;
  blockButton.textContent = device.ticketsBlocked ? 'Разрешить заявки' : 'Заблокировать заявки';
  blockButton.classList.toggle('danger', !device.ticketsBlocked);
  $('run-list').replaceChildren(); $('run-detail').replaceChildren();
  $('device-profile').replaceChildren(); $('run-tabs').replaceChildren();
  try {
    const runs = await api('/api/admin/devices/' + device.id + '/runs');
    if (requestId !== state.selectionRequest) return;
    if (!runs.length) { $('run-detail').append(node('p', 'Проверок пока нет.', 'detail-empty')); return; }
    const selectedRun = runs.find(run => run.id === requestedRunId) || runs.find(run => !isMonitoringRun(run)) || runs[0];
    renderRunNavigation(runs, isMonitoringRun(selectedRun));
    if (!isMonitoringRun(selectedRun)) await selectRun(selectedRun, false);
    if (requestId === state.selectionRequest) rememberNavigation(true);
    $('selected-title').scrollIntoView({ behavior: 'smooth', block: 'start' });
  } catch (error) { if (requestId === state.selectionRequest) $('run-detail').replaceChildren(node('p', error.message, 'signal-error')); }
}

function isMonitoringRun(run) { return String(run.kind || '').toLowerCase().includes('мониторинг'); }

function renderRunNavigation(runs, monitoringSelected) {
  $('run-tabs').replaceChildren(); $('run-list').replaceChildren();
  for (const [monitoring, label] of [[false, 'Проверки'], [true, 'Мониторинг']]) {
    const values = runs.filter(run => isMonitoringRun(run) === monitoring);
    const tab = node('button', `${label} · ${values.length}`, 'run-tab');
    tab.type = 'button'; tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-selected', String(monitoring === monitoringSelected)); tab.disabled = values.length === 0;
    tab.addEventListener('click', () => { renderRunNavigation(runs, monitoring); if (!monitoring && values.length) selectRun(values[0]); });
    $('run-tabs').append(tab);
  }
  if (monitoringSelected) { loadDeviceMonitoring(); return; }
  for (const run of runs.filter(run => !isMonitoringRun(run))) {
      const button = node('button', date(run.startedAt) + ' · ' + run.kind + ' · ' +
        (property(run.summary, 'result') || 'Результат сохранён'), 'run-item');
      button.type = 'button'; button.dataset.runId = run.id;
      button.addEventListener('click', () => selectRun(run));
      $('run-list').append(button);
  }
}

async function loadDeviceMonitoring() {
  const deviceId = state.selectedDevice?.id;
  if (!deviceId) return;
  const request = ++state.runRequest;
  const root = $('run-list'); root.replaceChildren(node('p', 'Загружаю причины…', 'meta'));
  $('run-detail').replaceChildren();
  try {
    const result = await api(`/api/admin/devices/${deviceId}/monitoring-issues`);
    if (request !== state.runRequest || deviceId !== state.selectedDevice?.id) return;
    root.replaceChildren();
    for (const issue of result.items || []) {
      const group = node('details', undefined, 'issue-history ' + (issue.severity === 1 ? 'critical' : 'warning'));
      const summary = node('summary');
      summary.append(node('strong', issue.title), node('span', issue.detail),
        node('small', `${issue.occurrences} событий · последнее ${date(issue.detectedAt)}`));
      group.append(summary);
      const history = node('div', undefined, 'issue-history-events'); group.append(history);
      let page = 0, loading = false, loaded = false;
      const more = node('button', 'Загрузить ещё', 'secondary compact'); more.type = 'button';
      const load = async () => {
        if (loading) return; loading = true; more.disabled = true;
        try {
          const params = new URLSearchParams({ category: issue.category, identity: issue.identity, page: String(page) });
          const events = await api(`/api/admin/devices/${deviceId}/monitoring-issues/history?${params}`);
          more.remove();
          for (const item of events.items || []) {
            const row = node('div', undefined, 'issue-history-event');
            row.append(node('span', date(item.detectedAt)), node('span', item.detail));
            const open = node('button', 'Все данные события', 'secondary compact'); open.type = 'button';
            open.addEventListener('click', () => selectRun({ id: item.runId })); row.append(open); history.append(row);
          }
          page++; loaded = true; if (events.hasMore) history.append(more);
        } catch (error) { history.append(node('p', error.message, 'signal-error')); history.append(more); }
        finally { loading = false; more.disabled = false; }
      };
      more.addEventListener('click', load); group.addEventListener('toggle', () => { if (group.open && !loaded) load(); });
      root.append(group);
    }
    if (!result.items?.length) root.append(node('p', 'Событий мониторинга пока нет.', 'meta'));
  } catch (error) { if (request === state.runRequest) root.replaceChildren(node('p', error.message, 'signal-error')); }
}

function clearDeviceSelection() {
  ++state.selectionRequest;
  ++state.runRequest;
  state.selectedDevice = null;
  state.selectedRunId = null;
  $('selected-title').textContent = 'Выберите ПК';
  $('selected-meta').textContent = '';
  $('selected-access').replaceChildren();
  $('ticket-block-toggle').disabled = true;
  $('ticket-block-toggle').textContent = 'Заявки ПК';
  $('ticket-block-toggle').classList.remove('danger');
  $('run-list').replaceChildren();
  $('device-profile').replaceChildren(); $('run-tabs').replaceChildren();
  $('run-detail').replaceChildren();
  for (const row of $('device-rows').rows) row.classList.remove('selected');
}

async function selectRun(run, remember = true) {
  const requestId = ++state.runRequest;
  state.selectedRunId = run.id;
  if (remember) rememberNavigation();
  for (const button of $('run-list').children) button.classList.toggle('active', button.dataset.runId === run.id);
  $('run-detail').replaceChildren(node('p', 'Загружаю проверку…', 'detail-empty'));
  try {
    const result = await api('/api/admin/runs/' + run.id);
    if (requestId === state.runRequest) renderRun(result);
  }
  catch (error) { if (requestId === state.runRequest) $('run-detail').replaceChildren(node('p', error.message, 'signal-error')); }
}

async function allowRunProcess(process, button) {
  button.disabled = true;
  button.title = 'Сохраняю правило…';
  try {
    await api('/api/admin/process-policy/allow', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: property(process, 'name', 'processName'),
        publisher: property(process, 'publisher', 'signer'), signature: property(process, 'signature') }) });
    button.textContent = '✓';
    button.classList.add('allowed');
    button.title = 'Процесс разрешён на сервере';
    button.setAttribute('aria-label', button.title);
  } catch (error) {
    button.disabled = false;
    button.title = 'Не удалось разрешить процесс: ' + error.message;
    button.setAttribute('aria-label', button.title);
    status('device-status', button.title, 'error');
  }
}


function detailSection(root, title, values, emptyText, collapsed = false) {
  if (!values.length) return;
  const container = collapsed ? node('details', undefined, 'report-section') : root;
  container.append(node(collapsed ? 'summary' : 'h3', title + (collapsed ? ' · ' + values.length : '')));
  if (!values.length) container.append(node('p', emptyText, 'detail-empty'));
  else for (const value of values) {
    const row = node('div', value.element ? undefined : value.text, value.className || 'detail-row');
    if (value.element) row.append(value.element);
    container.append(row);
  }
  if (collapsed) root.append(container);
}

function eventIdentity(event) {
  const provider = property(event, 'provider', 'source') || '';
  const message = property(event, 'message') || '';
  const bugcheck = String(provider).toLowerCase().includes('kernel-power') && String(property(event, 'id')) === '41'
    ? (String(message).match(/\bBugcheckCode\s*[:=]\s*(\d+)\b/i)?.[1] || '') : '';
  return [property(event, 'log'), provider, property(event, 'id'), property(event, 'level') || 2, bugcheck].join('|').toLowerCase();
}

function profileDetailRow(label, currentValue, savedValue, savedAt) {
  const current = currentValue != null && String(currentValue).trim() !== '' && String(currentValue) !== 'нет данных';
  const value = current ? currentValue : savedValue;
  if (value == null || String(value).trim() === '' || String(value) === 'нет данных') return null;
  const stale = !current && (!savedAt || Date.now() - new Date(savedAt).getTime() > 24 * 60 * 60 * 1000);
  return { text: `${label}: ${value}${current ? '' : ` · сохранено ${date(savedAt)}`}`,
    className: `detail-row ${current ? '' : stale ? 'stale-data' : 'profile-data'}` };
}

function telemetryDetailRow(label, value, history, category) {
  const row = node('div', undefined, 'sensor-detail-row');
  row.append(node('span', `${label}: ${value}`));
  const button = telemetryButton(history, category, label);
  if (button) row.append(button);
  return { element: row, className: 'detail-row' };
}

function renderRun(run) {
  const root = $('run-detail'); root.replaceChildren();
  renderDeviceProfile(run);
  const report = run.details || {}, full = property(report, 'full') || {};
  const profile = run.hardwareProfile || {};
  const profileUpdatedAt = run.hardwareProfileUpdatedAt || property(profile, 'capturedAtUtc');
  const history = property(report, 'sensorHistory');
  root.append(node('h3', isMonitoringRun(run) ? 'События мониторинга' : 'Результат проверки'));
  root.append(node('span', date(run.startedAt) + ' · ' + run.kind, 'meta'));
  if (property(report, 'trigger')) {
    const origin = 'Запуск: ' + property(report, 'trigger');
    root.append(node('span', origin, 'meta'));
  }
  const nestedHwInfo = property(report, 'hwiNFO', 'hwInfo');
  const legacySensorFields = ['cpuTemperatureC', 'vrmTemperatureC', 'pchTemperatureC', 'rail12V', 'rail5V', 'rail3V']
    .some(name => property(report, name) != null);
  const hwInfo = nestedHwInfo || (legacySensorFields ? {
    source: property(report, 'cpuSource'),
    toolVersion: property(report, 'hwInfoVersion'),
    cpuTemperatureC: property(report, 'cpuTemperatureC'),
    vrmTemperatureC: property(report, 'vrmTemperatureC'),
    pchTemperatureC: property(report, 'pchTemperatureC'),
    rail12V: property(report, 'rail12V'),
    rail5V: property(report, 'rail5V'),
    rail3V: property(report, 'rail3V'),
    cpuLoadPercent: String(property(report, 'cpuSource') || '').includes('HWiNFO') ? property(report, 'cpuPercent') : null
  } : null);
  const stats = node('div', undefined, 'detail-stats');
  const sensorTime = property(hwInfo, 'capturedAtUtc');
  const cpu = property(hwInfo, 'cpuLoadPercent') ?? property(report, 'cpuPercent');
  const temp = property(hwInfo, 'cpuTemperatureC') ?? property(report, 'cpuTemperatureC') ?? property(full, 'cpuTemperatureC');
  const measurements = [
    ['Загрузка CPU', cpu == null ? null : Math.round(Number(cpu)) + '%', 'CpuLoad', property(hwInfo,'cpuLoadPercent') != null ? sensorTime : property(report,'cpuPercentCapturedAtUtc')],
    ['Температура CPU', temperature(temp), 'CpuTemperature', property(hwInfo,'cpuTemperatureC') != null ? sensorTime : property(report,'cpuTemperatureCapturedAtUtc')],
    ['ОЗУ', property(report,'memoryUsedPercent') == null ? null : Math.round(Number(property(report,'memoryUsedPercent'))) + '%', 'MemoryLoad', property(report,'memoryUsedPercentCapturedAtUtc')],
    ...[['VRM','vrmTemperatureC','VrmTemperature'],['PCH','pchTemperatureC','PchTemperature']].map(([label,key,category])=>[label,temperature(property(hwInfo,key)),category,sensorTime]),
    ...[['12 В','rail12V','Rail12V'],['5 В','rail5V','Rail5V'],['3,3 В','rail3V','Rail3V']].map(([label,key,category])=>[label,property(hwInfo,key)==null?null:Number(property(hwInfo,key)).toFixed(2)+' В',category,sensorTime])
  ];
  for (const sensor of list(property(hwInfo,'sensors')).filter(sensor=>property(sensor,'category')==='Voltage')) {
    const name = property(sensor,'name');
    if (!measurements.some(([label])=>label===name)) measurements.push([name,Number(property(sensor,'value')).toFixed(2)+' В',null,sensorTime]);
  }
  for (const [label,value,category,time] of measurements.filter(([,value])=>value && value!=='Нет данных')) {
    const cell=node('div'); cell.title=time?date(time):''; cell.append(node('span',label),node('strong',String(value)),node('small',time && sensorTime && time !== sensorTime ? 'Замер раньше снимка' : ''));
    const graph=telemetryButton(history,category,label); if(graph)cell.append(graph); stats.append(cell);
  }
  if(stats.children.length) { root.append(stats); if(sensorTime && new Date(sensorTime).getTime()!==new Date(run.startedAt).getTime()) root.append(node('p','Замер датчиков · '+date(sensorTime),'meta'));  }
  const issues = list(run.issues || property(report, 'diagnosticIssues'));
  const critical = issues.filter(issue => Number(property(issue, 'severity')) === 1 || property(issue, 'severity') === 'Critical');
  const warnings = issues.filter(issue => Number(property(issue, 'severity')) === 2 || property(issue, 'severity') === 'Warning');
  const issueRows = values => values.map(issue => ({text: [property(issue, 'title'), property(issue, 'detail')].filter(Boolean).join(' · '), className: 'detail-row ' + (critical.includes(issue) ? 'critical' : 'warning')}));
  detailSection(root, 'Критические проблемы', issueRows(critical), 'Критических проблем не найдено.');
  detailSection(root, 'Предупреждения', issueRows(warnings), 'Предупреждений нет.', true);
  const applicationErrors = list(property(report, 'applicationErrors'));
  detailSection(root, 'Ошибки приложения', applicationErrors.map(error => ({
    text: [date(property(error, 'occurredAt')), property(error, 'component'), property(error, 'operation'), property(error, 'summary'),
      property(error, 'id') && 'код ' + property(error, 'id')].filter(Boolean).join(' · '),
    className: 'detail-row critical'
  })), 'Ошибок приложения для этой проверки нет.', applicationErrors.length === 0);
  const previous = run.previous || {}, prevFull = property(previous, 'full') || {};
  const processes = list(run.unknownProcesses || property(full, 'processes') || property(report, 'processes'));
  const oldProcesses = new Set(list(property(prevFull, 'processes') || property(previous, 'processes'))
    .map(process => [property(process, 'name', 'processName'), property(process, 'path')].join('|').toLowerCase()));
  detailSection(root, 'Процессы', processes.slice(0, 100).map(process => {
    const name = property(process, 'name', 'processName') || '';
    const path = property(process, 'path') || '';
    const row = node('div', undefined, 'process-policy-row');
    row.append(node('span', [name, property(process, 'publisher', 'description'), path].filter(Boolean).join(' · '), 'process-policy-text'));
    const allow = node('button', '✓', 'process-allow-check');
    allow.type = 'button'; allow.title = `Разрешить процесс ${name} на сервере`;
    allow.setAttribute('aria-label', allow.title);
    allow.addEventListener('click', () => allowRunProcess(process, allow));
    row.append(allow);
    return { element: row, className: run.previous && !oldProcesses.has([name, path].join('|').toLowerCase()) ? 'detail-row new-item' : 'detail-row' };
  }), 'Данные о процессах не переданы.', true);
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

function renderDeviceProfile(run) {
  const root = $('device-profile'); root.replaceChildren();
  const report = run.details || {}, full = property(report, 'full') || {};
  const profile = run.hardwareProfile || {}, hwInfo = property(report, 'hwInfo', 'hwiNFO') || {};
  const capturedAt = run.hardwareProfileUpdatedAt || property(profile, 'capturedAtUtc');
  const current = !isMonitoringRun(run) && Object.keys(full).length > 0;
  const choose = (value, saved) => current && value != null && value !== '' ? value : saved;
  const facts = [
    ['Компьютер', choose(property(report,'computerName'), property(profile,'computerName'))],
    ['CPU', choose(property(full,'cpuName'), property(profile,'cpuModel'))],
    ['ОЗУ', choose(property(report,'totalMemoryBytes'), property(profile,'totalMemoryBytes')) == null ? null :
      (Number(choose(property(report,'totalMemoryBytes'), property(profile,'totalMemoryBytes'))) / 1073741824).toFixed(1) + ' ГБ'],
    ['Память', choose(property(full,'memoryType'), property(profile,'memoryType'))],
    ['GPU', choose(property(full,'gpuName'), property(profile,'gpuModel'))],
    ['Плата', choose(property(hwInfo,'motherboard'), property(profile,'motherboard'))],
    ['BIOS', choose(property(hwInfo,'biosVersion'), property(profile,'biosVersion'))],
    ['Windows', choose([property(report,'windowsEdition'),property(report,'windowsRelease'),property(report,'windowsBuild')].filter(Boolean).join(' '),
      [property(profile,'windowsEdition'),property(profile,'windowsRelease'),property(profile,'windowsBuild')].filter(Boolean).join(' '))]
  ].filter(([,value]) => value != null && value !== '' && value !== 'нет данных');
  if (!facts.length && !Object.keys(profile).length) return;
  root.append(node('h3','Конфигурация'));
  if (!current && capturedAt) root.append(node('p', `Сохранено ${date(capturedAt)}`, 'meta profile-data'));
  const grid = node('dl', undefined, 'profile-facts');
  for (const [label,value] of facts) { const pair=node('div'); pair.append(node('dt',label),node('dd',String(value))); grid.append(pair); }
  root.append(grid);
  const adapters = current && list(property(report,'networkAdapters')).length ? list(property(report,'networkAdapters')) : list(property(profile,'networkAdapters'));
  detailSection(root,'Сеть',adapters.map(adapter=>({text:[property(adapter,'name'),property(adapter,'addresses','ipAddresses'),property(adapter,'macAddress','mac'),property(adapter,'linkSpeed')].filter(Boolean).join(' · ')})), '', true);
  const storage=node('section',undefined,'storage-panel'); storage.append(node('h3','Накопители'));
  const disks = current && list(property(full,'physicalDisks')).length ? list(property(full,'physicalDisks')) : list(property(profile,'physicalDisks'));
  const smart = list(property(full,'smartDisks')).length ? list(property(full,'smartDisks')) : list(property(profile,'smartDisks'));
  const benchmark = property(full,'benchmark') || property(profile,'benchmark');
  const used=new Set();
  for(const disk of disks) {
    const block=node('div',undefined,'storage-device');
    block.append(node('strong',[property(disk,'model'),property(disk,'mediaType')].filter(Boolean).join(' · ')));
    const matches=smart.filter(item=>String(property(item,'model')||'').trim().toLowerCase()===String(property(disk,'model')||'').trim().toLowerCase());
    block.append(node('p',[property(disk,'health'),property(disk,'powerOnHours')!=null?property(disk,'powerOnHours')+' ч наработки':''].filter(Boolean).join(' · ')));
    for(const item of matches){used.add(item);block.append(node('p',[property(item,'letters'),property(item,'status','health'),property(item,'smartWarnings'),property(item,'transferMode')].filter(Boolean).join(' · ')));}
    storage.append(block);
  }
  for(const disk of smart.filter(item=>!used.has(item)))storage.append(node('p',[property(disk,'letters'),property(disk,'model'),property(disk,'status','health'),property(disk,'smartWarnings'),property(disk,'transferMode')].filter(Boolean).join(' · '),'detail-row'));
  if(smart.length && !current && property(profile,'smartAtUtc') && property(profile,'smartAtUtc')!==capturedAt)storage.append(node('small','SMART · '+date(property(profile,'smartAtUtc')),'meta'));
  for(const disk of list(property(report,'disks'))){
    const free=property(disk,'freeBytes'),total=property(disk,'totalBytes');
    storage.append(node('p',(property(disk,'name','model')||'Раздел')+' · '+(free!=null&&total>0?`${(free/1073741824).toFixed(1)} из ${(total/1073741824).toFixed(1)} ГБ свободно · занято ${Math.round((1-free/total)*100)}%`:'Объём не передан'),'detail-row'));
  }
  if(benchmark)storage.append(node('p',[property(benchmark,'drive'),'DiskSpd',property(benchmark,'read')!=null?`чтение ${Number(property(benchmark,'read')).toFixed(1)} МБ/с`:'',property(benchmark,'write')!=null?`запись ${Number(property(benchmark,'write')).toFixed(1)} МБ/с`:'',!current && property(profile,'benchmarkAtUtc')!==capturedAt ? date(property(profile,'benchmarkAtUtc')) : ''].filter(Boolean).join(' · '),'detail-row'));
  for(const disk of list(property(report,'diskLatencies')))storage.append(node('p',[property(disk,'disk'),property(disk,'readMs')!=null?`отклик чтения ${Number(property(disk,'readMs')).toFixed(1)} мс`:'',property(disk,'writeMs')!=null?`запись ${Number(property(disk,'writeMs')).toFixed(1)} мс`:''].filter(Boolean).join(' · '),'detail-row'));
  if(disks.length||smart.length||benchmark||list(property(report,'disks')).length)root.append(storage);

}

function monitoringParameters() {
  const params = new URLSearchParams({ page: String(state.monitorPage), days: $('monitor-days').value });
  params.set('severity', $('monitor-severity').value || '1');
  if ($('monitor-search')?.value.trim()) params.set('q', $('monitor-search').value.trim());
  params.set('state', $('monitor-state').value || 'open');
  const kinds = [...document.querySelectorAll('[data-monitor-kind]')];
  const selected = kinds.filter(item => item.checked).map(item => item.dataset.monitorKind);
  if (kinds.length && selected.length !== kinds.length) params.set('types', selected.join(',') || 'none');
  if ($('monitor-company').value) params.set('companyId', $('monitor-company').value);
  if ($('monitor-site').value) params.set('siteId', $('monitor-site').value);
  return params;
}

let connectionsRequest=0;
async function loadConnections() {
  const requestId = ++connectionsRequest;
  status('connections-status', 'Загружаю подключения…');
  try {
    const connectionsParams = new URLSearchParams({ days: $('connections-days').value });
    const connections = await api('/api/admin/new-connections?' + connectionsParams);
    if (requestId !== connectionsRequest) return;
    const connectionContainer = $('monitor-connections'); connectionContainer.replaceChildren();
    for (const item of connections.items || []) {
      const row = node('div', undefined, 'monitor-connection-row');
      const title = node('strong', item.inventory ? 'PC № ' + item.inventory : 'PC № не указан');
      const place = node('span', [item.company, item.site].filter(Boolean).join(' · ') || 'Компания/объект не указаны');
      const when = node('span', date(item.createdAt), 'when');
      const openComputer = () => selectDevice({ id: item.deviceId,
        inventory: item.inventory, company: item.company, site: item.site, os: item.os, osVersion: item.osVersion });
      row.tabIndex = 0; row.setAttribute('role', 'link');
      row.setAttribute('aria-label', `Открыть PC № ${item.inventory || 'без номера'}`);
      row.addEventListener('click', event => {
        if (!event.target.closest('button,a,input') && !window.getSelection()?.toString()) openComputer();
      });
      row.addEventListener('keydown', event => {
        if (event.target === row && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); openComputer(); }
      });
      const info = node('div', undefined, 'connection-info');
      const identity = node('div', undefined, 'connection-identity'); identity.append(title, remoteAccessLine(item));
      const context = node('div', undefined, 'connection-context'); context.append(place, node('span', item.computer || '', 'connection-computer'));
      info.append(identity, context);
      const footer = node('div', undefined, 'connection-footer'); footer.append(when);
      row.append(info, footer);
      connectionContainer.append(row);
    }
    $('connections-count').textContent = connections.items?.length ? `${connections.items.length} новых` : '';
    $('monitor-connections-empty').classList.toggle('hidden', !!connections.items?.length);
    status('connections-status','');
  } catch(error) {if(requestId===connectionsRequest)status('connections-status',error.message,'error');}
}

async function loadApplicationErrors() {
  const requestId = ++state.applicationErrorRequest;
  status('app-errors-status', 'Загружаю журнал…');
  const params = new URLSearchParams({ days: $('app-errors-days').value, page: String(state.applicationErrorPage) });
  if ($('app-errors-company').value) params.set('companyId', $('app-errors-company').value);
  if ($('app-errors-site').value) params.set('siteId', $('app-errors-site').value);
  try {
    const result = await api('/api/admin/application-errors?' + params);
    if (requestId !== state.applicationErrorRequest) return;
    const container = $('app-errors-list');
    container.replaceChildren();
    for (const computer of result.items || []) {
      const card = node('article', undefined, 'application-error-card clickable');
      card.tabIndex = 0;
      const openComputer = () => selectDevice({ id: computer.deviceId, serial: computer.serial,
        inventory: computer.inventory, company: computer.company, site: computer.site });
      card.addEventListener('click', event => { if (!event.target.closest('button,a,details,summary')) openComputer(); });
      card.addEventListener('keydown', event => {
        if ((event.key === 'Enter' || event.key === ' ') && event.target === card) { event.preventDefault(); openComputer(); }
      });
      const heading = node('div', undefined, 'application-error-heading');
      const identity = node('div', undefined, 'application-error-identity');
      identity.append(node('strong', computer.inventory ? `PC № ${computer.inventory}` : 'PC № не указан'));
      identity.append(node('span', [computer.company, computer.site].filter(Boolean).join(' · ') || 'Компания и объект не указаны', 'application-error-context'));
      heading.append(identity, node('time', date(computer.latestErrorAt), 'when'));
      card.append(heading);
      const errors = node('div', undefined, 'application-error-items');
      for (const error of list(computer.errors)) {
        const item = node('section', undefined, 'application-error-item');
        const context = node('div', [error.component, error.operation, error.version ? 'версия ' + error.version : ''].filter(Boolean).join(' · '), 'application-error-context');
        const details = node('details', undefined, 'application-error-details');
        details.append(node('summary', `${date(error.occurredAt)} · ${error.summary || 'Ошибка приложения'}`));
        if (error.exceptionType) details.append(node('p', error.exceptionType, 'application-error-type'));
        if (error.technicalDetails) details.append(node('pre', error.technicalDetails, 'application-error-technical'));
        const history = property(error, 'sensorHistory');
        const charts = node('div', undefined, 'application-error-charts');
        for (const [category, label] of [['CpuLoad', 'Загрузка CPU'], ['MemoryLoad', 'Загрузка ОЗУ'],
          ['CpuTemperature', 'Температура CPU'], ['VrmTemperature', 'Температура VRM'], ['PchTemperature', 'Температура PCH'],
          ['Rail12V', '12 В'], ['Rail5V', '5 В'], ['Rail3V', '3,3 В'], ['CpuVoltage', 'Напряжение CPU']]) {
          const button = telemetryButton(history, category, label);
          if (button) charts.append(button);
        }
        item.append(context, details);
        if (charts.childElementCount) details.append(charts);
        errors.append(item);
      }
      card.append(errors);
      if (computer.hasMoreErrors) card.append(node('p', `Показаны последние ${list(computer.errors).length} из ${computer.errorCount} ошибок этого ПК.`, 'meta'));
      const footer = node('div', undefined, 'application-error-footer');
      footer.append(node('span', `${computer.errorCount} ошибок`), node('span', 'Открыть карточку ПК ›'));
      card.append(footer);
      container.append(card);
    }
    $('app-errors-page').textContent = 'Страница ' + (state.applicationErrorPage + 1);
    $('app-errors-prev').disabled = state.applicationErrorPage === 0;
    $('app-errors-next').disabled = !result.hasMore;
    $('app-errors-empty').classList.toggle('hidden', !!result.items?.length);
    const totalErrors = (result.items || []).reduce((sum, computer) => sum + Number(computer.errorCount || 0), 0);
    status('app-errors-status', result.items?.length ? `${result.items.length} компьютеров · ${totalErrors} ошибок` : '');
  } catch (error) {
    if (requestId === state.applicationErrorRequest) status('app-errors-status', error.message, 'error');
  }
}

async function loadMonitoring() {
  const requestId=++state.monitorRequest;
  status('monitor-status','Загружаю компьютеры…');
  try {
    const result=await api('/api/admin/monitoring?'+monitoringParameters());
    if(requestId!==state.monitorRequest)return;
    const container = $('monitor-list'); container.replaceChildren();
    for (const computer of result.items || []) {
      const card = node('article', undefined, 'monitor-card clickable');
      card.tabIndex = 0;
      const openComputer = () => selectDevice({ id: computer.deviceId, serial: computer.serial,
        inventory: computer.inventory, company: computer.company, site: computer.site }, computer.latestRunId);
      card.addEventListener('click', event => {
        if (!event.target.closest('button,a,details,summary')) openComputer();
      });
      card.addEventListener('keydown', event => {
        if ((event.key === 'Enter' || event.key === ' ') && event.target === card) { event.preventDefault(); openComputer(); }
      });
      const header = node('div', undefined, 'monitor-card-header');
      const identity = node('div');
      identity.append(node('strong', computer.inventory ? 'PC № ' + computer.inventory : 'PC № не указан'));
      identity.append(node('span', [computer.company, computer.site].filter(Boolean).join(' · ') || 'Компания и объект не указаны', 'monitor-card-place'));
      header.append(identity, node('span', date(computer.latestDetectedAt), 'when'));
      card.append(header);
      const errors = node('div', undefined, 'monitor-card-errors');
      for (const issue of list(computer.issues)) {
        const row = node('div', undefined, 'monitor-card-issue ' + (Number(issue.severity) === 1 ? 'critical' : 'warning'));
        const description = node('div', undefined, 'monitor-card-description');
        description.append(node('strong', issue.title || 'Проблема'));
        description.append(node('span', [Number(issue.occurrences) > 1 ? `Событий: ${issue.occurrences}` : ''].filter(Boolean).join(' · '), 'meta'));
        description.append(node('p', issue.detail || 'Подробности не переданы.'));
        row.append(description);
        if (String($('monitor-state').value) === 'open') {
          const actions = node('div', undefined, 'monitor-issue-actions');
          const resolve = node('button', '✓', 'issue-action resolve');
          resolve.type = 'button'; resolve.title = 'Отметить причину решённой'; resolve.setAttribute('aria-label', resolve.title);
          resolve.addEventListener('click', event => { event.stopPropagation(); updateMonitoringIssue(issue.id, 'resolve'); });
          const ignore = node('button', '×', 'issue-action ignore');
          ignore.type = 'button'; ignore.title = 'Игнорировать эту причину на данном ПК'; ignore.setAttribute('aria-label', ignore.title);
          ignore.addEventListener('click', event => { event.stopPropagation(); updateMonitoringIssue(issue.id, 'ignore', computer); });
          actions.append(resolve, ignore); row.append(actions);
        }
        errors.append(row);
      }
      card.append(errors);
      const footer = node('div', undefined, 'monitor-card-footer');
      footer.append(node('span', `${list(computer.issues).length} ошибок и предупреждений`));
      footer.append(node('span', 'Открыть карточку ПК ›'));
      card.append(footer);
      container.append(card);
    }
    $('monitor-empty').classList.toggle('hidden', !!result.items?.length);
    $('monitor-page').textContent = 'Страница ' + (state.monitorPage + 1);
    $('monitor-prev').disabled = state.monitorPage === 0;
    $('monitor-next').disabled = !result.hasMore;
    status('monitor-status', result.items?.length ? `Компьютеров: ${result.items.length}` : '');
  } catch (error) { if (requestId === state.monitorRequest) status('monitor-status', error.message, 'error'); }
}

async function updateMonitoringIssue(id, action, computer) {
  if (action === 'ignore') {
    const identity = computer.inventory ? `PC № ${computer.inventory}` : computer.computer || 'этого ПК';
    if (!confirm(`Игнорировать эту причину для ${identity}? Новые совпадающие события с этого ПК больше не будут показываться в мониторинге.`)) return;
  }
  const button = document.activeElement;
  if (button instanceof HTMLButtonElement) button.disabled = true;
  try {
    await api(`/api/admin/monitoring/${id}/${action}`, { method: 'POST' });
    await loadMonitoring();
    await loadOverview();
  } catch (error) {
    if (button instanceof HTMLButtonElement) button.disabled = false;
    status('monitor-status', error.message, 'error');
  }
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
      computer.append(node('strong', ticket.inventory ? 'PC № ' + ticket.inventory : 'PC № не указан', 'cell-main'), remoteAccessLine(ticket),
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
      const author = message.direction === 'user' ? 'Пользователь' : message.source === 'okdesk' ? 'Комментарий Okdesk' : 'Ответ инженера';
      const delivery = message.source === 'okdesk' ? '' : ' · ' + (ticketStates[message.state] || message.state);
      row.append(node('span', date(message.createdAt) + ' · ' + author + (message.isPublic ? '' : ' · Внутренний') + delivery, 'meta'),
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
  status('ticket-action-status', ''); $('ticket-dialog').showModal();
  navigationDialog = { kind: 'ticket', ticket }; rememberNavigation(); await loadConversation();
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
    const current = packages.filter(item => item.latest);
    const archived = packages.filter(item => !item.latest);
    $('package-rows').replaceChildren(...current.map(item => packageRow(item, false)));
    $('package-archive-rows').replaceChildren(...archived.map(item => packageRow(item, true)));
    $('package-empty').classList.toggle('hidden', packages.length !== 0);
    $('package-count').textContent = `Актуальных: ${current.length} · Всего: ${packages.length}`;
    $('package-archive-count').textContent = archived.length + ' версий';
    $('package-archive').classList.toggle('hidden', archived.length === 0);
  } catch (error) { status('package-status', error.message, 'error'); }
}

function packageRow(item, archived) {
  const row = node('tr');
  const title = node('td');
  title.append(node('span', item.name || item.key, 'cell-main'));
  if (item.name && item.name !== item.key) title.append(node('span', 'Ключ: ' + item.key, 'cell-sub'));
  row.append(title, node('td', item.version),
    node('td', {windows:'Windows 10/11',win7:'Windows 7',any:'Любая'}[item.platform] || item.platform),
    node('td', (item.sizeBytes / 1048576).toFixed(1) + ' МБ'), node('td', item.sha256, 'hash'),
    node('td', archived ? 'Архив' : 'Актуальная', archived ? 'muted' : 'signal-ok'));
  const actions = node('td');
  if (archived) {
    const activate = node('button', 'Сделать актуальной', 'secondary compact'); activate.type = 'button';
    activate.addEventListener('click', async () => {
      activate.disabled = true;
      try { await api('/api/admin/packages/' + item.id + '/activate', { method: 'POST' }); await loadPackages(); }
      catch (error) { status('package-status', error.message, 'error'); activate.disabled = false; }
    });
    actions.append(activate);
  }
  const remove = node('button', 'Удалить', 'danger compact'); remove.type = 'button';
  remove.addEventListener('click', async () => {
    const label = `${item.name || item.key} ${item.version} (${item.platform})`;
    const fallback = archived ? '' : '\nЕсли есть архивная версия, она станет актуальной.';
    if (!window.confirm(`Удалить пакет ${label} с сервера?${fallback}`)) return;
    remove.disabled = true;
    try {
      const result = await api('/api/admin/packages/' + item.id, { method: 'DELETE' });
      await loadPackages();
      const restored = result.fallbackVersion ? ` Активирована версия ${result.fallbackVersion}.` : '';
      const cleanup = result.cleanupPending ? ' Запись удалена, но файл пакета не удалось убрать.' : '';
      status('package-status', 'Пакет удалён.' + restored + cleanup, result.cleanupPending ? 'error' : 'ok');
    } catch (error) { status('package-status', error.message, 'error'); remove.disabled = false; }
  });
  actions.append(remove);
  row.append(actions);
  return row;
}

async function uploadPackage(event) {
  event.preventDefault();
  const form = event.currentTarget, button = form.querySelector('button[type="submit"]');
  button.disabled = true;
  try {
    const result = await new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', '/api/admin/packages');
      xhr.withCredentials = true;
      xhr.timeout = 30 * 60 * 1000;
      xhr.setRequestHeader('X-CSRF-TOKEN', csrf);
      xhr.upload.onprogress = progress => {
        if (progress.lengthComputable) status('package-status', `Загружаю файл: ${Math.round(progress.loaded * 100 / progress.total)}% (${(progress.loaded / 1048576).toFixed(1)} из ${(progress.total / 1048576).toFixed(1)} МБ)…`);
        else status('package-status', 'Загружаю файл на сервер…');
      };
      xhr.upload.onload = () => status('package-status', 'Файл передан. Сервер проверяет версию и SHA-256…');
      xhr.onload = () => {
        if (xhr.status === 401 || (xhr.responseURL && new URL(xhr.responseURL).pathname === '/login')) {
          location.assign('/login'); reject(Error('Сеанс завершён.')); return;
        }
        let body;
        try { body = JSON.parse(xhr.responseText); }
        catch {
          reject(Error(xhr.status >= 500
            ? `Сервер не смог обработать загрузку (HTTP ${xhr.status}); проверьте журнал сервера.`
            : `Сервер вернул не JSON (HTTP ${xhr.status}). Обновите страницу и войдите снова.`));
          return;
        }
        if (xhr.status < 200 || xhr.status >= 300) { reject(Object.assign(Error(body.error || `Ошибка загрузки (HTTP ${xhr.status}).`), { status: xhr.status })); return; }
        resolve(body);
      };
      xhr.onerror = () => reject(Error('Сетевая ошибка во время загрузки ПО.'));
      xhr.ontimeout = () => reject(Error('Загрузка не завершилась за 30 минут. Проверьте соединение и размер файла.'));
      xhr.onabort = () => reject(Error('Загрузка отменена.'));
      xhr.send(new FormData(form));
    });
    status('package-status', `Загружено: ${result.name || result.key} · ${result.version} (${result.key}). SHA-256: ${result.sha256}`, 'ok');
    form.reset(); await loadPackages();
  } catch (error) {
    if (error.status === 409) await loadPackages();
    status('package-status', error.message, 'error');
  }
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
$('ticket-dialog-close').addEventListener('click', () => closeNavigationDialog('ticket-dialog'));
$('telemetry-close').addEventListener('click', () => closeNavigationDialog('telemetry-dialog'));
$('telemetry-dialog').addEventListener('click', event => {
  if (event.target === $('telemetry-dialog')) closeNavigationDialog('telemetry-dialog');
});
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
$('refresh-connections').addEventListener('click',loadConnections);
$('connections-days').addEventListener('change',loadConnections);
$('refresh-application-errors').addEventListener('click',loadApplicationErrors);
$('app-errors-days').addEventListener('change',() => { state.applicationErrorPage = 0; loadApplicationErrors(); });
$('app-errors-company').addEventListener('change',() => { fillSites('app-errors', true); state.applicationErrorPage = 0; loadApplicationErrors(); });
$('app-errors-site').addEventListener('change',() => { state.applicationErrorPage = 0; loadApplicationErrors(); });
$('app-errors-prev').addEventListener('click',() => { state.applicationErrorPage = Math.max(0, state.applicationErrorPage - 1); loadApplicationErrors(); });
$('app-errors-next').addEventListener('click',() => { state.applicationErrorPage++; loadApplicationErrors(); });
$('refresh-tickets').addEventListener('click', loadTickets);
$('ticket-block-toggle').addEventListener('click', async () => {
  const device = state.selectedDevice;
  if (!device) return;
  const blocked = !device.ticketsBlocked;
  try {
    await api('/api/admin/devices/' + device.id + '/ticket-block', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ blocked })
    });
    device.ticketsBlocked = blocked;
    $('ticket-block-toggle').textContent = blocked ? 'Разрешить заявки' : 'Заблокировать заявки';
    $('ticket-block-toggle').classList.toggle('danger', !blocked);
    status('device-status', blocked ? 'Создание заявок и ответы для этого ПК заблокированы.' : 'Заявки для этого ПК снова разрешены.', 'ok');
    loadDevices();
  } catch (error) { status('device-status', error.message, 'error'); }
});
async function refreshDeviceLists() { await loadDevices(); }
let searchTimer;
$('device-search').addEventListener('input', () => {
  clearDeviceSelection();
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => { state.devicePage = 0; rememberNavigation(); refreshDeviceLists(); }, 300);
});
$('device-company').addEventListener('change', () => { fillSites('device', true); state.devicePage = 0; clearDeviceSelection(); refreshDeviceLists(); });
$('device-site').addEventListener('change', () => { state.devicePage = 0; clearDeviceSelection(); refreshDeviceLists(); });
$('device-prev').addEventListener('click', () => { state.devicePage--; loadDevices(); });
$('device-next').addEventListener('click', () => { state.devicePage++; loadDevices(); });
for (const id of ['monitor-days', 'monitor-search', 'monitor-site', 'monitor-severity', 'monitor-state']) $(id).addEventListener('change', () => { state.monitorPage = 0; loadMonitoring(); });
for (const item of document.querySelectorAll('[data-monitor-kind]')) item.addEventListener('change', () => { state.monitorPage = 0; rememberNavigation(); loadMonitoring(); });
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
    const equipment = result.equipmentWarning || `Оборудование Okdesk: ${result.equipment ?? 'не получено'}.`;
    status('device-status', `Обновлено: ${result.companies} компаний, ${result.sites} объектов. ${equipment}`, result.equipmentWarning ? 'error' : 'ok');
  } catch (error) { status('device-status', error.message, 'error'); }
  finally { button.disabled = false; }
});
$('package-form').addEventListener('submit', uploadPackage);

window.addEventListener('popstate', event => restoreNavigation(event.state?.itseti).catch(error => status('device-status', error.message, 'error')));
for (const id of ['ticket-dialog', 'telemetry-dialog']) $(id).addEventListener('cancel', event => { event.preventDefault(); closeNavigationDialog(id); });
document.addEventListener('change', event => { if (navigationFilters.includes(event.target.id)) rememberNavigation(); });
document.addEventListener('click', event => {
  if (['device-prev', 'device-next', 'monitor-prev', 'monitor-next', 'app-errors-prev', 'app-errors-next', 'ticket-prev', 'ticket-next'].includes(event.target.closest('button')?.id)) rememberNavigation();
});
Promise.all([loadCatalog(), loadOverview()]).then(async () => {
  navigationReady = true;
  const saved = window.history.state?.itseti;
  if (saved) await restoreNavigation(saved);
  else { await refreshDeviceLists(); rememberNavigation(true); }
}).catch(error => status('device-status', error.message, 'error'));

let monitorSearchTimer;
$('monitor-search').addEventListener('input', () => { clearTimeout(monitorSearchTimer); monitorSearchTimer=setTimeout(() => { state.monitorPage=0; rememberNavigation(); loadMonitoring(); },250); });

window.addEventListener('error', () => notifyError('Ошибка интерфейса. Обновите страницу; если ошибка повторяется, сообщите инженеру.'));
window.addEventListener('unhandledrejection', event => notifyError(event.reason?.message || 'Не удалось выполнить действие. Повторите попытку.'));
