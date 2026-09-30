'use strict';

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

  if (isSubtask) {
    const tree = document.createElement('span');
    tree.className = 'task-title-tree';

    const marker = document.createElement('span');
    marker.className = 'task-tree-marker';
    marker.textContent = '└─';

    const titleText = document.createElement('span');
    titleText.className = 'task-title-text';
    titleText.textContent = item.title;
    tree.append(marker, titleText);
    titleCell.append(tree);
  } else {
    titleCell.textContent = item.title;
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

function createTaskSplitAction(item) {
  const [primaryLabel, primaryStatus] = PRIMARY_STATUS_ACTION[item.status] ?? ['Edit', null];

  const group = document.createElement('div');
  group.className = 'task-split-group';

  const primary = actionButton(primaryLabel, () => {
    closeTaskActionMenus();
    if (primaryStatus) updateItem(item, { status: primaryStatus });
    else openEdit(item);
  });
  primary.classList.add('task-primary-action');

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

async function updateItem(item, changes) {
  setStatus(`Updating #${item.displayId}...`);
  try {
    await jsonApi(itemEndpoint(item), 'PATCH', changes);
    await loadTasks();
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

function openInfo(item, sourceItems = tasks) {
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

  infoUniversalId.textContent = String(item.universalId);
  infoTitle.textContent = item.title;
  infoDescription.textContent = item.description || '—';
  infoStatus.textContent = item.status;
  infoCreated.textContent = formatDate(item.createdAt);
  infoUpdated.textContent = formatDate(item.updatedAt);
  infoCompleted.textContent = formatDate(item.completedAt);
  infoCancelled.textContent = formatDate(item.cancelledAt);
  infoReopened.textContent = formatDate(item.reopenedAt);
  infoDialog.showModal();
}

closeInfo.addEventListener('click', () => infoDialog.close());

async function deleteItem(item) {
  const subtaskCount = countDescendants(item);
  const extra = subtaskCount > 0
    ? `\n\nThis will also delete ${subtaskCount} descendant subtask${subtaskCount === 1 ? '' : 's'}.`
    : '';

  if (!confirm(`Delete ${item.isSubtask ? 'subtask' : 'task'} #${item.displayId}?\n\n${item.title}${extra}`)) return;

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
