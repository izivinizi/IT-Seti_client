(() => {
  const key = 'itseti-theme';
  let theme = 'light';
  try { theme = localStorage.getItem(key) === 'dark' ? 'dark' : 'light'; } catch { /* Storage can be disabled. */ }
  function apply(value) {
    theme = value;
    document.documentElement.dataset.theme = value;
    const button = document.getElementById('theme-toggle');
    if (!button) return;
    const label = value === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему';
    button.setAttribute('aria-label', label);
    button.setAttribute('title', label);
    button.setAttribute('aria-pressed', String(value === 'dark'));
    document.getElementById('theme-icon').textContent = value === 'dark' ? '☀' : '☾';
  }
  apply(theme);
  document.addEventListener('DOMContentLoaded', () => {
    apply(theme);
    document.getElementById('theme-toggle')?.addEventListener('click', () => {
      apply(theme === 'dark' ? 'light' : 'dark');
      try { localStorage.setItem(key, theme); } catch { /* Theme still works without persistence. */ }
    });
  });
  window.addEventListener('storage', event => {
    if (event.key === key) apply(event.newValue === 'dark' ? 'dark' : 'light');
  });
})();
