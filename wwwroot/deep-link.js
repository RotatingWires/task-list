(() => {
  'use strict';

  const params = new URLSearchParams(location.search);
  const prettyMatch = /^\/task\/(\d+)\/?$/.exec(location.pathname);
  const prettyUniversalId = prettyMatch ? Number(prettyMatch[1]) : null;
  const legacyListId = Number(params.get('list'));
  const legacyUniversalId = Number(params.get('task'));
  const displayId = params.get('display') || null;
  const universalId = Number.isSafeInteger(prettyUniversalId) && prettyUniversalId > 0
    ? prettyUniversalId
    : legacyUniversalId;
  const hasPrettyLink = Number.isSafeInteger(prettyUniversalId) && prettyUniversalId > 0;
  const hasLegacyLink = Number.isSafeInteger(legacyListId) && legacyListId > 0
    && ((Number.isSafeInteger(legacyUniversalId) && legacyUniversalId > 0) || displayId);
  const hasDeepLink = hasPrettyLink || hasLegacyLink;

  function clearDeepLink() {
    const url = new URL(location.href);
    let changed = false;

    if (/^\/task\/\d+\/?$/.test(url.pathname)) {
      url.pathname = '/';
      changed = true;
    }

    for (const key of ['list', 'view', 'task', 'display']) {
      if (url.searchParams.has(key)) {
        url.searchParams.delete(key);
        changed = true;
      }
    }

    if (changed)
      history.replaceState(null, '', `${url.pathname}${url.search}${url.hash}`);
  }

  function findUniversal(items, id) {
    for (const item of items) {
      if (item.universalId === id) return item;
      const nested = findUniversal(item.subtasks || [], id);
      if (nested) return nested;
    }
    return null;
  }

  async function resolveUniversalLocation(id) {
    if (!Number.isSafeInteger(id) || id <= 0) return null;

    for (const list of lists) {
      const listTasks = await api(`/api/lists/${list.id}/tasks`);
      const item = findUniversal(listTasks, id);
      if (item) return { listId: list.id, displayId: item.displayId };
    }

    return null;
  }

  function scrollAndHighlight(display) {
    requestAnimationFrame(() => {
      const row = [...taskList.querySelectorAll('tr[data-display-id]')]
        .find(x => x.dataset.displayId === display);
      if (!row) {
        setStatus('Linked task was not found.');
        return;
      }

      row.scrollIntoView({ block: 'start', inline: 'nearest' });
      const taskPanel = row.closest('.task-panel');
      const tableHead = taskPanel?.querySelector('thead');
      const headerVisible = tableHead && getComputedStyle(tableHead).display !== 'none';
      const headerHeight = headerVisible ? tableHead.getBoundingClientRect().height : 0;
      if (taskPanel && headerHeight > 0)
        taskPanel.scrollTop = Math.max(0, taskPanel.scrollTop - headerHeight - 2);

      row.style.background = '#fff4a8';
      [...row.cells].forEach(cell => { cell.style.background = '#fff4a8'; });
      setTimeout(() => {
        row.style.background = '';
        [...row.cells].forEach(cell => { cell.style.background = ''; });
      }, 3200);
    });
  }

  if (hasDeepLink) {
    const originalLoadTasks = loadTasks;
    let intercepted = false;
    let targetListId = hasLegacyLink ? legacyListId : null;
    let targetDisplayId = displayId;

    loadTasks = async function() {
      if (!intercepted) {
        if (hasPrettyLink) {
          try {
            const target = await resolveUniversalLocation(universalId);
            if (target) {
              targetListId = target.listId;
              targetDisplayId = target.displayId;
            }
          } catch (error) {
            setStatus(`Deep-link error: ${error.message}`);
          }
        }

        if (Number.isSafeInteger(targetListId) && lists.some(list => list.id === targetListId)) {
          intercepted = true;
          currentListId = targetListId;
          localStorage.setItem('task-list-current-list', String(targetListId));
          currentView = 'all';
          updateViewMenu();
          renderFileMenu();
          updateListTitle();
        }
      }

      const result = await originalLoadTasks();
      if (intercepted === true) {
        const item = Number.isSafeInteger(universalId) && universalId > 0
          ? findUniversal(tasks, universalId)
          : null;
        scrollAndHighlight(item?.displayId || targetDisplayId);
        intercepted = 'done';
      } else if (!intercepted && hasPrettyLink) {
        setStatus(`Task with Universal ID ${universalId} was not found.`);
        intercepted = 'done';
      }
      return result;
    };
  }

  const originalSelectList = selectList;
  selectList = async function(id) {
    clearDeepLink();
    return originalSelectList(id);
  };

  const originalSetView = setView;
  setView = function(view) {
    clearDeepLink();
    return originalSetView(view);
  };

  const oldHelp = document.querySelector('#aboutMenu');
  if (oldHelp) {
    const help = oldHelp.cloneNode(true);
    oldHelp.replaceWith(help);
    help.addEventListener('click', () => alert(
      'Tasks with dates of "Unknown" were imported from a third party application, and have no data regarding those dates.\n\n' +
      'about.lehighradio.com\nTask List v1.3.3'
    ));
  }
})();
