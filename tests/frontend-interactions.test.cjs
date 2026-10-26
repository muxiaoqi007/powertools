const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const webroot = path.resolve(__dirname, '../wwwroot');

function harness(file, initial = {}) {
  const elements = new Map();
  const get = id => {
    if (!elements.has(id)) elements.set(id, { value: '', innerHTML: '', textContent: '', disabled: false, hidden: false, focus() {}, setAttribute() {}, querySelectorAll() { return []; }, classList: { toggle() {} } });
    return elements.get(id);
  };
  const context = vm.createContext({ state: initial, $: get, document: { querySelectorAll: () => [] }, fmt: String, escapeHtml: String, console });
  vm.runInContext(fs.readFileSync(path.join(webroot, file), 'utf8'), context);
  return { context, get, run: code => vm.runInContext(code, context) };
}

test('dependency fit includes translated node bounds and keeps all nodes inside viewport', () => {
  const { context, get, run } = harness('dependencies.js', {});
  get('dependencyGraph').querySelector = () => ({});
  get('dependencyGraph').getBoundingClientRect = () => ({ width: 480, height: 500 });
  get('dependencyNodes').getBBox = () => ({ x: 250, y: 92, width: 1180, height: 1800 });
  get('dependencyViewport').setAttribute = () => {};
  run('fitDependencyGraph()');
  const t = context.state.dependencyTransform;
  assert.ok(t.x + 250 * t.scale >= 0);
  assert.ok(t.y + 92 * t.scale >= 0);
  assert.ok(t.x + (250 + 1180) * t.scale <= 480);
  assert.ok(t.y + (92 + 1800) * t.scale <= 440);
});

test('hidden dependency view does not corrupt current transform', () => {
  const initial = { dependencyTransform: { x: 10, y: 20, scale: 1 } };
  const { get, run } = harness('dependencies.js', initial);
  get('dependencyGraph').querySelector = () => ({});
  get('dependencyGraph').getBoundingClientRect = () => ({ width: 0, height: 0 });
  run('fitDependencyGraph()');
  assert.deepEqual(initial.dependencyTransform, { x: 10, y: 20, scale: 1 });
});

test('dependency traversal respects direction and terminates on cycles', () => {
  const edges = [
    { sourceId: 'A', targetId: 'B', sourceName: 'A', targetName: 'B', sourceType: 'measure', targetType: 'measure' },
    { sourceId: 'B', targetId: 'C', sourceName: 'B', targetName: 'C', sourceType: 'measure', targetType: 'column' },
    { sourceId: 'C', targetId: 'A', sourceName: 'C', targetName: 'A', sourceType: 'column', targetType: 'measure' },
  ];
  const { get, run } = harness('dependencies.js', { data: { dependencies: edges }, selectedDependency: 'A' });
  get('dependencyDirection').value = 'upstream'; get('dependencyDepth').value = '1';
  assert.equal(run('dependencySubgraph().nodes.map(x=>x.id).sort().join()'), 'A,B');
  get('dependencyDirection').value = 'downstream';
  assert.equal(run('dependencySubgraph().nodes.map(x=>x.id).sort().join()'), 'A,C');
  get('dependencyDepth').value = '99';
  assert.equal(run('dependencySubgraph().nodes.length'), 3);
});

test('browsing a bookmark does not change original canvas visibility', () => {
  const { run } = harness('bookmarks.js', { selectedBookmark: 'b1', layoutBookmark: null, data: { bookmarks: [{ name: 'b1', visualStates: [{ pageName: 'p', visualName: 'v', isHidden: true }] }] } });
  assert.equal(run("bookmarkVisualHidden('p',{name:'v',isHidden:false})"), false);
  run("state.layoutBookmark='b1'");
  assert.equal(run("bookmarkVisualHidden('p',{name:'v',isHidden:false})"), true);
});

test('comparison rejects identical Windows paths without a server request', async () => {
  const { context, get, run } = harness('compare.js');
  get('compareBaseline').value = 'D:/Project/'; get('compareCurrent').value = 'd:\\project';
  context.normalizeProjectPath = value => value.trim().replace(/[\\/]+$/, '');
  const messages = []; context.feedback = (_, text) => messages.push(text);
  context.fetch = () => { throw new Error('should not fetch'); };
  await run('runComparison()');
  assert.match(messages[0], /路径相同/);
});

test('safe change candidates exclude already hidden objects', () => {
  const initial = { data: {
    tables: [{ name: 'Sales', measures: [{ name: 'Hidden', isHidden: true }, { name: 'Visible', isHidden: false }], columns: [{ name: 'Secret', isHidden: true }] }],
    removalCandidates: [
      { status: 'candidate', objectType: 'measure', tableName: 'Sales', objectName: 'Hidden' },
      { status: 'candidate', objectType: 'measure', tableName: 'Sales', objectName: 'Visible' },
      { status: 'candidate', objectType: 'column', tableName: 'Sales', objectName: 'Secret' },
      { status: 'blocked', objectType: 'measure', tableName: 'Sales', objectName: 'Blocked' },
    ],
  } };
  const { run } = harness('safe-changes.js', initial);
  assert.deepEqual(run('safeCandidates().map(x=>x.objectName)'), ['Visible']);
  assert.equal(run('safeHiddenCandidates().length'), 2);
});

test('risk score is described with a readable level', () => {
  const { run } = harness('optimizer.js', {});
  assert.equal(run('riskLabel(35)'), '低风险');
  assert.equal(run('riskLabel(45)'), '中风险');
  assert.equal(run('riskLabel(70)'), '高风险');
});

test('quality reset restores the first result page', () => {
  const { context, run } = harness('experience.js', { qualityLimit: 500, qualityFilter: 'warning' });
  context.renderQuality = () => {};
  run('resetQualityFilters()');
  assert.equal(context.state.qualityLimit, 120);
  assert.equal(context.state.qualityFilter, 'all');
});

const usageFixture = {
  pages: [
    { pageName: 'p1', displayName: '首页', isHidden: false, isTooltip: false, isDrillthrough: false, visualCount: 5, dataVisualCount: 4, columnCount: 2, measureCount: 2, filterCount: 1, pageFilterCount: 1, visualFilterCount: 0, titleCount: 2, altTextCount: 0, topObjects: [{ objectType: 'measure', tableName: 'Sales', objectName: 'Revenue', visualCount: 2 }] },
    { pageName: 'p2', displayName: 'Tooltip页', isHidden: true, isTooltip: true, isDrillthrough: false, visualCount: 2, dataVisualCount: 1, columnCount: 1, measureCount: 0, filterCount: 0, pageFilterCount: 0, visualFilterCount: 0, titleCount: 0, altTextCount: 0, topObjects: [] },
    { pageName: 'p3', displayName: '钻取页', isHidden: false, isTooltip: false, isDrillthrough: true, visualCount: 2, dataVisualCount: 1, columnCount: 1, measureCount: 0, filterCount: 0, pageFilterCount: 0, visualFilterCount: 0, titleCount: 0, altTextCount: 0, topObjects: [] },
  ],
  objects: [
    { objectType: 'measure', tableName: 'Sales', objectName: 'Revenue', isHidden: false, usageStatus: 'page', pageCount: 1, visualCount: 2, pages: [{ name: 'p1', displayName: '首页' }] },
    { objectType: 'column', tableName: 'Sales', objectName: 'Unused', isHidden: true, usageStatus: 'unused', pageCount: 0, visualCount: 0, pages: [] },
    { objectType: 'column', tableName: 'Date', objectName: 'Date', isHidden: false, usageStatus: 'model', pageCount: 0, visualCount: 0, pages: [] },
  ],
};

test('usage page view filters by page type and search text', () => {
  const { context, run } = harness('usage.js', {});
  context.usageFixture = usageFixture;
  assert.equal(run("filterUsagePages(usageFixture.pages,{type:'normal'}).length"), 1);
  assert.equal(run("filterUsagePages(usageFixture.pages,{type:'tooltip'}).map(x=>x.pageName).join()"), 'p2');
  assert.equal(run("filterUsagePages(usageFixture.pages,{query:'钻取'}).length"), 1);
});

test('usage object view filters by status, type, table and page text', () => {
  const { context, run } = harness('usage.js', {});
  context.usageFixture = usageFixture;
  assert.deepEqual(run("filterUsageObjects(usageFixture.objects,{status:'unused'}).map(x=>x.objectName)"), ['Unused']);
  assert.deepEqual(run("filterUsageObjects(usageFixture.objects,{type:'measure'}).map(x=>x.objectName)"), ['Revenue']);
  assert.deepEqual(run("filterUsageObjects(usageFixture.objects,{table:'Date'}).map(x=>x.objectName)"), ['Date']);
  assert.equal(run("filterUsageObjects(usageFixture.objects,{query:'首页'}).length"), 1);
  assert.equal(run("filterUsageObjects(usageFixture.objects,{sort:'status'})[0].usageStatus"), 'unused');
  assert.equal(run("usageStatusLabel('model')"), '仅模型使用');
});

test('confirmation gate enables only an exact phrase and blocks during execution', () => {
  const { get, run } = harness('safe-changes.js', { safeChangePlan: { status: 'planned', confirmationPhrase: 'APPLY 123' } });
  get('safeConfirmation').value = 'apply 123'; run('updateSafeConfirmation()');
  assert.equal(get('safeRollbackButton').disabled, true);
  get('safeConfirmation').value = 'APPLY 123'; run('updateSafeConfirmation()');
  assert.equal(get('safeRollbackButton').disabled, false);
  run('state.safeBusy=true;updateSafeConfirmation()');
  assert.equal(get('safeRollbackButton').disabled, true);
});

function loadHarness() {
  const { context, get, run } = harness('experience.js', { data: { path: 'old' }, loadToken: 0 });
  const source = fs.readFileSync(path.join(webroot, 'app.js'), 'utf8');
  vm.runInContext(source.slice(source.indexOf('async function load('), source.indexOf('function switchView(')), context);
  context.setProjectBusy = value => { context.state.projectBusy = value; };
  context.feedback = () => {}; context.toast = () => {}; context.renderAll = () => {}; context.hideModal = () => {}; context.rememberProject = () => {};
  get('pathInput').removeAttribute = () => {};
  return { context, get, run };
}

test('failed project load preserves previously loaded data and restores controls', async () => {
  const { context, run } = loadHarness();
  context.fetch = async () => ({ ok: false, json: async () => ({ error: 'invalid path' }) });
  assert.equal(await run("load('/api/project/open')"), false);
  assert.equal(context.state.data.path, 'old');
  assert.equal(context.state.projectBusy, false);
});

test('parallel load clicks do not send duplicate requests', async () => {
  const { context, run } = loadHarness();
  let calls = 0; let release;
  context.fetch = () => { calls++; return new Promise(resolve => { release = resolve; }); };
  const pending = run("load('/api/sample')");
  assert.equal(await run("load('/api/sample')"), false);
  release({ ok: true, json: async () => ({ name: 'sample', path: '内置演示数据', tables: [], pages: [] }) });
  assert.equal(await pending, true); assert.equal(calls, 1);
});
