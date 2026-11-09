'use strict';

// $ and $$ take CSS selectors, the same helpers as TaskList Stats, NewsReview, and SpendTracker.
/** Creates an element with an optional class and text; buttons get type="button". The same helper in every app. */
function el(tag, className = '', text = '') {
  const node = document.createElement(tag);
  if (tag === 'button') node.type = 'button';
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}

const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];

const taskList = $('#taskList');
const emptyState = $('#emptyState');
const statusText = $('#statusText');
const taskCount = $('#taskCount');
const appWindow = $('main.window');
const newTaskForm = $('#newTaskForm');
const newTaskTitle = $('#newTaskTitle');

const fileMenu = $('#fileMenu');
const fileDropdown = $('#fileDropdown');
const viewMenu = $('#viewMenu');
const viewDropdown = $('#viewDropdown');
const viewChoices = [...viewDropdown.querySelectorAll('[data-view]')];
const aboutMenu = $('#aboutMenu');
const aboutDialog = $('#aboutDialog');
const titleAppName = $('.title-app-name');
const titleListName = $('#titleListName');
const aboutVersion = $('#aboutVersion');

const confirmDialog = $('#confirmDialog');
const confirmDialogTitle = $('#confirmDialogTitle');
const confirmDialogMessage = $('#confirmDialogMessage');
const confirmDialogDetail = $('#confirmDialogDetail');
const confirmDialogCancel = $('#confirmDialogCancel');
const confirmDialogAccept = $('#confirmDialogAccept');

const editDialog = $('#editDialog');
const editForm = $('#editForm');
const editTaskId = $('#editTaskId');
const editTitle = $('#editTitle');
const editDescription = $('#editDescription');
const cancelEdit = $('#cancelEdit');

const subtaskDialog = $('#subtaskDialog');
const subtaskForm = $('#subtaskForm');
const subtaskParentId = $('#subtaskParentId');
const subtaskTitle = $('#subtaskTitle');
const subtaskDescription = $('#subtaskDescription');
const cancelSubtask = $('#cancelSubtask');

const moveDialog = $('#moveDialog');
const moveForm = $('#moveForm');
const moveTaskId = $('#moveTaskId');
const moveSelectedTasks = $('#moveSelectedTasks');
const moveListButton = $('#moveListButton');
const moveListLabel = $('#moveListLabel');
const moveListDropdown = $('#moveListDropdown');
const moveNote = $('#moveNote');
const cancelMove = $('#cancelMove');

const infoDialog = $('#infoDialog');
const infoTaskId = $('#infoTaskId');
const infoParentTaskRow = $('#infoParentTaskRow');
const infoParentTask = $('#infoParentTask');
const infoUniversalId = $('#infoUniversalId');
const infoTitle = $('#infoTitle');
const infoDescription = $('#infoDescription');
const infoStatus = $('#infoStatus');
const infoCreated = $('#infoCreated');
const infoUpdated = $('#infoUpdated');
const infoCompleted = $('#infoCompleted');
const infoCancelled = $('#infoCancelled');
const infoReopened = $('#infoReopened');

const listDialog = $('#listDialog');
const listForm = $('#listForm');
const listDialogTitle = $('#listDialogTitle');
const listName = $('#listName');
const listDescription = $('#listDescription');
const cancelListEdit = $('#cancelListEdit');
const manageListsDialog = $('#manageListsDialog');
const manageListsBody = $('#manageListsBody');
const manageListsTitleText = $('#manageListsTitleText');
const manageListsUidTotal = $('#manageListsUidTotal');
const openArchivesButton = $('#openArchives');
const archivesDialog = $('#archivesDialog');
const archivesBody = $('#archivesBody');
const archivesTitleText = $('#archivesTitleText');
const archivesUidTotal = $('#archivesUidTotal');

const searchDialog = $('#searchDialog');
const keywordSearchTab = $('#keywordSearchTab');
const dateSearchTab = $('#dateSearchTab');
const keywordSearchPanel = $('#keywordSearchPanel');
const dateSearchPanel = $('#dateSearchPanel');
const keywordSearchForm = $('#keywordSearchForm');
const dateSearchForm = $('#dateSearchForm');
const searchKeyword = $('#searchKeyword');
const searchPeriod = $('#searchPeriod');
const searchSummary = $('#searchSummary');
const searchListFilterButton = $('#searchListFilterButton');
const searchListFilterLabel = $('#searchListFilterLabel');
const searchListFilterDropdown = $('#searchListFilterDropdown');
const searchResults = $('#searchResults');

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
  const button = el('button');
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
