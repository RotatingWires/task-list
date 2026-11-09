/* Menus, navigation, startup and the first-run reminder, and deep links. */
import { $, aboutDialog, aboutMenu, api, appWindow, currentListId, currentView, deepLinkUniversalId, el, fileDropdown, fileMenu, hasDeepLink, infoDialog, loadAppVersion, pendingDeepLinkDisplayId, resolveUniversalLocation, setCurrentListId, setCurrentView, setPendingDeepLinkDisplayId, setStatus, viewChoices, viewDropdown, viewMenu } from './app.js';
import { loadTasks, setView, updateViewMenu } from './tasks.js';
import { openTransfer } from './transfer.js';
import { loadLists, renderFileMenu, updateListTitle } from './lists.js';

const themeChoices = [...viewDropdown.querySelectorAll('[data-theme-choice]')];
const retroFontChoice = $('#retroFontChoice');
const firstRunDialog = $('#firstRunDialog');
const firstRunImport = $('#firstRunImport');
const firstRunContinue = $('#firstRunContinue');
const firstRunError = $('#firstRunError');
let importReminderResolve = null;
let importReminderBusy = false;

async function showImportReminder() {
  const onboarding = await api('/api/onboarding', { cache: 'no-store' });
  if (!onboarding.importReminderPending) return;
  firstRunError.textContent = '';
  await new Promise(resolve => {
    importReminderResolve = resolve;
    firstRunDialog.showModal();
    firstRunImport.focus();
  });
}

async function dismissImportReminder(openImport = false) {
  if (importReminderBusy) return;
  importReminderBusy = true;
  firstRunImport.disabled = true;
  firstRunContinue.disabled = true;
  firstRunError.textContent = '';
  try {
    await api('/api/onboarding/acknowledge', { method: 'POST' }, false);
    firstRunDialog.close();
    if (openImport) openTransfer();
  } catch (error) {
    firstRunError.textContent = error.message;
  } finally {
    importReminderBusy = false;
    firstRunImport.disabled = false;
    firstRunContinue.disabled = false;
  }
}

firstRunImport.addEventListener('click', () => dismissImportReminder(true));
firstRunContinue.addEventListener('click', () => dismissImportReminder());
firstRunDialog.addEventListener('cancel', event => {
  event.preventDefault();
  dismissImportReminder();
});
firstRunDialog.addEventListener('close', () => {
  importReminderResolve?.();
  importReminderResolve = null;
});

export function closeFileMenu() {
  fileDropdown.hidden = true;
  fileMenu.setAttribute('aria-expanded', 'false');
}

export function closeViewMenu() {
  viewDropdown.hidden = true;
  viewMenu.setAttribute('aria-expanded', 'false');
}

function updateThemeMenu() {
  const selected = window.AppTheme?.getPreference?.() ?? 'light';
  for (const choice of themeChoices)
    choice.setAttribute('aria-checked', choice.dataset.themeChoice === selected ? 'true' : 'false');
  retroFontChoice.setAttribute('aria-checked', window.AppTheme?.getRetroFont?.() ? 'true' : 'false');
}

fileMenu.addEventListener('click', event => {
  event.stopPropagation();
  const willOpen = fileDropdown.hidden;
  closeViewMenu();
  if (willOpen) renderFileMenu();
  fileDropdown.hidden = !willOpen;
  fileMenu.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

viewMenu.addEventListener('click', event => {
  event.stopPropagation();
  const willOpen = viewDropdown.hidden;
  closeFileMenu();
  if (willOpen) updateThemeMenu();
  viewDropdown.hidden = !willOpen;
  viewMenu.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

for (const choice of viewChoices)
  choice.addEventListener('click', () => setView(choice.dataset.view));

for (const choice of themeChoices) {
  choice.addEventListener('click', event => {
    event.stopPropagation();
    window.AppTheme?.setPreference?.(choice.dataset.themeChoice);
    updateThemeMenu();
    closeViewMenu();
  });
}

retroFontChoice.addEventListener('click', event => {
  event.stopPropagation();
  window.AppTheme?.setRetroFont?.(!window.AppTheme.getRetroFont());
  updateThemeMenu();
  closeViewMenu();
});

window.addEventListener('theme-change', updateThemeMenu);

document.addEventListener('click', event => {
  if (!event.target.closest('.menu-wrap')) {
    closeFileMenu();
    closeViewMenu();
  }
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    closeFileMenu();
    closeViewMenu();
  }
});

aboutMenu.addEventListener('click', () => aboutDialog.showModal());

// No offline copy, like NewsReview: remove the service worker and cache that earlier releases installed.
navigator.serviceWorker?.getRegistrations().then(registrations => registrations.forEach(registration => registration.unregister())).catch(() => {});
window.caches?.keys().then(keys => keys.forEach(key => caches.delete(key))).catch(() => {});

function initializeMouseHoverDetection() {
  const root = document.documentElement;
  let ignoreSyntheticMouseUntil = 0;

  document.addEventListener('touchstart', () => {
    ignoreSyntheticMouseUntil = performance.now() + 1200;
    root.classList.remove('mouse-hover-capable');
  }, { passive: true, capture: true });

  document.addEventListener('mousemove', () => {
    if (performance.now() >= ignoreSyntheticMouseUntil)
      root.classList.add('mouse-hover-capable');
  }, { passive: true });
}

function initializeTaskInfoTouchHelp() {
  const popup = el('div', 'tap-help-tooltip');
  popup.hidden = true;
  infoDialog.append(popup);

  function hide() {
    popup.hidden = true;
  }

  function showFor(element) {
    const text = element.getAttribute('title');
    if (!text) return;

    popup.textContent = text;
    popup.hidden = false;

    const rect = element.getBoundingClientRect();
    const margin = 10;
    const width = popup.offsetWidth || 260;
    const height = popup.offsetHeight || 60;
    const center = rect.left + rect.width / 2;
    const left = Math.max(margin, Math.min(window.innerWidth - width - margin, center - width / 2));
    let top = rect.bottom + 12;
    if (top + height + margin > window.innerHeight)
      top = Math.max(margin, rect.top - height - 12);

    popup.style.left = `${left}px`;
    popup.style.top = `${top}px`;
  }

  document.addEventListener('click', event => {
    const target = event.target.closest?.('#infoDialog .has-tooltip[title]');
    if (target) {
      showFor(target);
      return;
    }
    hide();
  }, true);

  document.addEventListener('keydown', event => {
    if ((event.key === 'Enter' || event.key === ' ') && event.target.matches?.('#infoDialog .has-tooltip[title]')) {
      event.preventDefault();
      showFor(event.target);
    } else if (event.key === 'Escape') {
      hide();
    }
  });

  infoDialog.querySelector('.info-body').addEventListener('scroll', hide, { passive: true });
  infoDialog.addEventListener('close', hide);
  window.addEventListener('resize', hide);
}

async function start() {
  appWindow.inert = true;
  updateViewMenu();
  updateThemeMenu();
  loadAppVersion();
  initializeMouseHoverDetection();
  initializeTaskInfoTouchHelp();

  try {
    await loadLists();

    let deepLinkError = null;
    if (hasDeepLink) {
      const target = await resolveUniversalLocation(deepLinkUniversalId);
      if (target) {
        setCurrentListId(target.listId);
        localStorage.setItem('task-list-current-list', String(target.listId));
        setCurrentView('all');
        updateViewMenu();
        renderFileMenu();
        updateListTitle();
        setPendingDeepLinkDisplayId(target.displayId);
      } else {
        deepLinkError = `Task with Universal ID ${deepLinkUniversalId} was not found.`;
      }
    }

    await loadTasks();
    if (deepLinkError) setStatus(deepLinkError);
    await showImportReminder();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  } finally {
    appWindow.inert = false;
  }

  try {
    await import('/milestones.js');
  } catch {}
}

// Start once every module has run: module scripts all finish before DOMContentLoaded.
document.addEventListener('DOMContentLoaded', start);
