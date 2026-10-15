# TaskList / Stats integrity audit

Validated against disposable SQLite databases and the actual frontend JavaScript with DOM fixtures.

| Area | Cases exercised | Result |
| --- | --- | --- |
| Moves | Whole trees, independent subtasks, mixed statuses, missing dates, repeated moves, archived destinations, interrupted moves | UIDs/dates/statuses/child counters preserved; history stays with its original list; failures roll back. |
| Renames and identities | List/task renames, Unicode/quotes, reused list names, duplicate visible IDs across lists | Stable list/Universal IDs keep selections, links, histories, and counters separate. |
| Selection and bulk actions | Parent expansion, shift ranges, ancestor deduplication, visible order, status skipping, partial failures | Counts/order/remaining selection preserved; one milestone check per completed batch. |
| Status and deletion | Every status pair, repeat requests, same-clock repeated transitions, subtree/list deletion | Real transitions remain distinct; deleting trees records each descendant; deleted-list names survive. Fixed same-clock uniqueness and net open-flow counting. |
| Dates and milestones | Unknown/invalid/date-only/timed events, backdated inserts, all families, continuation thresholds, UTC offsets/year boundaries, large UID histories | Global/list counts include undated events; missing dates stay unknown. Threshold attribution follows recording order. Year totals need valid dates and use UTC. |
| Backup restoration | Stale derived counters, old schema without history revision, same final event, interrupted restore, post-backup ID allocation | Counters rebuilt, history revision changed, cache reset supported, rollback atomic, allocated IDs/numbers not reused. |
| Refresh and popups | Overlapping snapshots, late history responses, incremental/reset batches, background tabs, focus lost during responses, writes during checks | Refreshes cannot mix datasets; unfocused pages defer consumption; in-flight write checks rerun. |
| Stats workspaces | All 11 tabs in all-list and individual-list scopes, with mixed current/historical/moved/deleted/undated records | Rendered without exceptions; current counts and historical attribution retain their distinct meanings. |

The SQL suite includes 12 seeded sequences of 100 mixed operations, checking relationships and derived counters after every operation. Optional integration cases compare actual TL notification keys with Stats milestone computation at 1, 100, 500, 5,500, 10,500, and 11,000 creations: 296 matching milestone keys across the six datasets.

Run SQL checks with `python3 -m unittest discover -s tests -v`, TL frontend checks with `node tests/frontend.test.cjs`, and Stats frontend checks with the same Node command from its repository. The cross-app check requires the adjacent Stats checkout and Node on PATH, or `TASKLIST_TEST_NODE` pointing to Node.

Existing history is still the startup baseline, so upgrades do not replay old celebrations. Popups still use the documented atomic consume-before-display API: a lost consume response or closing a page after consumption can prevent display. Tests cover application state and SQL behavior, rather than a live NAS deployment.
