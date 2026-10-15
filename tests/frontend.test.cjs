const test = require('node:test');
const assert = require('node:assert/strict');
const { fixture, task } = require('./frontend-fixture.cjs');
const settle = () => new Promise(resolve => setImmediate(resolve));
const notice = { kind: 'created', threshold: 100, scope: 'list', scopeValue: '1', scopeLabel: 'homework', reachedAt: 'Unknown' };

test('background tabs do not consume pending celebrations; focus checks them', async () => {
  const f = fixture();
  let calls = 0;
  f.context.fetch = async () => { calls++; return { ok: true, json: async () => [notice] }; };
  await settle();
  await f.context.TaskMilestones.check();
  assert.equal(calls, 0);
  f.activate();
  f.trigger('focus');
  await settle();
  assert.equal(calls, 1);
  assert.match(f.document.body.textContent, /100 tasks created/);
  assert.match(f.document.body.textContent, /Unknown date/);
});

test('a write during an in-flight check causes another check instead of losing the request', async () => {
  const f = fixture();
  await settle();
  f.activate();
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  let calls = 0;
  f.context.fetch = async () => {
    calls++;
    if (calls === 1) { await pending; return { ok: true, json: async () => [] }; }
    return { ok: true, json: async () => [notice] };
  };
  const first = f.context.TaskMilestones.check();
  await settle();
  const second = f.context.TaskMilestones.check();
  release();
  await Promise.all([first, second]);
  assert.equal(calls, 2);
  assert.match(f.document.body.textContent, /100 tasks created/);
});

test('losing focus during a response keeps received notices until focus returns', async () => {
  const f = fixture();
  await settle();
  f.activate();
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  let calls = 0;
  f.context.fetch = async () => {
    calls++;
    if (calls === 1) await pending;
    return { ok: true, json: async () => calls === 1 ? [notice] : [] };
  };
  const checking = f.context.TaskMilestones.check();
  await settle();
  f.context.focused = false;
  f.document.visibilityState = 'hidden';
  release();
  await checking;
  assert.equal(f.document.body.children.length, 0);
  f.activate();
  f.trigger('visibilitychange');
  await settle();
  assert.match(f.document.body.textContent, /100 tasks created/);
});

test('concurrent checks coalesce; malformed and failed responses do not show fake notices', async () => {
  const f = fixture();
  await settle();
  f.activate();
  let calls = 0;
  f.context.fetch = async () => { calls++; return { ok: true, json: async () => ({ error: 'invalid' }) }; };
  await Promise.all(Array.from({ length: 8 }, () => f.context.TaskMilestones.check()));
  assert.equal(calls, 1);
  assert.equal(f.document.body.children.length, 0);
  f.context.fetch = async () => { throw new Error('offline'); };
  await f.context.TaskMilestones.check();
  assert.equal(f.document.body.children.length, 0);
});

function selection() {
  const f = fixture(['app.js', 'tasks.js', 'bulk.js']);
  const grandchild = task(13, { displayId: '1.1.1', isSubtask: true });
  const child = task(12, { displayId: '1.1', isSubtask: true, subtasks: [grandchild] });
  f.context.payload = [task(3), task(2), task(1, { subtasks: [child] })];
  f.run("tasks=payload; currentListId=1; currentView='all'; selectionMode=true;");
  return f;
}

test('root selection includes descendants exactly once and deselects the whole tree', () => {
  const f = selection();
  f.run('toggleTaskSelection(tasks[2])');
  assert.deepEqual(f.json('Array.from(selectedTaskIds)'), [1, 12, 13]);
  assert.equal(f.run('selectedTaskItems().length'), 3);
  f.run('toggleTaskSelection(tasks[2])');
  assert.equal(f.run('selectedTaskIds.size'), 0);
});

test('shift selection uses visible order and expands selected roots without duplicates', () => {
  const f = selection();
  f.run('toggleTaskSelection(tasks[0]); toggleTaskSelection(tasks[2], true)');
  assert.deepEqual(f.json('Array.from(selectedTaskIds)'), [3, 2, 1, 12, 13]);
  assert.equal(f.run('selectedTaskItems().length'), 5);
});

test('bulk move preserves visible root order independent of missing or backdated dates', () => {
  const f = selection();
  f.run('toggleTaskSelection(tasks[0]); toggleTaskSelection(tasks[2], true)');
  assert.deepEqual(f.json("bulkActionItems('move', selectedTaskItems()).map(task=>task.universalId)"), [1, 2, 3]);
  assert.deepEqual(f.json("bulkActionItems('delete', selectedTaskItems()).map(task=>task.universalId)"), [3, 2, 1]);
});

test('ancestor deduplication respects list identity and exact ID boundaries', () => {
  const f = selection();
  f.context.candidates = [task(1), task(2, { displayId: '1.1', isSubtask: true }),
    task(3, { listId: 2, displayId: '1.1', isSubtask: true }), task(10)];
  assert.deepEqual(f.json('selectedSubtreeRoots(candidates).map(task=>task.universalId)'), [1, 3, 10]);
});

test('view changes and moves prune stale selections by Universal ID', () => {
  const f = selection();
  f.run("toggleTaskSelection(tasks[0]); tasks[0].status='Done'; currentView='open'; pruneTaskSelection()");
  assert.equal(f.run('selectedTaskIds.size'), 0);
  f.run("currentView='all'; toggleTaskSelection(tasks[1]); tasks[1].listId=2; pruneTaskSelection()");
  assert.equal(f.run('selectedTaskIds.size'), 0);
});

test('status actions skip only the corresponding unchanged statuses', () => {
  const f = selection();
  f.context.candidates = [task(1), task(2, { status: 'Done' }), task(3, { status: 'Cancelled' })];
  assert.deepEqual(f.json("bulkActionItems('complete', candidates).map(task=>task.universalId)"), [1]);
  assert.deepEqual(f.json("bulkActionItems('reopen', candidates).map(task=>task.universalId)"), [2, 3]);
  assert.deepEqual(f.json("bulkActionItems('cancel', candidates).map(task=>task.universalId)"), [1, 2]);
});

test('confirmed bulk move submits each selected subtree once and checks milestones once', async () => {
  const f = selection();
  f.run("lists=[{id:1,name:'A'},{id:2,name:'B'}]; toggleTaskSelection(tasks[0]); toggleTaskSelection(tasks[2],true);");
  const moved = [];
  f.context.jsonApi = async url => { moved.push(url); return {}; };
  f.context.api = async () => [];
  f.context.confirmAction = async () => true;
  f.context.refreshListCounts = async () => {};
  let checks = 0;
  f.context.TaskMilestones = { check: () => { checks++; } };
  await f.run("requestBulkAction('move', 2)");
  await settle();
  assert.deepEqual(moved, ['/api/lists/1/items/1/move', '/api/lists/1/items/2/move', '/api/lists/1/items/3/move']);
  assert.equal(f.run('selectedTaskIds.size'), 0);
  assert.equal(checks, 1);
});

test('a partial bulk failure keeps only remaining visible tasks selected for retry', async () => {
  const f = selection();
  f.run("lists=[{id:1,name:'A'},{id:2,name:'B'}]; toggleTaskSelection(tasks[0]); toggleTaskSelection(tasks[2],true);");
  let calls = 0;
  f.context.jsonApi = async () => { if (++calls === 2) throw new Error('move failed'); return {}; };
  f.context.api = async () => f.context.payload.slice(0, 2);
  f.context.confirmAction = async () => true;
  f.context.refreshListCounts = async () => {};
  await f.run("requestBulkAction('move', 2)");
  assert.deepEqual(f.json('Array.from(selectedTaskIds)'), [3, 2]);
  assert.match(f.get('#statusText').textContent, /1 of 3 actions confirmed/);
  assert.equal(f.run('bulkBusy'), false);
});
