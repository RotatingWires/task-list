'use strict';

const freshStartDialog = document.querySelector('#freshStartDialog');
const freshStartOptions = [...freshStartDialog.querySelectorAll('[data-fresh-start]')];
const freshStartConfirmation = document.querySelector('#freshStartConfirmation');
const freshStartError = document.querySelector('#freshStartError');
const freshStartSize = document.querySelector('#freshStartSize');
const freshStartSubmit = document.querySelector('#freshStartSubmit');
const freshStartCancel = document.querySelector('#freshStartCancel');
const freshStartClose = document.querySelector('#freshStartClose');
let freshStartBusy = false;

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} bytes`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

function plural(count, noun) {
  return `${count.toLocaleString()} ${noun}${count === 1 ? '' : 's'}`;
}

function showFreshStartSummary(summary) {
  const counts = {
    finishedTasks: plural(summary.finishedTasks, 'task'),
    archivedLists: `${plural(summary.archivedLists, 'list')}, ${plural(summary.archivedListTasks, 'task')}`,
    everything: `${plural(summary.lists, 'list')}, ${plural(summary.tasks, 'task')}`,
    deletedTaskHistory: plural(summary.deletedTaskEvents, 'event'),
    allHistory: plural(summary.events, 'event'),
    recoveryBackups: `${plural(summary.recoveryBackups, 'file')}, ${formatBytes(summary.recoveryBackupBytes)}`
  };
  for (const [key, text] of Object.entries(counts))
    freshStartDialog.querySelector(`[data-fresh-count="${key}"]`).textContent = `(${text})`;
  freshStartSize.textContent = `Database size: ${formatBytes(summary.databaseBytes)}.`;
}

async function openFreshStart() {
  closeFileMenu();
  closeViewMenu();
  closeTaskActionMenus();
  for (const option of freshStartOptions) option.checked = option.defaultChecked;
  for (const count of freshStartDialog.querySelectorAll('[data-fresh-count]')) count.textContent = '';
  freshStartSize.textContent = 'Checking stored data...';
  freshStartConfirmation.value = '';
  freshStartError.textContent = '';
  freshStartDialog.showModal();
  freshStartCancel.focus();
  try { showFreshStartSummary(await api('/api/fresh-start')); }
  catch (error) { freshStartSize.textContent = `Could not read stored data: ${error.message}`; }
}

function setFreshStartBusy(busy) {
  freshStartBusy = busy;
  freshStartSubmit.disabled = busy;
  freshStartCancel.disabled = busy;
  freshStartClose.disabled = busy;
  appWindow.inert = busy;
}

freshStartCancel.addEventListener('click', () => freshStartDialog.close());
freshStartClose.addEventListener('click', () => freshStartDialog.close());
freshStartDialog.addEventListener('cancel', event => {
  if (freshStartBusy) event.preventDefault();
});

freshStartSubmit.addEventListener('click', async () => {
  if (freshStartBusy) return;
  const selections = Object.fromEntries(freshStartOptions.map(option => [option.dataset.freshStart, option.checked]));
  if (!Object.values(selections).some(Boolean)) {
    freshStartError.textContent = 'Choose at least one item to clear.';
    return;
  }
  if (freshStartConfirmation.value !== 'RESET SELECTED') {
    freshStartError.textContent = 'Type RESET SELECTED to confirm.';
    freshStartConfirmation.focus();
    return;
  }
  freshStartError.textContent = '';
  setFreshStartBusy(true);
  setStatus('Clearing selected data and compacting the database...');
  try {
    const result = await jsonApi('/api/fresh-start', 'POST', { confirmation: freshStartConfirmation.value, selections }, false);
    freshStartDialog.close();
    clearDeepLink();
    clearTaskSelection();
    tasks = [];
    renderTasks();
    await refreshTransferData();
    const removed = [
      result.removedTasks && plural(result.removedTasks, 'task'),
      result.removedLists && plural(result.removedLists, 'list'),
      result.removedEvents && plural(result.removedEvents, 'history event'),
      result.removedBackups && plural(result.removedBackups, 'recovery backup')
    ].filter(Boolean);
    setStatus(result.warning ?? `Fresh start: ${removed.length ? `removed ${removed.join(', ')}` : 'nothing to remove'}; freed ${formatBytes(result.freedBytes)}.`);
  } catch (error) {
    freshStartError.textContent = error.message;
    setStatus(`Fresh start error: ${error.message}`);
  } finally {
    setFreshStartBusy(false);
  }
});
