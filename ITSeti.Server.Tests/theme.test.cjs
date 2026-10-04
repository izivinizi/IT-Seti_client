const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../ITSeti.Server/wwwroot/theme.js'), 'utf8');
function boot(saved, blocked = false, hasButton = true) {
  const events = {}, attributes = {}, storage = { 'itseti-theme': saved };
  const root = { dataset: {} }, icon = {};
  const button = { setAttribute: (key, value) => { attributes[key] = value; }, addEventListener: (key, fn) => { events[key] = fn; } };
  const context = vm.createContext({
    document: { documentElement: root, getElementById: id => id === 'theme-toggle' ? (hasButton ? button : null) : icon,
      addEventListener: (key, fn) => { events[key] = fn; } },
    window: { addEventListener: (key, fn) => { events[key] = fn; } },
    localStorage: { getItem: key => { if (blocked) throw Error('disabled'); return storage[key]; },
      setItem: (key, value) => { if (blocked) throw Error('disabled'); storage[key] = value; } }
  });
  vm.runInContext(source, context);
  return { root, events, attributes, storage };
}
const first = boot('dark');
assert.equal(first.root.dataset.theme, 'dark', 'apply saved theme before DOM ready');
first.events.DOMContentLoaded();
assert.equal(first.attributes['aria-pressed'], 'true');
first.events.click();
assert.equal(first.root.dataset.theme, 'light');
assert.equal(first.storage['itseti-theme'], 'light');
first.events.click();
assert.equal(boot(first.storage['itseti-theme']).root.dataset.theme, 'dark', 'persist on reload');
first.events.storage({ key: 'itseti-theme', newValue: 'light' });
assert.equal(first.root.dataset.theme, 'light', 'synchronize other tabs');
const denied = boot(null, true);
denied.events.DOMContentLoaded();
denied.events.click();
assert.equal(denied.root.dataset.theme, 'dark', 'storage failure does not break toggle');
boot('dark', false, false).events.DOMContentLoaded();
console.log('PASS: initial theme, toggle, persistence, cross-tab sync, blocked storage, login page');
