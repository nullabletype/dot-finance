// Offline regression trial for tracker reconciliation. No GitHub calls are made.
import assert from 'node:assert/strict';
import { AmbiguousTrackerWrite, createTracker, trackerMarker, trackerTarget } from './tracker.mjs';

const fake = { comments: [], status: 'Todo', commentWrites: 0, projectWrites: [] };
const projectId = 'synthetic-project';
const itemId = 'synthetic-item';
const statusFieldId = 'synthetic-status';
const options = { Todo: 'synthetic-todo', 'In Progress': 'synthetic-progress' };
function runGh(args) {
  if (args[0] === 'issue' && args[1] === 'view') {
    assert.deepEqual(args.slice(2, 5), [String(trackerTarget.issue), '--repo', trackerTarget.repository]);
    return JSON.stringify({ state: 'OPEN', url: 'https://example.invalid/sandbox',
      comments: fake.comments.map(body => ({ body })) });
  }
  if (args[0] === 'issue' && args[1] === 'comment') {
    assert.equal(args[2], String(trackerTarget.issue));
    assert.equal(args[4], trackerTarget.repository);
    const body = args[6];
    assert.ok(body.includes(trackerMarker));
    fake.comments.push(body);
    fake.commentWrites++;
    return 'https://example.invalid/comment';
  }
  if (args[0] === 'project' && args[1] === 'view') return JSON.stringify({ id: projectId });
  if (args[0] === 'project' && args[1] === 'item-list') {
    return JSON.stringify({ items: [{ id: itemId, status: fake.status,
      content: { number: trackerTarget.issue, repository: trackerTarget.repository } }] });
  }
  if (args[0] === 'project' && args[1] === 'field-list') {
    return JSON.stringify({ fields: [{ id: statusFieldId, name: 'Status',
      options: Object.entries(options).map(([name, id]) => ({ name, id })) }] });
  }
  if (args[0] === 'project' && args[1] === 'item-edit') {
    const option = args[args.indexOf('--single-select-option-id') + 1];
    fake.status = Object.entries(options).find(([, id]) => id === option)?.[0];
    assert.ok(fake.status);
    fake.projectWrites.push(fake.status);
    return '';
  }
  throw new Error(`Unexpected fake gh command: ${args.join(' ')}`);
}

const tracker = createTracker(runGh);
assert.deepEqual(tracker.status(), { issue: 10, issueState: 'OPEN', markerComments: 0, project: 2,
  projectStatus: 'Todo', issueUrl: 'https://example.invalid/sandbox' });
assert.throws(() => tracker.advance('lost-comment-response'), error => {
  assert.ok(error instanceof AmbiguousTrackerWrite);
  assert.equal(error.stage, 'comment');
  return true;
});
assert.equal(fake.commentWrites, 1);
assert.equal(tracker.status().markerComments, 1);
assert.throws(() => tracker.advance('lost-project-response'), error => {
  assert.ok(error instanceof AmbiguousTrackerWrite);
  assert.equal(error.stage, 'project');
  return true;
});
assert.equal(fake.status, 'In Progress');
assert.deepEqual(tracker.advance().mutations, []);
assert.equal(fake.commentWrites, 1, 'Reconciliation must not duplicate the marked comment');
assert.deepEqual(fake.projectWrites, ['In Progress']);
tracker.reset();
assert.equal(fake.status, 'Todo');
assert.deepEqual(fake.projectWrites, ['In Progress', 'Todo']);
console.log('Offline tracker reconciliation trial passed.');
