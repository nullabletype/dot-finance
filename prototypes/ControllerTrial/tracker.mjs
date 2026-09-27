// Disposable manual tracker adapter for the explicitly authorised issue #10 trial.
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(import.meta.url);

export const trackerTarget = Object.freeze({
  repository: 'nullabletype/dot-finance',
  issue: 10,
  project: 2,
  projectOwner: 'nullabletype',
  allowedStatuses: ['Todo', 'In Progress'],
});
export const trackerMarker = '<!-- dot-finance-controller-trial:issue-4 -->';
const trackerComment = `${trackerMarker}\nManual issue #4 tracker-recovery trial. Each response-loss phase restarts and reconciles this exact comment before continuing. Existing operator authentication is not unattended authority.`;

export class AmbiguousTrackerWrite extends Error {
  constructor(stage, receipt) {
    super(`Tracker ${stage} write outcome deliberately treated as ambiguous`);
    this.name = 'AmbiguousTrackerWrite';
    this.stage = stage;
    this.receipt = receipt;
  }
}

function commandFailure(args, result) {
  const detail = result.error?.code || result.stderr?.trim() || `exit ${result.status}`;
  return new Error(`gh ${args.join(' ')} failed: ${detail}`);
}

function defaultRunGh(args) {
  const result = spawnSync('gh', args, { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
  if (result.status !== 0) throw commandFailure(args, result);
  return result.stdout;
}

function parseJson(output, operation) {
  try { return JSON.parse(output); }
  catch { throw new Error(`GitHub returned invalid JSON for ${operation}`); }
}

export function createTracker(runGh = defaultRunGh) {
  function issueState() {
    const issue = parseJson(runGh(['issue', 'view', String(trackerTarget.issue), '--repo', trackerTarget.repository,
      '--json', 'comments,state,url']), 'issue read');
    const marked = issue.comments.filter(comment => comment.body.includes(trackerMarker));
    return {
      state: issue.state,
      url: issue.url,
      markerComments: marked.length,
      markerCurrent: marked.length === 1 && marked[0].body === trackerComment,
      markerId: marked.length === 1 ? marked[0].id : undefined,
    };
  }

  function projectState() {
    const project = parseJson(runGh(['project', 'view', String(trackerTarget.project), '--owner', trackerTarget.projectOwner,
      '--format', 'json']), 'project read');
    if (project.number !== trackerTarget.project || project.owner?.login !== trackerTarget.projectOwner || project.owner?.type !== 'User') {
      throw new Error('Resolved Project does not match the fixed nullabletype user Project 2 target');
    }
    const listing = parseJson(runGh(['project', 'item-list', String(trackerTarget.project), '--owner', trackerTarget.projectOwner,
      '--format', 'json', '--limit', '100']), 'project item read');
    const matches = listing.items.filter(item => item.content?.number === trackerTarget.issue &&
      item.content?.repository === trackerTarget.repository);
    if (matches.length !== 1) throw new Error(`Expected exactly one Project item for ${trackerTarget.repository}#${trackerTarget.issue}`);
    return { id: project.id, itemId: matches[0].id, status: matches[0].status };
  }

  function snapshot() {
    const issue = issueState();
    const project = projectState();
    if (issue.state !== 'OPEN') throw new Error(`Sandbox issue must remain open, found ${issue.state}`);
    if (!trackerTarget.allowedStatuses.includes(project.status)) {
      throw new Error(`Sandbox Project status must be Todo or In Progress, found ${project.status}`);
    }
    if (issue.markerComments > 1) throw new Error('Duplicate marked tracker comments require owner review');
    return { issue: trackerTarget.issue, issueState: issue.state, markerComments: issue.markerComments,
      markerCurrent: issue.markerCurrent,
      project: trackerTarget.project, projectStatus: project.status, issueUrl: issue.url };
  }

  function moveProject(status) {
    if (!trackerTarget.allowedStatuses.includes(status)) throw new Error(`Tracker status ${status} is not allowed`);
    const project = projectState();
    const fields = parseJson(runGh(['project', 'field-list', String(trackerTarget.project), '--owner', trackerTarget.projectOwner,
      '--format', 'json']), 'project field read');
    const statusField = fields.fields.find(field => field.name === 'Status');
    const option = statusField?.options?.find(candidate => candidate.name === status);
    if (!statusField || !option) throw new Error(`Project Status option ${status} was not found`);
    runGh(['project', 'item-edit', '--id', project.itemId, '--project-id', project.id, '--field-id', statusField.id,
      '--single-select-option-id', option.id]);
  }

  function pending(stage, before, mutations) {
    throw new AmbiguousTrackerWrite(stage, { target: trackerTarget, outcome: 'pending', stage, before, mutations,
      nextAction: 'Re-read the marked comment and Project status before retrying' });
  }

  function writeOrPending(stage, args, before, mutations, mutation) {
    try { runGh(args); }
    catch { pending(stage, before, [...mutations, `${mutation}-outcome-unknown`]); }
    mutations.push(mutation);
  }

  function advance(simulation) {
    if (![undefined, 'lost-comment-response', 'lost-project-response'].includes(simulation)) {
      throw new Error('Unknown tracker simulation');
    }
    const before = snapshot();
    const mutations = [];
    let current = before;
    if (current.markerComments === 0) {
      writeOrPending('comment', ['issue', 'comment', String(trackerTarget.issue), '--repo', trackerTarget.repository,
        '--body', trackerComment], before, mutations, 'created-marked-comment');
      if (simulation === 'lost-comment-response') pending('comment', before, mutations);
      current = snapshot();
    } else if (!current.markerCurrent) {
      const issue = issueState();
      if (issue.markerComments !== 1 || !issue.markerId) throw new Error('Marked tracker comment changed during reconciliation');
      const mutation = 'mutation($id:ID!,$body:String!){updateIssueComment(input:{id:$id,body:$body}){issueComment{id}}}';
      writeOrPending('comment', ['api', 'graphql', '-f', `query=${mutation}`, '-F', `id=${issue.markerId}`,
        '-f', `body=${trackerComment}`], before, mutations, 'updated-marked-comment');
      if (simulation === 'lost-comment-response') pending('comment', before, mutations);
      current = snapshot();
    }
    if (current.markerComments !== 1 || !current.markerCurrent) throw new Error('Marked tracker comment was not reconciled');
    if (current.projectStatus === 'Todo') {
      try { moveProject('In Progress'); }
      catch { pending('project', before, [...mutations, 'moved-project-to-in-progress-outcome-unknown']); }
      mutations.push('moved-project-to-in-progress');
      if (simulation === 'lost-project-response') pending('project', before, mutations);
      current = snapshot();
    }
    if (current.projectStatus !== 'In Progress') throw new Error('Tracker advance did not reach In Progress');
    return { target: trackerTarget, outcome: 'complete', before, after: current, mutations };
  }

  function reset() {
    const before = snapshot();
    if (before.markerComments !== 1) throw new Error('Reset requires the single marked trial comment');
    const mutations = [];
    if (before.projectStatus === 'In Progress') {
      moveProject('Todo');
      mutations.push('moved-project-to-todo');
    }
    const after = snapshot();
    if (after.projectStatus !== 'Todo') throw new Error('Tracker reset did not reach Todo');
    return { target: trackerTarget, outcome: 'complete', before, after, mutations };
  }

  return { status: snapshot, advance, reset };
}

if (process.argv[1] && resolve(process.argv[1]) === source) {
  const [action = 'status', simulation] = process.argv.slice(2);
  const tracker = createTracker();
  try {
    const receipt = action === 'status' ? tracker.status() : action === 'advance' ? tracker.advance(simulation) :
      action === 'reset' ? tracker.reset() : (() => { throw new Error('Unknown tracker action'); })();
    console.log(JSON.stringify(receipt, null, 2));
  } catch (error) {
    if (error instanceof AmbiguousTrackerWrite) {
      console.log(JSON.stringify(error.receipt, null, 2));
      process.exitCode = 75;
    } else {
      console.error(error.message);
      process.exitCode = 1;
    }
  }
}
