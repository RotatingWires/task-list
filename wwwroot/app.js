'use strict';

const taskList = document.querySelector('#taskList');
const emptyState = document.querySelector('#emptyState');
const statusText = document.querySelector('#statusText');
const taskCount = document.querySelector('#taskCount');
const appWindow = document.querySelector('main.window');
const newTaskForm = document.querySelector('#newTaskForm');
const newTaskTitle = document.querySelector('#newTaskTitle');

const fileMenu = document.querySelector('#fileMenu');
const fileDropdown = document.querySelector('#fileDropdown');
const viewMenu = document.querySelector('#viewMenu');
const viewDropdown = document.querySelector('#viewDropdown');
const viewChoices = [...viewDropdown.querySelectorAll('[data-view]')];
const aboutMenu = document.querySelector('#aboutMenu');
const aboutDialog = document.querySelector('#aboutDialog');
const titleAppName = document.querySelector('.title-app-name');
const titleListName = document.querySelector('#titleListName');
const aboutVersion = document.querySelector('#aboutVersion');

const confirmDialog = document.querySelector('#confirmDialog');
const confirmDialogTitle = document.querySelector('#confirmDialogTitle');
const confirmDialogMessage = document.querySelector('#confirmDialogMessage');
const confirmDialogDetail = document.querySelector('#confirmDialogDetail');
const confirmDialogCancel = document.querySelector('#confirmDialogCancel');
const confirmDialogAccept = document.querySelector('#confirmDialogAccept');

const editDialog = document.querySelector('#editDialog');
const editForm = document.querySelector('#editForm');
const editTaskId = document.querySelector('#editTaskId');
const editTitle = document.querySelector('#editTitle');
const editDescription = document.querySelector('#editDescription');
const cancelEdit = document.querySelector('#cancelEdit');

const subtaskDialog = document.querySelector('#subtaskDialog');
const subtaskForm = document.querySelector('#subtaskForm');
const subtaskParentId = document.querySelector('#subtaskParentId');
const subtaskTitle = document.querySelector('#subtaskTitle');
const subtaskDescription = document.querySelector('#subtaskDescription');
const cancelSubtask = document.querySelector('#cancelSubtask');

const moveDialog = document.querySelector('#moveDialog');
const moveForm = document.querySelector('#moveForm');
const moveTaskId = document.querySelector('#moveTaskId');
const moveSelectedTasks = document.querySelector('#moveSelectedTasks');
const moveListButton = document.querySelector('#moveListButton');
const moveListLabel = document.querySelector('#moveListLabel');
const moveListDropdown = document.querySelector('#moveListDropdown');
const moveNote = document.querySelector('#moveNote');
const cancelMove = document.querySelector('#cancelMove');

const infoDialog = document.querySelector('#infoDialog');
const infoTaskId = document.querySelector('#infoTaskId');
const infoParentTaskRow = document.querySelector('#infoParentTaskRow');
const infoParentTask = document.querySelector('#infoParentTask');
const infoUniversalId = document.querySelector('#infoUniversalId');
const infoTitle = document.querySelector('#infoTitle');
const infoDescription = document.querySelector('#infoDescription');
const infoStatus = document.querySelector('#infoStatus');
const infoCreated = document.querySelector('#infoCreated');
const infoUpdated = document.querySelector('#infoUpdated');
const infoCompleted = document.querySelector('#infoCompleted');
const infoCancelled = document.querySelector('#infoCancelled');
const infoReopened = document.querySelector('#infoReopened');

const listDialog = document.querySelector('#listDialog');
const listForm = document.querySelector('#listForm');
const listDialogTitle = document.querySelector('#listDialogTitle');
const listName = document.querySelector('#listName');
const listDescription = document.querySelector('#listDescription');
const cancelListEdit = document.querySelector('#cancelListEdit');
const manageListsDialog = document.querySelector('#manageListsDialog');
const manageListsBody = document.querySelector('#manageListsBody');
const manageListsTitleText = document.querySelector('#manageListsTitleText');
const manageListsUidTotal = document.querySelector('#manageListsUidTotal');
const openArchivesButton = document.querySelector('#openArchives');
const archivesDialog = document.querySelector('#archivesDialog');
const archivesBody = document.querySelector('#archivesBody');
const archivesTitleText = document.querySelector('#archivesTitleText');
const archivesUidTotal = document.querySelector('#archivesUidTotal');

const searchDialog = document.querySelector('#searchDialog');
const keywordSearchTab = document.querySelector('#keywordSearchTab');
const dateSearchTab = document.querySelector('#dateSearchTab');
const keywordSearchPanel = document.querySelector('#keywordSearchPanel');
const dateSearchPanel = document.querySelector('#dateSearchPanel');
const keywordSearchForm = document.querySelector('#keywordSearchForm');
const dateSearchForm = document.querySelector('#dateSearchForm');
const searchKeyword = document.querySelector('#searchKeyword');
const searchPeriod = document.querySelector('#searchPeriod');
const searchSummary = document.querySelector('#searchSummary');
const searchListFilterButton = document.querySelector('#searchListFilterButton');
const searchListFilterLabel = document.querySelector('#searchListFilterLabel');
const searchListFilterDropdown = document.querySelector('#searchListFilterDropdown');
const searchResults = document.querySelector('#searchResults');

let lists = [];
let archivedLists = [];
let tasks = [];
let editingItem = null;
let subtaskParent = null;
let movingItem = null;
let moveTargetListId = null;
let editingList = null;
let currentView = 'open';
let currentListId = Number(localStorage.getItem('task-list-current-list')) || null;
let highestUniversalId = 0;
let searchResultSet = [];
let searchListFilterValue = 'all';

const deepLinkMatch = /^\/task\/(\d+)\/?$/.exec(location.pathname);
const deepLinkUniversalId = deepLinkMatch ? Number(deepLinkMatch[1]) : null;
const hasDeepLink = Number.isSafeInteger(deepLinkUniversalId) && deepLinkUniversalId > 0;
let pendingDeepLinkDisplayId = null;

const PRIMARY_STATUS_ACTION = {
  Open: ['Complete', 'Done'],
  Done: ['Reopen', 'Open'],
  Cancelled: ['Reopen', 'Open']
};

// Buttons marked data-close="dialogId" close that dialog; the same code in every app.
document.addEventListener('click', event => {
  const button = event.target.closest('[data-close]');
  if (button && !button.disabled) document.getElementById(button.dataset.close)?.close(button.value || 'cancel');
});

function setStatus(message) {
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

async function apiResponse(url, options = {}) {
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

async function api(url, options = {}, checkMilestones = true) {
  const method = (options.method ?? 'GET').toUpperCase();
  const response = await apiResponse(url, options);
  const result = response.status === 204 ? null : await response.json();
  if (method !== 'GET' && checkMilestones) queueMicrotask(() => window.TaskMilestones?.check?.());
  return result;
}

function jsonApi(url, method, body, checkMilestones = true) {
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

async function loadAppVersion() {
  applyAppVersion(await frontendReleaseVersion());
}

function clearDeepLink() {
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

function findItemByDisplayId(items, displayId) {
  for (const item of items) {
    if (item.displayId === displayId) return item;
    const nested = findItemByDisplayId(item.subtasks ?? [], displayId);
    if (nested) return nested;
  }
  return null;
}

async function resolveUniversalLocation(universalId) {
  for (const list of [...lists, ...archivedLists]) {
    const listTasks = await api(`/api/lists/${list.id}/tasks`);
    const item = findUniversal(listTasks, universalId);
    if (item) return { listId: list.id, displayId: item.displayId };
  }
  return null;
}

function scrollAndHighlightTask(displayId, highlight = true) {
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

function formatDate(value) {
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

function confirmAction({
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

function actionButton(label, handler, disabled = false) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = label;
  button.disabled = disabled;
  button.addEventListener('click', handler);
  return button;
}

function appendActions(container, actions) {
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
