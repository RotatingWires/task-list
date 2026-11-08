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
// View → Use Retro Font: the bundled W95FA font, off until chosen, saved per app like the theme.
const RETRO_FONT_KEY = 'task-ui-retro-font';

function readRetroFont() {
  try {
    return localStorage.getItem(RETRO_FONT_KEY) === 'on';
  } catch {
    return false;
  }
}

function applyRetroFont(on, persist = false) {
  if (persist) {
    try { localStorage.setItem(RETRO_FONT_KEY, on ? 'on' : 'off'); } catch {}
  }
  document.documentElement.toggleAttribute('data-retro-font', on);
  // Canvas charts draw with the page font, so they redraw once it has loaded.
  if (persist) document.fonts.load('13px W95FA').finally(() => window.dispatchEvent(new CustomEvent('task-theme-change')));
  return on;
}

const getRetroFont = () => document.documentElement.hasAttribute('data-retro-font');
const setRetroFont = on => applyRetroFont(on, true);

window.addEventListener('storage', event => {
  if (event.key === RETRO_FONT_KEY) applyRetroFont(readRetroFont());
});
applyRetroFont(readRetroFont());

window.TaskTheme = {
  getPreference,
  setPreference,
  getRetroFont,
  setRetroFont
};
})();
