// Offline regression trial for tracker reconciliation. No GitHub calls are made.
import assert from 'node:assert/strict';
import { AmbiguousTrackerWrite, createTracker, trackerMarker, trackerTarget } from './tracker.mjs';

const fake = { comments: [], status: 'Todo', commentWrites: 0, commentUpdates: 0, projectWrites: [],
  failNextCommentUpdate: false, failNextProjectWrite: false };
const projectId = 'synthetic-project';
const itemId = 'synthetic-item';
const statusFieldId = 'synthetic-status';
const options = { Todo: 'synthetic-todo', 'In Progress': 'synthetic-progress' };
function runGh(args) {
  if (args[0] === 'issue' && args[1] === 'view') {
    assert.deepEqual(args.slice(2, 5), [String(trackerTarget.issue), '--repo', trackerTarget.repository]);
    return JSON.stringify({ state: 'OPEN', url: 'https://example.invalid/sandbox',
      comments: fake.comments.map((body, index) => ({ body, id: `synthetic-comment-${index}` })) });
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
  if (args[0] === 'project' && args[1] === 'view') {
    return JSON.stringify({ id: projectId, number: trackerTarget.project,
      owner: { login: trackerTarget.projectOwner, type: 'User' } });
  }
  if (args[0] === 'api' && args[1] === 'graphql') {
    if (fake.failNextCommentUpdate) {
      fake.failNextCommentUpdate = false;
      throw new Error('Synthetic lost comment response');
    }
    const body = args.find(argument => argument.startsWith('body='))?.slice('body='.length);
    assert.ok(body?.includes(trackerMarker));
    fake.comments[0] = body;
    fake.commentUpdates++;
    return JSON.stringify({ data: { updateIssueComment: { issueComment: { id: 'synthetic-comment-0' } } } });
  }
  if (args[0] === 'project' && args[1] === 'item-list') {
    return JSON.stringify({ items: [{ id: itemId, status: fake.status,
      content: { number: trackerTarget.issue, repository: trackerTarget.repository } }] });
  }
  if (args[0] === 'project' && args[1] === 'field-list') {
    return JSON.stringify({ fields: [{ id: statusFieldId, name: 'Status',
      options: Object.entries(options).map(([name, id]) => ({ name, id })) }] });
  }
  if (args[0] === 'project' && args[1] === 'item-edit') {
    if (fake.failNextProjectWrite) {
      fake.failNextProjectWrite = false;
      throw new Error('Synthetic lost project response');
    }
    const option = args[args.indexOf('--single-select-option-id') + 1];
    fake.status = Object.entries(options).find(([, id]) => id === option)?.[0];
    assert.ok(fake.status);
    fake.projectWrites.push(fake.status);
    return '';
  }
  throw new Error(`Unexpected fake gh command: ${args.join(' ')}`);
}

let tracker = createTracker(runGh);
assert.deepEqual(tracker.status(), { issue: 10, issueState: 'OPEN', markerComments: 0, project: 2,
  markerCurrent: false, projectStatus: 'Todo', issueUrl: 'https://example.invalid/sandbox' });
assert.throws(() => tracker.advance('lost-comment-response'), error => {
  assert.ok(error instanceof AmbiguousTrackerWrite);
  assert.equal(error.stage, 'comment');
  return true;
});
assert.equal(fake.commentWrites, 1);
tracker = createTracker(runGh); // A retry starts in a new process and reconciles remote state.
assert.equal(tracker.status().markerComments, 1);
assert.throws(() => tracker.advance('lost-project-response'), error => {
  assert.ok(error instanceof AmbiguousTrackerWrite);
  assert.equal(error.stage, 'project');
  return true;
});
assert.equal(fake.status, 'In Progress');
tracker = createTracker(runGh);
assert.deepEqual(tracker.advance().mutations, []);
assert.equal(fake.commentWrites, 1, 'Reconciliation must not duplicate the marked comment');
assert.deepEqual(fake.projectWrites, ['In Progress']);
tracker.reset();
assert.equal(fake.status, 'Todo');
assert.deepEqual(fake.projectWrites, ['In Progress', 'Todo']);
fake.comments[0] = `${trackerMarker}\nOutdated marked body`;
fake.failNextCommentUpdate = true;
assert.throws(() => tracker.advance(), error => {
  assert.ok(error instanceof AmbiguousTrackerWrite);
  assert.deepEqual(error.receipt.mutations, ['updated-marked-comment-outcome-unknown']);
  return true;
});
tracker = createTracker(runGh);
assert.throws(() => tracker.advance('lost-comment-response'), error => {
  assert.ok(error instanceof AmbiguousTrackerWrite);
  assert.equal(error.stage, 'comment');
  return true;
});
assert.equal(fake.commentWrites, 1);
assert.equal(fake.commentUpdates, 1);
assert.equal(tracker.status().markerCurrent, true);
fake.failNextProjectWrite = true;
assert.throws(() => tracker.advance(), error => {
  assert.ok(error instanceof AmbiguousTrackerWrite);
  assert.deepEqual(error.receipt.mutations, ['moved-project-to-in-progress-outcome-unknown']);
  return true;
});
tracker = createTracker(runGh);
assert.equal(tracker.status().projectStatus, 'Todo');
console.log('Offline tracker reconciliation trial passed.');
