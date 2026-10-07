'use strict';

const COMPLETE_HOLD_MS = 800;

async function loadTasks() {
  if (!currentListId) return;

  setStatus('Loading...');
  try {
    tasks = await api(`/api/lists/${currentListId}/tasks`);
    renderTasks();
    setStatus('Ready');
    if (pendingDeepLinkDisplayId) {
      const displayId = pendingDeepLinkDisplayId;
      pendingDeepLinkDisplayId = null;
      scrollAndHighlightTask(displayId, true);
    }
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function tasksForCurrentView() {
  if (currentView === 'open') return tasks.filter(task => task.status === 'Open');
  if (currentView === 'done') return tasks.filter(task => task.status === 'Done');
  return tasks;
}

function renderTasks() {
  const visibleTasks = tasksForCurrentView();
  taskList.replaceChildren();
  emptyState.hidden = visibleTasks.length > 0;
  emptyState.textContent = currentView === 'all' ? 'No tasks.' : `No ${currentView} tasks.`;

  for (const task of visibleTasks)
    appendTaskTree(task, 0);

  const open = tasks.filter(task => task.status === 'Open').length;
  const done = tasks.filter(task => task.status === 'Done').length;
  const cancelled = tasks.filter(task => task.status === 'Cancelled').length;
  const subtaskTotal = tasks.reduce((sum, task) => sum + countDescendants(task), 0);
  taskCount.textContent = `${open} open / ${done} done / ${cancelled} cancelled / ${tasks.length} tasks / ${subtaskTotal} subtasks`;
}

function appendTaskTree(item, depth) {
  taskList.append(createTaskRow(item, depth));
  for (const child of item.subtasks ?? [])
    appendTaskTree(child, depth + 1);
}

function countDescendants(item) {
  return (item.subtasks ?? []).reduce((sum, child) => sum + 1 + countDescendants(child), 0);
}

function createDescriptionIndicator(item, sourceItems = tasks) {
  if (typeof item.description !== 'string' || !item.description.trim()) return null;

  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'task-description-indicator';
  button.title = 'Has description — open Task Information';
  button.setAttribute('aria-label', `Open description for task #${item.displayId}`);

  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('viewBox', '0 0 16 16');
  icon.setAttribute('aria-hidden', 'true');
  icon.innerHTML = `
    <path d="M3.5 1.5h6l3 3v10h-9z" fill="none" stroke="currentColor"/>
    <path d="M9.5 1.5v3h3" fill="none" stroke="currentColor"/>
    <path d="M5.5 7h5M5.5 9.5h5M5.5 12h3.5" fill="none" stroke="currentColor"/>
  `;
  button.append(icon);

  button.addEventListener('click', event => {
    event.stopPropagation();
    openInfo(item, sourceItems, true);
  });

  return button;
}

function createTaskRow(item, depth) {
  const isSubtask = depth > 0;
  const row = document.createElement('tr');
  row.classList.add(`status-${item.status.toLowerCase()}`);
  if (isSubtask) row.classList.add('subtask-row');
  row.dataset.depth = String(depth);
  row.dataset.displayId = item.displayId;
  row.style.setProperty('--tree-indent', `${Math.max(0, depth - 1) * 18}px`);
  row.style.setProperty('--row-indent', `${depth * 12}px`);

  const idCell = document.createElement('td');
  idCell.dataset.label = 'ID';

  const idButton = document.createElement('button');
  idButton.type = 'button';
  idButton.className = 'task-id-link';
  idButton.textContent = `#${item.displayId}`;
  idButton.title = `View information for ${isSubtask ? 'subtask' : 'task'} #${item.displayId}`;
  idButton.addEventListener('click', () => openInfo(item));
  idCell.append(idButton);

  const titleCell = document.createElement('td');
  titleCell.dataset.label = 'Task';
  titleCell.className = 'task-title';

  const titleContent = document.createElement('span');
  titleContent.className = 'task-title-content';

  const titleText = document.createElement('span');
  titleText.className = 'task-title-text';
  titleText.textContent = item.title;
  titleContent.append(titleText);

  const descriptionIndicator = createDescriptionIndicator(item);
  if (descriptionIndicator) titleContent.append(descriptionIndicator);

  if (isSubtask) {
    const tree = document.createElement('span');
    tree.className = 'task-title-tree';

    const marker = document.createElement('span');
    marker.className = 'task-tree-marker';
    marker.textContent = '└─';

    tree.append(marker, titleContent);
    titleCell.append(tree);
  } else {
    titleCell.append(titleContent);
  }

  const statusCell = document.createElement('td');
  statusCell.dataset.label = 'Status';
  statusCell.textContent = item.status;

  const actionsCell = document.createElement('td');
  actionsCell.dataset.label = 'Actions';
  actionsCell.className = 'task-actions';
  actionsCell.append(
    createTaskSplitAction(item),
    actionButton('Edit', () => openEdit(item))
  );

  row.append(idCell, titleCell, statusCell, actionsCell);
  return row;
}

function closeTaskActionMenus(except = null) {
  for (const menu of document.querySelectorAll('.task-action-dropdown:not([hidden])')) {
    if (menu === except) continue;
    menu.hidden = true;
    menu.closest('.task-split-group')?.querySelector('.task-action-arrow')
      ?.setAttribute('aria-expanded', 'false');
  }
}

function taskActionMenuButton(label, handler, disabled = false) {
  const button = document.createElement('button');
  button.type = 'button';
  button.disabled = disabled;
  button.setAttribute('role', 'menuitem');

  const spacer = document.createElement('span');
  spacer.className = 'menu-check';
  const text = document.createElement('span');
  text.className = 'menu-label';
  text.textContent = label;
  button.append(spacer, text);

  button.addEventListener('click', event => {
    event.stopPropagation();
    if (button.disabled) return;
    closeTaskActionMenus();
    handler();
  });
  return button;
}

function configureHoldToComplete(button, onComplete) {
  button.classList.add('hold-to-complete');
  button.style.setProperty('--complete-hold-duration', `${COMPLETE_HOLD_MS}ms`);
  button.title = `Hold for ${COMPLETE_HOLD_MS} ms to complete`;
  button.setAttribute('aria-label', `Hold Complete for ${COMPLETE_HOLD_MS} milliseconds`);

  const progress = document.createElement('span');
  progress.className = 'task-complete-progress';
  progress.setAttribute('aria-hidden', 'true');

  const label = document.createElement('span');
  label.className = 'task-complete-label';
  label.textContent = 'Complete';
  button.replaceChildren(progress, label);

  let timer = null;
  let activePointerId = null;
  let activeTouchId = null;
  let completed = false;
  let keyboardKey = null;

  function detachTouchEndGuards() {
    document.removeEventListener('touchend', handleDocumentTouchEnd, true);
    document.removeEventListener('touchcancel', handleDocumentTouchCancel, true);
  }

  function resetHold() {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    activePointerId = null;
    activeTouchId = null;
    keyboardKey = null;
    button.classList.remove('is-holding');
    detachTouchEndGuards();
  }

  function finishHold() {
    timer = null;
    completed = true;
    detachTouchEndGuards();
    button.classList.add('is-holding');
    button.disabled = true;

    Promise.resolve(onComplete()).finally(() => {
      if (!button.isConnected) return;
      completed = false;
      button.disabled = false;
      resetHold();
    });
  }

  function startHold() {
    if (timer !== null || completed || button.disabled) return;
    button.classList.add('is-holding');
    timer = window.setTimeout(finishHold, COMPLETE_HOLD_MS);
  }

  function pointIsInside(clientX, clientY) {
    const rect = button.getBoundingClientRect();
    const slop = 8;
    return clientX >= rect.left - slop &&
      clientX <= rect.right + slop &&
      clientY >= rect.top - slop &&
      clientY <= rect.bottom + slop;
  }

  function findTouch(list, identifier) {
    for (const touch of list)
      if (touch.identifier === identifier) return touch;
    return null;
  }

  function handleDocumentTouchEnd(event) {
    if (activeTouchId === null || completed) return;
    const touch = findTouch(event.changedTouches, activeTouchId);
    if (!touch) return;

    event.preventDefault();
    event.stopPropagation();
    resetHold();
  }

  function handleDocumentTouchCancel(event) {
    if (activeTouchId === null || completed) return;
    event.preventDefault();
    resetHold();
  }

  // Mobile touch handling is guarded at the document capture phase so a
  // quick release always cancels the timer even if the browser retargets
  // the touch away from the button.
  button.addEventListener('touchstart', event => {
    if (event.touches.length !== 1 || completed) return;
    const touch = event.changedTouches[0];
    if (!touch) return;

    event.preventDefault();
    event.stopPropagation();
    activeTouchId = touch.identifier;
    document.addEventListener('touchend', handleDocumentTouchEnd, { capture: true, passive: false });
    document.addEventListener('touchcancel', handleDocumentTouchCancel, { capture: true, passive: false });
    startHold();
  }, { passive: false });

  button.addEventListener('touchmove', event => {
    if (activeTouchId === null || completed) return;
    const touch = findTouch(event.touches, activeTouchId);
    if (!touch) return;

    event.preventDefault();
    if (!pointIsInside(touch.clientX, touch.clientY)) resetHold();
  }, { passive: false });

  // Pointer events cover mouse and pen. Touch pointers are deliberately
  // ignored because the guarded touch path above owns mobile behavior.
  button.addEventListener('pointerdown', event => {
    if (event.pointerType === 'touch') return;
    if (event.pointerType === 'mouse' && event.button !== 0) return;

    event.preventDefault();
    event.stopPropagation();
    activePointerId = event.pointerId;
    button.setPointerCapture?.(event.pointerId);
    startHold();
  });

  button.addEventListener('pointermove', event => {
    if (event.pointerId !== activePointerId || completed) return;
    if (!pointIsInside(event.clientX, event.clientY)) resetHold();
  });

  button.addEventListener('pointerup', event => {
    if (event.pointerId !== activePointerId) return;
    event.preventDefault();
    event.stopPropagation();
    if (!completed) resetHold();
    else activePointerId = null;
  });

  button.addEventListener('pointercancel', () => {
    if (!completed) resetHold();
  });
  button.addEventListener('lostpointercapture', () => {
    if (!completed) resetHold();
  });

  // A Complete action is never allowed to come from click activation.
  // stopImmediatePropagation also blocks any compatibility/synthetic click
  // listener that a mobile browser may try to run on this same button.
  button.addEventListener('click', event => {
    event.preventDefault();
    event.stopImmediatePropagation();
  }, true);

  button.addEventListener('keydown', event => {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    if (event.repeat || keyboardKey) return;
    keyboardKey = event.key;
    startHold();
  });

  button.addEventListener('keyup', event => {
    if (event.key !== keyboardKey) return;
    event.preventDefault();
    if (!completed) resetHold();
    else keyboardKey = null;
  });

  button.addEventListener('blur', () => {
    if (!completed) resetHold();
  });
}

function createTaskSplitAction(item) {
  const [primaryLabel, primaryStatus] = PRIMARY_STATUS_ACTION[item.status] ?? ['Edit', null];

  const group = document.createElement('div');
  group.className = 'task-split-group';

  const activatePrimary = () => {
    closeTaskActionMenus();
    if (primaryStatus) return updateItem(item, { status: primaryStatus });
    openEdit(item);
    return null;
  };

  const primary = document.createElement('button');
  primary.type = 'button';
  primary.textContent = primaryLabel;
  primary.classList.add('task-primary-action');

  if (primaryStatus === 'Done') configureHoldToComplete(primary, activatePrimary);
  else primary.addEventListener('click', activatePrimary);

  const arrow = document.createElement('button');
  arrow.type = 'button';
  arrow.className = 'task-action-arrow';
  arrow.textContent = '▼';
  arrow.title = 'More actions';
  arrow.setAttribute('aria-label', `More actions for task #${item.displayId}`);
  arrow.setAttribute('aria-haspopup', 'menu');
  arrow.setAttribute('aria-expanded', 'false');

  const menu = document.createElement('div');
  menu.className = 'menu-dropdown task-action-dropdown';
  menu.setAttribute('role', 'menu');
  menu.hidden = true;

  menu.append(taskActionMenuButton('Add Subtask', () => openSubtaskDialog(item)));
  if (item.status !== 'Cancelled')
    menu.append(taskActionMenuButton('Cancel', () => updateItem(item, { status: 'Cancelled' })));
  menu.append(
    taskActionMenuButton('Move...', () => openMove(item), lists.length <= 1),
    taskActionMenuButton('Delete', () => deleteItem(item))
  );

  arrow.addEventListener('click', event => {
    event.stopPropagation();
    const opening = menu.hidden;
    closeTaskActionMenus(menu);
    menu.hidden = !opening;
    arrow.setAttribute('aria-expanded', opening ? 'true' : 'false');
  });

  group.append(primary, arrow, menu);
  return group;
}

document.addEventListener('click', event => {
  if (!event.target.closest('.task-split-group')) closeTaskActionMenus();
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') closeTaskActionMenus();
});

newTaskForm.addEventListener('submit', async event => {
  event.preventDefault();
  const title = newTaskTitle.value.trim();
  if (!title || !currentListId) return;

  setStatus('Adding task...');
  try {
    await jsonApi(`/api/lists/${currentListId}/tasks`, 'POST', { title, description: '' });
    newTaskTitle.value = '';
    currentView = 'open';
    updateViewMenu();
    await Promise.all([loadTasks(), refreshListCounts()]);
    newTaskTitle.focus();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
});

function itemEndpoint(item) {
  return `/api/lists/${item.listId}/items/${encodeURIComponent(item.displayId)}`;
}

function applyUpdatedItem(items, updatedItem) {
  for (const item of items) {
    if (item.universalId === updatedItem.universalId) {
      const subtasks = item.subtasks ?? [];
      Object.assign(item, updatedItem);
      item.subtasks = subtasks;
      return true;
    }
    if (applyUpdatedItem(item.subtasks ?? [], updatedItem)) return true;
  }
  return false;
}

async function updateItem(item, changes) {
  setStatus(`Updating #${item.displayId}...`);
  try {
    const updatedItem = await jsonApi(itemEndpoint(item), 'PATCH', changes);
    if (!applyUpdatedItem(tasks, updatedItem)) {
      await loadTasks();
      return;
    }
    renderTasks();
    setStatus('Ready');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function openEdit(item) {
  editingItem = item;
  editTaskId.textContent = `#${item.displayId}`;
  editTitle.value = item.title;
  editDescription.value = item.description ?? '';
  editDialog.showModal();
  editTitle.focus();
  editTitle.select();
}

editForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (editingItem === null) return;

  const title = editTitle.value.trim();
  if (!title) return;

  const item = editingItem;
  const description = editDescription.value.trim();
  editDialog.close();
  editingItem = null;
  await updateItem(item, { title, description });
});

cancelEdit.addEventListener('click', () => {
  editDialog.close();
  editingItem = null;
});

function openSubtaskDialog(parent) {
  subtaskParent = parent;
  subtaskParentId.textContent = `#${parent.displayId} - ${parent.title}`;
  subtaskTitle.value = '';
  subtaskDescription.value = '';
  subtaskDialog.showModal();
  subtaskTitle.focus();
}

subtaskForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (!subtaskParent) return;

  const title = subtaskTitle.value.trim();
  if (!title) return;

  const parent = subtaskParent;
  const description = subtaskDescription.value.trim();
  subtaskDialog.close();
  subtaskParent = null;

  setStatus(`Adding subtask to #${parent.displayId}...`);
  try {
    await jsonApi(`/api/lists/${parent.listId}/items/${encodeURIComponent(parent.displayId)}/subtasks`, 'POST', { title, description });
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
});

cancelSubtask.addEventListener('click', () => {
  subtaskDialog.close();
  subtaskParent = null;
});

function closeMoveListMenu() {
  moveListDropdown.hidden = true;
  moveListButton.setAttribute('aria-expanded', 'false');
}

function setMoveTargetList(listId) {
  moveTargetListId = Number(listId);
  const selectedList = lists.find(list => list.id === moveTargetListId);
  moveListLabel.textContent = selectedList?.name ?? 'Choose a list';

  for (const option of moveListDropdown.querySelectorAll('[data-list-id]')) {
    const selected = Number(option.dataset.listId) === moveTargetListId;
    option.setAttribute('aria-checked', selected ? 'true' : 'false');
  }
}

function renderMoveListChoices(destinations) {
  moveListDropdown.replaceChildren();
  for (const list of destinations) {
    const option = document.createElement('button');
    option.type = 'button';
    option.setAttribute('role', 'menuitemradio');
    option.dataset.listId = String(list.id);

    const check = document.createElement('span');
    check.className = 'menu-check';
    const label = document.createElement('span');
    label.className = 'menu-label';
    label.textContent = list.name;
    label.title = list.description || list.name;
    option.append(check, label);
    option.addEventListener('click', event => {
      event.stopPropagation();
      setMoveTargetList(list.id);
      closeMoveListMenu();
      moveListButton.focus();
    });
    moveListDropdown.append(option);
  }

  setMoveTargetList(destinations[0]?.id ?? null);
}

function openMove(item) {
  const destinations = lists.filter(list => list.id !== item.listId);
  if (!destinations.length) {
    setStatus('Create another list before moving tasks.');
    return;
  }

  movingItem = item;
  moveTaskId.textContent = `#${item.displayId} — ${item.title}`;
  renderMoveListChoices(destinations);

  const descendants = countDescendants(item);
  const subtreeText = descendants > 0
    ? ` This will also move ${descendants} descendant subtask${descendants === 1 ? '' : 's'} with it.`
    : '';
  const rootText = item.isSubtask
    ? ' It will become a top-level task in the destination list.'
    : '';
  moveNote.textContent = `The moved task will receive the destination list's next task ID. Its Universal ID, status, dates, title, description, and child numbering are preserved.${rootText}${subtreeText}`;

  moveDialog.showModal();
  moveListButton.focus();
}

moveListButton.addEventListener('click', event => {
  event.stopPropagation();
  const willOpen = moveListDropdown.hidden;
  closeMoveListMenu();
  moveListDropdown.hidden = !willOpen;
  moveListButton.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

moveForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (!movingItem) return;

  const targetListId = Number(moveTargetListId);
  const targetList = lists.find(list => list.id === targetListId);
  if (!targetList || targetListId === movingItem.listId) return;

  const item = movingItem;
  setStatus(`Moving #${item.displayId} to ${targetList.name}...`);
  try {
    const result = await jsonApi(`${itemEndpoint(item)}/move`, 'POST', { targetListId });
    moveDialog.close();
    movingItem = null;
    moveTargetListId = null;
    await Promise.all([loadTasks(), refreshListCounts()]);
    setStatus(`Moved Universal ID ${result.universalId} to ${targetList.name} as #${result.displayId}.`);
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
});

cancelMove.addEventListener('click', () => {
  closeMoveListMenu();
  moveDialog.close();
  movingItem = null;
  moveTargetListId = null;
});

moveDialog.addEventListener('close', closeMoveListMenu);
document.addEventListener('click', event => {
  if (!event.target.closest('.move-list-menu')) closeMoveListMenu();
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') closeMoveListMenu();
});

function flattenInfoItems(items, output = []) {
  for (const item of items) {
    output.push(item);
    flattenInfoItems(item.subtasks ?? [], output);
  }
  return output;
}

function taskInfoHierarchy(item, sourceItems) {
  const all = flattenInfoItems(sourceItems);
  const prefix = `${item.displayId}.`;
  const rootDisplayId = String(item.displayId).split('.')[0];
  return {
    directChildren: all.filter(candidate => candidate.parentDisplayId === item.displayId).length,
    descendants: all.filter(candidate => candidate.displayId.startsWith(prefix)).length,
    siblings: all.filter(candidate => candidate.parentDisplayId === item.parentDisplayId && candidate.universalId !== item.universalId).length,
    rootTreeSize: all.filter(candidate => candidate.displayId === rootDisplayId || candidate.displayId.startsWith(`${rootDisplayId}.`)).length
  };
}

function parseInfoDate(value) {
  if (!value || value === 'Unknown') return null;
  const dateOnly = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (dateOnly) {
    const [, year, month, day] = dateOnly;
    const date = new Date(Number(year), Number(month) - 1, Number(day));
    return Number.isNaN(date.getTime()) ? null : date;
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

function hasInfoClockTime(value) {
  return typeof value === 'string' && /T\d{2}:\d{2}/.test(value);
}

function formatInfoDuration(ms) {
  if (ms == null || !Number.isFinite(ms) || ms < 0) return '—';
  const minute = 60_000;
  const hour = 60 * minute;
  const day = 24 * hour;
  if (ms < hour) return `${Math.max(1, Math.round(ms / minute))} min`;
  if (ms < day) return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(ms / hour)} hr`;
  return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(ms / day)} days`;
}

function taskInfoTerminalDuration(item) {
  if (!hasInfoClockTime(item.createdAt)) return null;
  const created = parseInfoDate(item.createdAt);
  const terminalRaw = item.status === 'Done'
    ? item.completedAt
    : item.status === 'Cancelled'
      ? item.cancelledAt
      : null;
  if (!created || !hasInfoClockTime(terminalRaw)) return null;
  const terminal = parseInfoDate(terminalRaw);
  if (!terminal) return null;
  const ms = terminal - created;
  return ms >= 0 ? ms : null;
}

let infoDescriptionHighlightTimer = null;

function setInfoDescriptionHighlight(enabled) {
  if (infoDescriptionHighlightTimer !== null) {
    clearTimeout(infoDescriptionHighlightTimer);
    infoDescriptionHighlightTimer = null;
  }

  const label = infoDescription.previousElementSibling;
  label?.classList.remove('info-description-highlight');
  infoDescription.classList.remove('info-description-highlight');

  if (!enabled) return;

  void infoDescription.offsetWidth;
  label?.classList.add('info-description-highlight');
  infoDescription.classList.add('info-description-highlight');
  infoDescriptionHighlightTimer = window.setTimeout(() => {
    label?.classList.remove('info-description-highlight');
    infoDescription.classList.remove('info-description-highlight');
    infoDescriptionHighlightTimer = null;
  }, 3200);
}

function openInfo(item, sourceItems = tasks, highlightDescription = false) {
  infoTaskId.textContent = `#${item.displayId}`;

  if (item.parentDisplayId) {
    const parent = findItemByDisplayId(sourceItems, item.parentDisplayId);
    infoParentTask.textContent = parent
      ? `#${parent.displayId} — ${parent.title}`
      : `#${item.parentDisplayId}`;
    infoParentTaskRow.hidden = false;
  } else {
    infoParentTask.textContent = '';
    infoParentTaskRow.hidden = true;
  }

  const hierarchy = taskInfoHierarchy(item, sourceItems);
  const createdDate = parseInfoDate(item.createdAt);

  infoUniversalId.textContent = String(item.universalId);
  infoTitle.textContent = item.title;
  infoDescription.textContent = item.description || '—';
  infoStatus.textContent = item.status;
  document.querySelector('#infoType').textContent = item.parentDisplayId ? 'Subtask' : 'Root task';
  document.querySelector('#infoNestingDepth').textContent = String(Math.max(0, String(item.displayId).split('.').length - 1));
  document.querySelector('#infoDirectChildren').textContent = hierarchy.directChildren.toLocaleString();
  document.querySelector('#infoDescendants').textContent = hierarchy.descendants.toLocaleString();
  document.querySelector('#infoSiblings').textContent = hierarchy.siblings.toLocaleString();
  document.querySelector('#infoRootTreeSize').textContent = hierarchy.rootTreeSize.toLocaleString();
  infoCreated.textContent = formatDate(item.createdAt);
  document.querySelector('#infoCurrentAge').textContent = item.status === 'Open' && createdDate
    ? formatInfoDuration(Date.now() - createdDate.getTime())
    : '—';
  document.querySelector('#infoTerminalTime').textContent = formatInfoDuration(taskInfoTerminalDuration(item));
  infoUpdated.textContent = formatDate(item.updatedAt);
  infoCompleted.textContent = formatDate(item.completedAt);
  infoCancelled.textContent = formatDate(item.cancelledAt);
  infoReopened.textContent = formatDate(item.reopenedAt);
  setInfoDescriptionHighlight(false);
  infoDialog.showModal();
  setInfoDescriptionHighlight(highlightDescription);
}

closeInfo.addEventListener('click', () => infoDialog.close());
infoDialog.addEventListener('close', () => setInfoDescriptionHighlight(false));

async function deleteItem(item) {
  const subtaskCount = countDescendants(item);
  const detailParts = [item.title];
  if (subtaskCount > 0)
    detailParts.push(`This will also delete ${subtaskCount} descendant subtask${subtaskCount === 1 ? '' : 's'}.`);
  detailParts.push('Recorded task history is preserved for Stats.');

  const confirmed = await confirmAction({
    title: item.isSubtask ? 'Delete Subtask' : 'Delete Task',
    message: `Delete ${item.isSubtask ? 'subtask' : 'task'} #${item.displayId}?`,
    detail: detailParts.join('\n\n'),
    confirmLabel: 'Delete'
  });
  if (!confirmed) return;

  setStatus(`Deleting #${item.displayId}...`);
  try {
    await api(itemEndpoint(item), { method: 'DELETE' });
    await Promise.all([loadTasks(), refreshListCounts()]);
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function chooseMarkdown() {
  if (!currentListId) return;
  markdownFile.value = '';
  markdownFile.click();
}

importMenu.addEventListener('click', chooseMarkdown);

markdownFile.addEventListener('change', async () => {
  const file = markdownFile.files?.[0];
  if (!file || !currentListId) return;

  setStatus(`Importing ${file.name}...`);
  try {
    const markdown = await file.text();
    const result = await api(`/api/lists/${currentListId}/import/markdown`, {
      method: 'POST',
      headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
      body: markdown
    });
    await Promise.all([loadTasks(), refreshListCounts()]);
    await loadStats().catch(() => {});
    setStatus(`Imported ${result.tasksImported} task${result.tasksImported === 1 ? '' : 's'} and ${result.subtasksImported} subtask${result.subtasksImported === 1 ? '' : 's'}.`);
  } catch (error) {
    setStatus(`Import error: ${error.message}`);
  }
});

function setView(view) {
  clearDeepLink();
  currentView = view;
  updateViewMenu();
  renderTasks();
  closeViewMenu();
}

function updateViewMenu() {
  for (const choice of viewChoices) {
    const selected = choice.dataset.view === currentView;
    choice.setAttribute('aria-checked', selected ? 'true' : 'false');
    choice.querySelector('.menu-check').textContent = selected ? '•' : '';
  }
}
