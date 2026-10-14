'use strict';

let selectionMode = localStorage.getItem('task-list-selection-mode') === 'true';
const selectedTaskIds = new Set();
let selectionAnchorId = null;
let bulkBusy = false;
let bulkMoveItems = null;
const bulkControls = document.querySelector('#bulkControls');
const selectedTaskCount = document.querySelector('#selectedTaskCount');
const bulkActions = document.querySelector('#bulkActions');

function visibleTaskItems() {
  return flattenTaskItems(tasksForCurrentView()).filter(item => item.listId === currentListId);
}

function selectedTaskItems() {
  return visibleTaskItems().filter(item => selectedTaskIds.has(item.universalId));
}

function clearTaskSelection() {
  selectedTaskIds.clear();
  selectionAnchorId = null;
}

function pruneTaskSelection() {
  const visible = new Set(visibleTaskItems().map(item => item.universalId));
  for (const id of selectedTaskIds) if (!visible.has(id)) selectedTaskIds.delete(id);
  if (!visible.has(selectionAnchorId)) selectionAnchorId = null;
}

function setSelectionMode(enabled) {
  if (bulkBusy) return;
  selectionMode = enabled;
  localStorage.setItem('task-list-selection-mode', String(selectionMode));
  if (!selectionMode) clearTaskSelection();
  closeFileMenu();
  closeTaskActionMenus();
  renderFileMenu();
  renderTasks();
}

document.addEventListener('keydown', event => {
  if (event.key !== 'Escape' || event.defaultPrevented || !selectionMode || bulkBusy ||
      document.querySelector('dialog[open]') || appWindow.inert) return;
  event.preventDefault();
  setSelectionMode(false);
});

function selectionMenuCommand() {
  const button = menuCommand('Select...', () => setSelectionMode(!selectionMode));
  button.setAttribute('role', 'menuitemradio');
  button.setAttribute('aria-checked', String(selectionMode));
  button.disabled = bulkBusy;
  button.firstElementChild.className = 'menu-check';
  return button;
}

function configureTaskSelection(row, item) {
  row.dataset.universalId = String(item.universalId);
  if (!selectionMode) return;
  row.classList.add('task-selectable');
  row.classList.toggle('task-selected', selectedTaskIds.has(item.universalId));
  row.setAttribute('aria-selected', String(selectedTaskIds.has(item.universalId)));
  row.tabIndex = 0;
  const checkbox = document.createElement('input');
  checkbox.type = 'checkbox';
  checkbox.className = 'task-selection-check';
  checkbox.checked = selectedTaskIds.has(item.universalId);
  checkbox.setAttribute('aria-label', `Select task #${item.displayId}: ${item.title}`);
  checkbox.addEventListener('click', event => {
    event.stopPropagation();
    toggleTaskSelection(item, event.shiftKey);
  });
  row.firstElementChild.prepend(checkbox);
  row.addEventListener('click', event => {
    if (event.target.closest('button, a, input, select, textarea')) return;
    toggleTaskSelection(item, event.shiftKey);
  });
  row.addEventListener('keydown', event => {
    if (event.target !== row || event.repeat || !['Enter', ' '].includes(event.key)) return;
    event.preventDefault();
    toggleTaskSelection(item, event.shiftKey);
  });
}

function toggleTaskSelection(item, extendRange = false) {
  if (!selectionMode || bulkBusy || item.listId !== currentListId) return;
  let range = null;
  if (extendRange && selectionAnchorId !== null) {
    const visible = visibleTaskItems();
    const start = visible.findIndex(candidate => candidate.universalId === selectionAnchorId);
    const end = visible.findIndex(candidate => candidate.universalId === item.universalId);
    if (start >= 0 && end >= 0) range = visible.slice(Math.min(start, end), Math.max(start, end) + 1);
  }
  if (!range) selectionAnchorId = item.universalId;
  const selecting = Boolean(range) || !selectedTaskIds.has(item.universalId);
  for (const target of range ?? [item]) {
    for (const selectedItem of target.isSubtask ? [target] : flattenTaskItems([target])) {
      if (selecting) selectedTaskIds.add(selectedItem.universalId);
      else selectedTaskIds.delete(selectedItem.universalId);
    }
  }
  for (const row of taskList.querySelectorAll('[data-universal-id]')) {
    const selected = selectedTaskIds.has(Number(row.dataset.universalId));
    row.classList.toggle('task-selected', selected);
    row.setAttribute('aria-selected', String(selected));
    const checkbox = row.querySelector('.task-selection-check');
    if (checkbox) checkbox.checked = selected;
  }
  updateBulkToolbar();
}

function selectedSubtreeRoots(items) {
  return items.filter(item => !items.some(parent => parent !== item && parent.listId === item.listId &&
    item.displayId.startsWith(`${parent.displayId}.`)));
}

function selectedTaskSummary(items) {
  const lines = items.slice(0, 12).map(item => `#${item.displayId} — ${item.title}`);
  if (items.length > 12) lines.push(`And ${items.length - 12} more selected tasks/subtasks.`);
  return lines.join('\n');
}

function bulkActionItems(action, items) {
  if (action === 'complete') return items.filter(item => item.status === 'Open');
  if (action === 'reopen') return items.filter(item => item.status !== 'Open');
  if (action === 'cancel') return items.filter(item => item.status !== 'Cancelled');
  const roots = selectedSubtreeRoots(items);
  // Destination root numbers increase, while the task list displays them
  // descending. Move bottom-to-top to preserve the selection's visible order.
  return action === 'move' ? roots.reverse() : roots;
}

function updateBulkToolbar() {
  bulkControls.hidden = !selectionMode;
  const selected = selectedTaskItems();
  selectedTaskCount.textContent = `Selected tasks: ${selected.length}`;
  if (!selectionMode) {
    bulkActions.replaceChildren();
    return;
  }
  const primary = selected.some(item => item.status === 'Open') || (!selected.length && currentView !== 'done')
    ? 'complete' : 'reopen';
  const labels = { complete: 'Complete', reopen: 'Reopen', cancel: 'Cancel' };
  const actions = Object.entries(labels).map(([action, label]) => [label, () => requestBulkAction(action),
    bulkBusy || !bulkActionItems(action, selected).length]);
  actions.push(
    ['Move...', () => openMove(selected[0], selected), bulkBusy || !selected.length || !lists.some(list => list.id !== currentListId)],
    ['Delete', () => requestBulkAction('delete'), bulkBusy || !selected.length]
  );
  bulkActions.replaceChildren(createSplitAction(labels[primary], () => requestBulkAction(primary), actions,
    'More actions for selected tasks', true, bulkBusy || !selected.length));
}

async function requestBulkAction(action, targetListId = null, selected = selectedTaskItems()) {
  if (bulkBusy || !selected.length) return;
  const items = bulkActionItems(action, selected);
  if (!items.length) return;
  const label = { complete: 'Complete', reopen: 'Reopen', cancel: 'Cancel', delete: 'Delete', move: 'Move' }[action];
  const destination = lists.find(list => list.id === targetListId);
  if (action === 'move' && (!destination || targetListId === currentListId)) return;
  const structural = action === 'delete' || action === 'move';
  const affected = structural ? items.reduce((sum, item) => sum + 1 + countDescendants(item), 0) : items.length;
  const detail = [selectedTaskSummary(selected)];
  if (structural) {
    detail.push(`${affected} tasks/subtasks will be ${action === 'delete' ? 'deleted' : 'moved'}, including descendants.`);
    if (action === 'delete') detail.push('Recorded task history is preserved for Stats.');
    else detail.push(`Destination: ${destination.name}. Selected subtasks become top-level tasks unless an ancestor is also selected. Universal IDs, status, dates, and hierarchy are preserved.`);
  } else {
    detail.push(`${items.length} selected tasks/subtasks will change. Status changes do not affect unselected descendants.`);
    if (items.length < selected.length) detail.push(`${selected.length - items.length} selected tasks/subtasks ${action === 'complete' ? 'are already closed' : 'already have the requested status'} and will be skipped.`);
  }
  bulkBusy = true;
  updateBulkToolbar();
  let processed = 0;
  let failure = null;
  let selectionLost = false;
  let confirmed = false;
  try {
    confirmed = await confirmAction({ title: `${label} Selected Tasks`,
      message: `${label} ${selected.length} selected ${selected.length === 1 ? 'task/subtask' : 'tasks/subtasks'}${destination ? ` to ${destination.name}` : ''}?`,
      detail: detail.join('\n\n'), confirmLabel: label });
    if (!confirmed) return;
    appWindow.inert = true;
    closeFileMenu();
    closeViewMenu();
    closeTaskActionMenus();
    for (const item of items) {
      setStatus(`${label}: ${processed + 1} of ${items.length}...`);
      if (action === 'delete') await api(itemEndpoint(item), { method: 'DELETE' }, false);
      else if (action === 'move') await jsonApi(`${itemEndpoint(item)}/move`, 'POST', { targetListId }, false);
      else {
        const status = { complete: 'Done', reopen: 'Open', cancel: 'Cancelled' }[action];
        const updated = await jsonApi(itemEndpoint(item), 'PATCH', { status }, false);
        applyUpdatedItem(tasks, updated);
      }
      processed++;
      for (const selection of selected) {
        if (selection.universalId === item.universalId || (structural && selection.displayId.startsWith(`${item.displayId}.`)))
          selectedTaskIds.delete(selection.universalId);
      }
    }
  } catch (error) {
    failure = error.message;
  } finally {
    if (confirmed && (structural || failure)) {
      try {
        tasks = await api(`/api/lists/${currentListId}/tasks`);
        await refreshListCounts();
      } catch (error) {
        failure = `${failure ? `${failure} ` : ''}Could not refresh the task list: ${error.message}`;
        clearTaskSelection();
        selectionLost = true;
      }
    }
    bulkBusy = false;
    appWindow.inert = false;
    renderTasks();
    if (confirmed) setStatus(failure
      ? `${processed} of ${items.length} actions confirmed. ${failure} ${selectionLost ? 'Selection cleared; refresh before retrying.' : 'Remaining visible tasks stay selected.'}`
      : `${label}: ${processed} task${processed === 1 ? '' : 's'} updated.`);
    if (confirmed) queueMicrotask(() => window.TaskMilestones?.check?.());
  }
}
