/* Shared state, DOM references, API calls, and helpers for the other scripts. */
// $ and $$ take CSS selectors, the same helpers as TaskList Stats, NewsReview, and SpendTracker.
/** Creates an element with an optional class and text; buttons get type="button". The same helper in every app. */
export function el(tag, className = '', text = '') {
  const node = document.createElement(tag);
  if (tag === 'button') node.type = 'button';
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}

export const $ = selector => document.querySelector(selector);
export const $$ = selector => [...document.querySelectorAll(selector)];

export const taskList = $('#taskList');
export const emptyState = $('#emptyState');
const statusText = $('#statusText');
export const taskCount = $('#taskCount');
export const appWindow = $('main.window');
export const newTaskForm = $('#newTaskForm');
export const newTaskTitle = $('#newTaskTitle');

export const fileMenu = $('#fileMenu');
export const fileDropdown = $('#fileDropdown');
export const viewMenu = $('#viewMenu');
export const viewDropdown = $('#viewDropdown');
export const viewChoices = [...viewDropdown.querySelectorAll('[data-view]')];
export const aboutMenu = $('#aboutMenu');
export const aboutDialog = $('#aboutDialog');
const titleAppName = $('.title-app-name');
export const titleListName = $('#titleListName');
const aboutVersion = $('#aboutVersion');

const confirmDialog = $('#confirmDialog');
const confirmDialogTitle = $('#confirmDialogTitle');
const confirmDialogMessage = $('#confirmDialogMessage');
const confirmDialogDetail = $('#confirmDialogDetail');
const confirmDialogCancel = $('#confirmDialogCancel');
const confirmDialogAccept = $('#confirmDialogAccept');

export const editDialog = $('#editDialog');
export const editForm = $('#editForm');
export const editTaskId = $('#editTaskId');
export const editTitle = $('#editTitle');
export const editDescription = $('#editDescription');
export const cancelEdit = $('#cancelEdit');

export const subtaskDialog = $('#subtaskDialog');
export const subtaskForm = $('#subtaskForm');
export const subtaskParentId = $('#subtaskParentId');
export const subtaskTitle = $('#subtaskTitle');
export const subtaskDescription = $('#subtaskDescription');
export const cancelSubtask = $('#cancelSubtask');

export const moveDialog = $('#moveDialog');
export const moveForm = $('#moveForm');
export const moveTaskId = $('#moveTaskId');
export const moveSelectedTasks = $('#moveSelectedTasks');
export const moveListButton = $('#moveListButton');
export const moveListLabel = $('#moveListLabel');
export const moveListDropdown = $('#moveListDropdown');
export const moveNote = $('#moveNote');
export const cancelMove = $('#cancelMove');

export const infoDialog = $('#infoDialog');
export const infoTaskId = $('#infoTaskId');
export const infoParentTaskRow = $('#infoParentTaskRow');
export const infoParentTask = $('#infoParentTask');
export const infoUniversalId = $('#infoUniversalId');
export const infoTitle = $('#infoTitle');
export const infoDescription = $('#infoDescription');
export const infoStatus = $('#infoStatus');
export const infoCreated = $('#infoCreated');
export const infoUpdated = $('#infoUpdated');
export const infoCompleted = $('#infoCompleted');
export const infoCancelled = $('#infoCancelled');
export const infoReopened = $('#infoReopened');

export const listDialog = $('#listDialog');
export const listForm = $('#listForm');
export const listDialogTitle = $('#listDialogTitle');
export const listName = $('#listName');
export const listDescription = $('#listDescription');
export const cancelListEdit = $('#cancelListEdit');
export const manageListsDialog = $('#manageListsDialog');
export const manageListsBody = $('#manageListsBody');
export const manageListsTitleText = $('#manageListsTitleText');
export const manageListsUidTotal = $('#manageListsUidTotal');
export const openArchivesButton = $('#openArchives');
export const archivesDialog = $('#archivesDialog');
export const archivesBody = $('#archivesBody');
export const archivesTitleText = $('#archivesTitleText');
export const archivesUidTotal = $('#archivesUidTotal');

export const searchDialog = $('#searchDialog');
export const keywordSearchTab = $('#keywordSearchTab');
export const dateSearchTab = $('#dateSearchTab');
export const keywordSearchPanel = $('#keywordSearchPanel');
export const dateSearchPanel = $('#dateSearchPanel');
export const keywordSearchForm = $('#keywordSearchForm');
export const dateSearchForm = $('#dateSearchForm');
export const searchKeyword = $('#searchKeyword');
export const searchPeriod = $('#searchPeriod');
export const searchSummary = $('#searchSummary');
export const searchListFilterButton = $('#searchListFilterButton');
export const searchListFilterLabel = $('#searchListFilterLabel');
export const searchListFilterDropdown = $('#searchListFilterDropdown');
export const searchResults = $('#searchResults');

export let lists = [];
/** Other modules change lists through this; an imported binding is read-only. */
export function setLists(value) { return (lists = value); }
export let archivedLists = [];
/** Other modules change archivedLists through this; an imported binding is read-only. */
export function setArchivedLists(value) { return (archivedLists = value); }
export let tasks = [];
/** Other modules change tasks through this; an imported binding is read-only. */
export function setTasks(value) { return (tasks = value); }
export let editingItem = null;
/** Other modules change editingItem through this; an imported binding is read-only. */
export function setEditingItem(value) { return (editingItem = value); }
export let subtaskParent = null;
/** Other modules change subtaskParent through this; an imported binding is read-only. */
export function setSubtaskParent(value) { return (subtaskParent = value); }
export let movingItem = null;
/** Other modules change movingItem through this; an imported binding is read-only. */
export function setMovingItem(value) { return (movingItem = value); }
export let moveTargetListId = null;
/** Other modules change moveTargetListId through this; an imported binding is read-only. */
export function setMoveTargetListId(value) { return (moveTargetListId = value); }
export let editingList = null;
/** Other modules change editingList through this; an imported binding is read-only. */
export function setEditingList(value) { return (editingList = value); }
export let currentView = 'open';
/** Other modules change currentView through this; an imported binding is read-only. */
export function setCurrentView(value) { return (currentView = value); }
export let currentListId = Number(localStorage.getItem('task-list-current-list')) || null;
/** Other modules change currentListId through this; an imported binding is read-only. */
export function setCurrentListId(value) { return (currentListId = value); }
export let highestUniversalId = 0;
/** Other modules change highestUniversalId through this; an imported binding is read-only. */
export function setHighestUniversalId(value) { return (highestUniversalId = value); }
export let searchResultSet = [];
/** Other modules change searchResultSet through this; an imported binding is read-only. */
export function setSearchResultSet(value) { return (searchResultSet = value); }
export let searchListFilterValue = 'all';
/** Other modules change searchListFilterValue through this; an imported binding is read-only. */
export function setSearchListFilterValue(value) { return (searchListFilterValue = value); }

const deepLinkMatch = /^\/task\/(\d+)\/?$/.exec(location.pathname);
export const deepLinkUniversalId = deepLinkMatch ? Number(deepLinkMatch[1]) : null;
export const hasDeepLink = Number.isSafeInteger(deepLinkUniversalId) && deepLinkUniversalId > 0;
export let pendingDeepLinkDisplayId = null;
/** Other modules change pendingDeepLinkDisplayId through this; an imported binding is read-only. */
export function setPendingDeepLinkDisplayId(value) { return (pendingDeepLinkDisplayId = value); }

export const PRIMARY_STATUS_ACTION = {
  Open: ['Complete', 'Done'],
  Done: ['Reopen', 'Open'],
  Cancelled: ['Reopen', 'Open']
};

// Buttons marked data-close="dialogId" close that dialog; the same code in every app.
document.addEventListener('click', event => {
  const button = event.target.closest('[data-close]');
  if (button && !button.disabled) document.getElementById(button.dataset.close)?.close(button.value || 'cancel');
});

export function setStatus(message) {
  statusText.textContent = message;
}

const REQUEST_TIMEOUT_MS = 30000;

/** fetch that gives up after 30 seconds, like NewsReview, instead of waiting forever on a stopped server. */
async function timedFetch(url, options = {}, timeoutMs = REQUEST_TIMEOUT_MS) {
  try {
    return await fetch(url, timeoutMs ? { ...options, signal: AbortSignal.timeout(timeoutMs) } : options);
  } catch (error) {
    if (error.name === 'TimeoutError') throw new Error('The server did not respond within 30 seconds. Check that it is running.');
    throw error;
  }
}

export async function apiResponse(url, options = {}) {
  const { timeout = REQUEST_TIMEOUT_MS, ...fetchOptions } = options;
  const response = await timedFetch(url, { credentials: 'same-origin', ...fetchOptions }, timeout);
  if (response.status === 401) {
    window.location.replace('/login.html');
    throw new Error('Your session has expired.');
  }
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      if (body.error) message = body.error;
    } catch {}
    throw new Error(message);
  }
  return response;
}

export async function api(url, options = {}, checkMilestones = true) {
  const method = (options.method ?? 'GET').toUpperCase();
  const response = await apiResponse(url, options);
  const result = response.status === 204 ? null : await response.json();
  if (method !== 'GET' && checkMilestones) queueMicrotask(() => window.TaskMilestones?.check?.());
  return result;
}

export function jsonApi(url, method, body, checkMilestones = true) {
  return api(url, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  }, checkMilestones);
}

function applyAppVersion(version) {
  if (!version) return;
  const label = `TaskList v${version}`;
  // The version is its own span so phones can hide it from the title bar.
  if (titleAppName) titleAppName.replaceChildren('TaskList', Object.assign(document.createElement('span'), { className: 'title-version', textContent: ` v${version}` }));
  if (aboutVersion) aboutVersion.textContent = label;
}

async function frontendReleaseVersion() {
  try {
    const response = await timedFetch('/version.json', { cache: 'no-store', credentials: 'same-origin' });
    if (!response.ok) return '';
    const body = await response.json();
    return body.version || '';
  } catch {
    return '';
  }
}

export async function loadAppVersion() {
  applyAppVersion(await frontendReleaseVersion());
}

export function clearDeepLink() {
  if (!/^\/task\/\d+\/?$/.test(location.pathname)) return;
  history.replaceState(null, '', '/');
}

function findUniversal(items, universalId) {
  for (const item of items) {
    if (item.universalId === universalId) return item;
    const nested = findUniversal(item.subtasks ?? [], universalId);
    if (nested) return nested;
  }
  return null;
}

export function findItemByDisplayId(items, displayId) {
  for (const item of items) {
    if (item.displayId === displayId) return item;
    const nested = findItemByDisplayId(item.subtasks ?? [], displayId);
    if (nested) return nested;
  }
  return null;
}

export async function resolveUniversalLocation(universalId) {
  for (const list of [...lists, ...archivedLists]) {
    const listTasks = await api(`/api/lists/${list.id}/tasks`);
    const item = findUniversal(listTasks, universalId);
    if (item) return { listId: list.id, displayId: item.displayId };
  }
  return null;
}

export function scrollAndHighlightTask(displayId, highlight = true) {
  requestAnimationFrame(() => {
    const row = [...taskList.querySelectorAll('tr[data-display-id]')]
      .find(candidate => candidate.dataset.displayId === displayId);
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

    if (!highlight) return;
    row.classList.remove('task-visit-highlight');
    void row.offsetWidth;
    row.classList.add('task-visit-highlight');
    setTimeout(() => row.classList.remove('task-visit-highlight'), 3200);
  });
}

export function formatDate(value) {
  if (!value) return '—';
  if (/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    const [year, month, day] = value.split('-').map(Number);
    return new Date(year, month - 1, day).toLocaleDateString();
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

let confirmActionResolve = null;

function finishConfirmAction(result) {
  const resolve = confirmActionResolve;
  confirmActionResolve = null;
  if (confirmDialog.open) confirmDialog.close();
  resolve?.(result);
}

export function confirmAction({
  title = 'Confirm',
  message = '',
  detail = '',
  confirmLabel = 'OK'
} = {}) {
  if (confirmActionResolve) finishConfirmAction(false);

  confirmDialogTitle.textContent = title;
  confirmDialogMessage.textContent = message;
  confirmDialogDetail.textContent = detail;
  confirmDialogDetail.hidden = !detail;
  confirmDialogAccept.textContent = confirmLabel;

  return new Promise(resolve => {
    confirmActionResolve = resolve;
    confirmDialog.showModal();
    requestAnimationFrame(() => confirmDialogCancel.focus({ preventScroll: true }));
  });
}

confirmDialogCancel.addEventListener('click', () => finishConfirmAction(false));
confirmDialogAccept.addEventListener('click', () => finishConfirmAction(true));
confirmDialog.addEventListener('cancel', event => {
  event.preventDefault();
  finishConfirmAction(false);
});
confirmDialog.addEventListener('close', () => {
  if (confirmActionResolve) finishConfirmAction(false);
});

export function actionButton(label, handler, disabled = false) {
  const button = el('button');
  button.textContent = label;
  button.disabled = disabled;
  button.addEventListener('click', handler);
  return button;
}

export function appendActions(container, actions) {
  container.append(...actions.map(([label, handler, disabled]) => actionButton(label, handler, disabled)));
}

function descriptionEnterToSave(textarea, form) {
  textarea.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.shiftKey) return;
    event.preventDefault();
    form.requestSubmit();
  });
}

descriptionEnterToSave(editDescription, editForm);
descriptionEnterToSave(subtaskDescription, subtaskForm);
descriptionEnterToSave(listDescription, listForm);
