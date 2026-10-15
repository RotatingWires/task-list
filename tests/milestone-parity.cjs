const fs = require('node:fs');
const assert = require('node:assert/strict');
const { fixture } = require(process.argv[2]);
const cases = JSON.parse(fs.readFileSync(0, 'utf8'));
for (const scenario of cases) {
  const f = fixture();
  f.snapshot(scenario.snapshot);
  const threshold = record => {
    const label = record.label.split(' — ').at(-1);
    return label.startsWith('First ') ? 1 : Number(label.match(/[\d,]+/)[0].replaceAll(',', ''));
  };
  const keys = [];
  for (const kind of ['Created', 'Completed', 'Cancelled', 'Reopened', 'Deleted']) {
    const records = f.json('milestoneEventRecords(allRecordedEvents(),' + JSON.stringify(kind) + ')');
    for (const record of records) keys.push('global:' + kind.toLowerCase() + ':' + threshold(record));
  }
  for (const record of f.json('milestonePerListRecords(allRecordedEvents())'))
    keys.push('list:' + record.event.listId + ':' + record.type.toLowerCase() + ':' + threshold(record));
  for (const record of f.json('milestoneYearRecords(allRecordedEvents())'))
    keys.push('year:' + record.label.split(' — ')[0] + ':' + record.type.toLowerCase() + ':' + threshold(record));
  for (const record of f.json('milestoneUniversalIdRecords(allRecordedEvents())'))
    keys.push('universal:' + Number(record.label.match(/#[\d,]+/)[0].slice(1).replaceAll(',', '')));
  for (const value of f.json('continuingMilestoneThresholds(MILESTONE_EVENT_BASE_THRESHOLDS,allRecordedEvents().length).filter(n=>n<=allRecordedEvents().length)'))
    keys.push('global:recorded:' + value);
  assert.deepEqual(keys.sort(), scenario.expected.sort(), scenario.name);
  console.log(scenario.name + ': exact milestone-key parity (' + keys.length + ' milestones)');
}
