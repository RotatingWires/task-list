(() => {
  'use strict';
  const params = new URLSearchParams(location.search);
  const listId = Number(params.get('list'));
  const universalId = Number(params.get('task'));
  const displayId = params.get('display') || null;
  const hasDeepLink = Number.isSafeInteger(listId) && listId > 0 && ((Number.isSafeInteger(universalId) && universalId > 0) || displayId);

  function clearDeepLink() {
    const url = new URL(location.href);
    let changed = false;
    for (const key of ['list','view','task','display']) if (url.searchParams.has(key)) { url.searchParams.delete(key); changed = true; }
    if (changed) history.replaceState(null, '', `${url.pathname}${url.search}${url.hash}`);
  }
  function findUniversal(items, id) {
    for (const item of items) {
      if (item.universalId === id) return item;
      const nested = findUniversal(item.subtasks || [], id);
      if (nested) return nested;
    }
    return null;
  }
  function highlight(display) {
    requestAnimationFrame(() => {
      const row = [...taskList.querySelectorAll('tr[data-display-id]')].find(x => x.dataset.displayId === display);
      if (!row) { setStatus('Linked task was not found.'); return; }
      row.scrollIntoView({block:'center', inline:'nearest'});
      row.style.background = '#fff4a8';
      [...row.cells].forEach(cell => cell.style.background = '#fff4a8');
      setTimeout(() => { row.style.background=''; [...row.cells].forEach(cell => cell.style.background=''); }, 3200);
    });
  }

  if (hasDeepLink) {
    const originalLoadTasks = loadTasks;
    let intercepted = false;
    loadTasks = async function() {
      if (!intercepted && lists.some(list => list.id === listId)) {
        intercepted = true;
        currentListId = listId;
        localStorage.setItem('task-list-current-list', String(listId));
        currentView = 'all';
        updateViewMenu(); renderFileMenu(); updateListTitle();
      }
      const result = await originalLoadTasks();
      if (intercepted === true) {
        const item = Number.isSafeInteger(universalId) && universalId > 0 ? findUniversal(tasks, universalId) : null;
        highlight(item?.displayId || displayId);
        intercepted = 'done';
      }
      return result;
    };
  }

  const originalSelectList = selectList;
  selectList = async function(id) { clearDeepLink(); return originalSelectList(id); };
  const originalSetView = setView;
  setView = function(view) { clearDeepLink(); return originalSetView(view); };

  const oldHelp = document.querySelector('#aboutMenu');
  if (oldHelp) {
    const help = oldHelp.cloneNode(true);
    oldHelp.replaceWith(help);
    help.addEventListener('click', () => alert('Tasks with dates of "Unknown" were imported from a third party application, and have no data regarding those dates.\n\nabout.lehighradio.com\nTask List v1.3.2'));
  }
})();
