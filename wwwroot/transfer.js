'use strict';

const transferDialog = document.querySelector('#transferDialog');
const transferFile = document.querySelector('#transferFile');
const exportData = document.querySelector('#exportData');
const importData = document.querySelector('#importData');
const closeTransfer = document.querySelector('#closeTransfer');
const transferListName = document.querySelector('#transferListName');
let transferBusy = false;

function openTransfer() {
  closeFileMenu();
  closeViewMenu();
  closeTaskActionMenus();
  transferListName.textContent = currentList()?.name ?? 'the current list';
  transferDialog.showModal();
  exportData.focus();
}

function setTransferBusy(busy) {
  transferBusy = busy;
  exportData.disabled = busy;
  importData.disabled = busy;
  closeTransfer.disabled = busy;
  appWindow.inert = busy;
}

async function refreshTransferData() {
  await loadLists();
  tasks = await api(`/api/lists/${currentListId}/tasks`);
  renderTasks();
  await loadStats().catch(() => {});
}

closeTransfer.addEventListener('click', () => transferDialog.close());
transferDialog.addEventListener('cancel', event => {
  if (transferBusy) event.preventDefault();
});

exportData.addEventListener('click', async () => {
  if (transferBusy) return;
  setTransferBusy(true);
  setStatus('Creating export...');
  let downloadUrl = null;
  try {
    const response = await apiResponse('/api/export', { cache: 'no-store' });
    if (!response.headers.get('Content-Type')?.toLowerCase().startsWith('application/zip'))
      throw new Error('The server did not return a ZIP export. Rebuild and restart TaskList if it has just been updated.');
    downloadUrl = URL.createObjectURL(await response.blob());
    const link = document.createElement('a');
    link.href = downloadUrl;
    link.download = /filename="?([^";]+)"?/.exec(response.headers.get('Content-Disposition') ?? '')?.[1]
      ?? 'task-list-export.zip';
    document.body.append(link);
    link.click();
    link.remove();
    setStatus('Export download started.');
  } catch (error) {
    setStatus(`Export error: ${error.message}`);
  } finally {
    setTransferBusy(false);
    if (downloadUrl) window.setTimeout(() => URL.revokeObjectURL(downloadUrl), 60000);
  }
});

importData.addEventListener('click', () => {
  if (transferBusy) return;
  transferFile.value = '';
  transferFile.click();
});

transferFile.addEventListener('change', async () => {
  const file = transferFile.files?.[0];
  if (!file || transferBusy) return;
  const backup = /\.zip$/i.test(file.name);
  if (!backup && !/\.(md|markdown|txt)$/i.test(file.name)) {
    setStatus('Import error: choose a TaskList ZIP or Markdown file.');
    return;
  }
  if (backup && file.size > 256 * 1024 * 1024) {
    setStatus('Import error: backup ZIP exceeds the 256 MiB limit.');
    return;
  }
  const listId = currentListId;
  const listName = currentList()?.name ?? 'the current list';
  if (!backup && !listId) return;
  setTransferBusy(true);
  transferDialog.close();
  let imported = false;
  let restoreAttempted = false;
  try {
    if (backup && !await confirmAction({
      title: 'Restore TaskList Backup',
      message: 'Replace all current TaskList lists and history with this backup?',
      detail: `${file.name}\n\nAll active and archived lists, tasks/subtasks, history, IDs, and milestone data will be restored. This replaces current data rather than merging it. ID counters keep their highest values to prevent reuse. The server saves a recovery backup first.\n\nYour login password and browser preferences are kept.`,
      confirmLabel: 'Restore'
    })) return;
    if (backup) {
      restoreAttempted = true;
      clearTaskSelection();
      tasks = [];
      renderTasks();
    }
    setStatus(backup ? 'Restoring backup...' : `Importing into ${listName}...`);
    const result = backup
      ? await api('/api/import/backup', { method: 'POST', headers: { 'Content-Type': 'application/zip' }, body: file }, false)
      : await api(`/api/lists/${listId}/import/markdown`, {
          method: 'POST', headers: { 'Content-Type': 'text/markdown; charset=utf-8' }, body: await file.text()
        }, false);
    imported = true;
    if (backup) {
      clearDeepLink();
      currentView = 'open';
      updateViewMenu();
    } else {
      clearTaskSelection();
      tasks = [];
      renderTasks();
    }
    await refreshTransferData();
    setStatus(backup
      ? `Restored ${result.totals.lists} lists, ${result.totals.tasks} tasks, ${result.totals.subtasks} subtasks, and ${result.totals.events} history events. Recovery backup saved on the server.`
      : `Imported ${result.tasksImported} task${result.tasksImported === 1 ? '' : 's'} and ${result.subtasksImported} subtask${result.subtasksImported === 1 ? '' : 's'}.`);
  } catch (error) {
    let message = `${imported ? 'Data imported, but could not refresh the display' : 'Import error'}: ${error.message}`;
    if (restoreAttempted) {
      try { await refreshTransferData(); }
      catch {
        tasks = [];
        lists = [];
        archivedLists = [];
        currentListId = null;
        clearTaskSelection();
        renderTasks();
        renderFileMenu();
        updateListTitle();
        message += ' Refresh before trying again.';
      }
    }
    setStatus(message);
  } finally {
    setTransferBusy(false);
    if (imported) queueMicrotask(() => window.TaskMilestones?.check?.());
    if (!imported) openTransfer();
  }
});
