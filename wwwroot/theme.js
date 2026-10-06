(() => {
'use strict';

const STORAGE_KEY = 'task-ui-theme';
const VALID = new Set(['light', 'dark']);

function readPreference() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    return VALID.has(saved) ? saved : 'light';
  } catch {
    return 'light';
  }
}

function applyTheme(preference, persist = false) {
  const selected = VALID.has(preference) ? preference : 'light';
  if (persist) {
    try { localStorage.setItem(STORAGE_KEY, selected); } catch {}
  }

  const root = document.documentElement;
  const previousPreference = root.dataset.themePreference;
  root.dataset.themePreference = selected;
  root.dataset.themeEffective = selected;
  root.style.colorScheme = selected;

  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', selected === 'dark' ? '#111858' : '#000080');

  if (previousPreference !== undefined && previousPreference !== selected) {
    window.dispatchEvent(new CustomEvent('task-theme-change', {
      detail: { theme: selected }
    }));
  }
  return selected;
}

function getPreference() {
  return document.documentElement.dataset.themePreference || readPreference();
}

function setPreference(preference) {
  return applyTheme(preference, true);
}

window.addEventListener('storage', event => {
  if (event.key === STORAGE_KEY) applyTheme(readPreference(), false);
});

applyTheme(readPreference(), false);
window.TaskTheme = {
  getPreference,
  setPreference
};
})();
