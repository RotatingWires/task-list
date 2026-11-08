/*
 * Light/dark theme and View → Use Retro Font. The same file in TaskList, TaskList Stats, NewsReview, and SpendTracker,
 * loaded at the top of every page (login included) so the page never flashes the wrong theme or font.
 */
(() => {
'use strict';

// The only lines that differ between the apps: where each one saves its choices in this browser.
const STORAGE_KEY = 'task-ui-theme';
const RETRO_FONT_KEY = 'task-ui-retro-font';
const VALID = new Set(['light', 'dark']);

function readPreference() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    return VALID.has(saved) ? saved : 'light';
  } catch {
    return 'light';
  }
}

/** data-theme on the page picks the colors in theme.css; color-scheme makes scrollbars and form controls match. */
function applyTheme(preference, persist = false) {
  const selected = VALID.has(preference) ? preference : 'light';
  if (persist) {
    try { localStorage.setItem(STORAGE_KEY, selected); } catch {}
  }
  const root = document.documentElement;
  const previous = root.dataset.theme;
  root.dataset.theme = selected;
  root.style.colorScheme = selected;
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', selected === 'dark' ? '#111858' : '#000080');
  if (previous !== undefined && previous !== selected) window.dispatchEvent(new CustomEvent('theme-change', { detail: { theme: selected } }));
  return selected;
}

const getPreference = () => document.documentElement.dataset.theme || readPreference();
const setPreference = preference => applyTheme(preference, true);

function readRetroFont() {
  try {
    return localStorage.getItem(RETRO_FONT_KEY) === 'on';
  } catch {
    return false;
  }
}

/** The bundled W95FA font (fonts/OFL.txt), off until chosen. Canvas charts redraw once it has loaded. */
function applyRetroFont(on, persist = false) {
  if (persist) {
    try { localStorage.setItem(RETRO_FONT_KEY, on ? 'on' : 'off'); } catch {}
  }
  document.documentElement.toggleAttribute('data-retro-font', on);
  if (persist) document.fonts.load('13px W95FA').finally(() => window.dispatchEvent(new CustomEvent('theme-change', { detail: { theme: getPreference() } })));
  return on;
}

const getRetroFont = () => document.documentElement.hasAttribute('data-retro-font');
const setRetroFont = on => applyRetroFont(on, true);

window.addEventListener('storage', event => {
  if (event.key === STORAGE_KEY) applyTheme(readPreference());
  if (event.key === RETRO_FONT_KEY) applyRetroFont(readRetroFont());
});

applyTheme(readPreference());
applyRetroFont(readRetroFont());
window.AppTheme = { getPreference, setPreference, getRetroFont, setRetroFont };
})();
