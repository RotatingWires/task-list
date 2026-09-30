'use strict';

function closeFileMenu() {
  fileDropdown.hidden = true;
  fileMenu.setAttribute('aria-expanded', 'false');
}

function closeViewMenu() {
  viewDropdown.hidden = true;
  viewMenu.setAttribute('aria-expanded', 'false');
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
  viewDropdown.hidden = !willOpen;
  viewMenu.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

for (const choice of viewChoices)
  choice.addEventListener('click', () => setView(choice.dataset.view));

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
closeAbout.addEventListener('click', () => aboutDialog.close());

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}

function initializeTaskInfoTouchHelp() {
  const popup = document.createElement('div');
  popup.className = 'tap-help-tooltip';
  popup.hidden = true;
  document.body.append(popup);

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

  infoDialog.addEventListener('scroll', hide, { passive: true });
  infoDialog.addEventListener('close', hide);
  window.addEventListener('resize', hide);
}

async function start() {
  updateViewMenu();
  loadAppVersion();
  initializeTaskInfoTouchHelp();

  try {
    await loadLists();

    let deepLinkError = null;
    if (hasDeepLink) {
      const target = await resolveUniversalLocation(deepLinkUniversalId);
      if (target) {
        currentListId = target.listId;
        localStorage.setItem('task-list-current-list', String(target.listId));
        currentView = 'all';
        updateViewMenu();
        renderFileMenu();
        updateListTitle();
        pendingDeepLinkDisplayId = target.displayId;
      } else {
        deepLinkError = `Task with Universal ID ${deepLinkUniversalId} was not found.`;
      }
    }

    await loadTasks();
    if (deepLinkError) setStatus(deepLinkError);
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

start();
