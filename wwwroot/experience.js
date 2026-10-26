// Shared interaction helpers for the browser and the desktop WebView host.
let activeModal = null;
let modalReturnFocus = null;
let recentProjects = [];

function showModal(id, focusId) {
  if (activeModal === id) return;
  if (activeModal) hideModal(activeModal);
  modalReturnFocus = document.activeElement;
  activeModal = id;
  $(id).hidden = false;
  document.querySelector('.app-shell').inert = true;
  document.body.classList.add('modal-open');
  $(focusId)?.focus();
}

function hideModal(id) {
  $(id).hidden = true;
  if (activeModal !== id) return;
  activeModal = null;
  document.querySelector('.app-shell').inert = false;
  document.body.classList.remove('modal-open');
  modalReturnFocus?.focus();
}

function feedback(id, message, error = false) {
  const element = $(id);
  element.hidden = !message;
  element.textContent = message;
  element.classList.toggle('error', error);
}

function normalizeProjectPath(value) { return value.trim().replace(/^"|"$/g, '').replace(/[\\/]+$/, ''); }

function setProjectBusy(busy) {
  state.projectBusy = busy;
  for (const id of ['openButton', 'projectButton', 'sampleButton', 'liveButton', 'refreshButton']) $(id).disabled = busy;
  $('pathInput').disabled = busy;
  $('projectModal').querySelectorAll('[data-browse], [data-recent-path]').forEach(el => el.disabled = busy);
  document.querySelector('main').setAttribute('aria-busy', String(busy));
  $('loadStatus').hidden = !busy;
  $('loadStatus').textContent = busy ? '正在读取项目并分析模型与报表，请稍候…' : '';
  if (!busy) $('refreshButton').disabled = !state.data;
}

function renderRecentProjects() {
  $('recentProjects').innerHTML = recentProjects.length ? recentProjects.map(path => `<button class="recent-project" data-recent-path="${escapeHtml(path)}"><b>${escapeHtml(path.split(/[\\/]/).pop())}</b><small>${escapeHtml(path)}</small></button>`).join('') : '<p class="muted">打开本地项目后，会在这里保留最近 6 个路径。</p>';
  $('recentProjects').querySelectorAll('button').forEach(button => button.addEventListener('click', () => {
    $('pathInput').value = button.dataset.recentPath;
    feedback('projectFeedback', '');
    $('pathInput').focus();
  }));
}

function rememberProject(path) {
  recentProjects = [path, ...recentProjects.filter(x => x.toLowerCase() !== path.toLowerCase())].slice(0, 6);
  try { localStorage.setItem('powertools.recentProjects', JSON.stringify(recentProjects)); } catch {}
  window.chrome?.webview?.postMessage({type: 'save-recent-projects', paths: recentProjects});
  renderRecentProjects();
}

function browseProject(target, kind = 'folder') {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage({type: 'browse-project', target, kind});
  } else {
    $(target).focus();
    toast('浏览器模式请粘贴本机项目路径；桌面版支持选择目录和文件。');
  }
}

function renderExperience() {
  const data = state.data;
  const demo = data.path === '内置演示数据';
  $('sourceBadge').textContent = demo ? '示例数据' : data.liveModel ? '实时只读' : '本地项目';
  $('sourceBadge').classList.toggle('demo', demo);
  $('sourceNotice').hidden = !demo && data.pages.length > 0;
  $('sourceNotice').textContent = demo ? '你正在体验内置示例。点击右上角“打开项目”，开始分析自己的模型与报表。' : '当前来源没有报表页面。模型分析可正常使用；书签和页面布局需要包含报表定义的 PBIP 项目。';
  document.querySelectorAll('.search-box input').forEach(input => {
    if (!input.getAttribute('aria-label')) input.setAttribute('aria-label', input.placeholder);
  });
  document.querySelectorAll('.nav-item[data-view]').forEach(button => {
    if (button.dataset.view === state.view) button.setAttribute('aria-current', 'page');
    else button.removeAttribute('aria-current');
  });
  $('projectName').title = data.name;
  bindOverviewLinks();
}

function bindOverviewLinks() {
  $('pageOverview').querySelectorAll('.page-line[data-page]').forEach(el => {
    el.tabIndex = 0; el.setAttribute('role', 'button');
    const open = () => { state.selectedPage = el.dataset.page; state.layoutBookmark = null; state.selectedVisual = null; renderPageSelect(); renderBookmarkSelect(); switchView('layout'); renderLayout(); };
    el.onclick = open; el.onkeydown = event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(); } };
  });
  $('attention').querySelectorAll('.attention-item[data-issue-index]').forEach(el => {
    el.tabIndex = 0; el.setAttribute('role', 'button');
    const open = () => {
      const issue = state.data.issues[Number(el.dataset.issueIndex)];
      if (!issue) return;
      resetQualityFilters(); $('qualitySearch').value = issue.objectName; renderQuality(); switchView('quality');
    };
    el.onclick = open; el.onkeydown = event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(); } };
  });
}

function resetQualityFilters() {
  state.qualityFilter = 'all'; state.qualityPage = 'all'; state.qualityCategory = 'all'; state.qualityLimit = 120; $('qualitySearch').value = ''; renderQuality();
}

function qualityObjectTarget(issue) {
  if (state.data.pages.some(page => page.name === issue.pageName)) return {view: 'layout'};
  const measure = allMeasures().find(item => `${item.table}[${item.name}]` === issue.objectName);
  if (measure) return {view: 'dax', key: measure.key};
  const table = state.data.tables.find(item => issue.objectName === item.name || String(issue.objectName).startsWith(item.name + '['));
  return table ? {view: 'model', table: table.name} : null;
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    toast('DAX 表达式已复制');
  } catch { toast('无法访问剪贴板，请选择表达式后按 Ctrl+C。', true); }
}

function bindExperience() {
  try { const paths = JSON.parse(localStorage.getItem('powertools.recentProjects') || '[]'); recentProjects = Array.isArray(paths) ? paths.filter(x => typeof x === 'string').slice(0, 6) : []; } catch {}
  renderRecentProjects();
  $('projectButton').addEventListener('click', () => { feedback('projectFeedback', ''); showModal('projectModal', 'pathInput'); });
  $('projectClose').onclick = $('projectCancel').onclick = () => hideModal('projectModal');
  $('projectModal').addEventListener('click', event => { if (event.target === $('projectModal')) hideModal('projectModal'); });
  $('refreshButton').addEventListener('click', () => state.data?.liveModel ? load('/api/live/current') : state.data?.path === '内置演示数据' ? load('/api/sample') : openProject());
  document.querySelectorAll('[data-browse]').forEach(button => button.addEventListener('click', () => browseProject(button.dataset.browse, button.dataset.browseKind)));
  $('qualityReset').onclick = resetQualityFilters;
  $('dependencyCenter').onclick = focusDependencyNode;
  $('compareSwap').onclick = () => { const baseline = $('compareBaseline').value; $('compareBaseline').value = $('compareCurrent').value; $('compareCurrent').value = baseline; state.comparison = null; feedback('compareStatus', ''); renderComparison(); };
  ['compareBaseline', 'compareCurrent'].forEach(id => $(id).addEventListener('input', () => { state.comparison = null; feedback('compareStatus', ''); renderComparison(); }));
  for (const input of document.querySelectorAll('.search-box input')) {
    const clear = document.createElement('button'); clear.type = 'button'; clear.className = 'search-clear'; clear.textContent = '×'; clear.setAttribute('aria-label', '清除' + input.placeholder); clear.hidden = !input.value;
    input.after(clear);
    input.addEventListener('input', () => clear.hidden = !input.value);
    clear.onclick = () => { input.value = ''; input.dispatchEvent(new Event('input')); input.focus(); };
  }
  document.addEventListener('keydown', event => {
    if (activeModal) {
      if (event.key === 'Escape') { event.preventDefault(); hideModal(activeModal); return; }
      if (event.key === 'Tab') {
        const items = [...$(activeModal).querySelectorAll('button:not(:disabled), input:not(:disabled), a[href], select')].filter(el => el.getClientRects().length);
        const first = items[0], last = items.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
      }
      return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'o') { event.preventDefault(); if (!state.projectBusy) $('projectButton').click(); }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') { const search = $(state.view)?.querySelector('.search-box input'); if (search) { event.preventDefault(); search.focus(); search.select(); } }
  });
  window.chrome?.webview?.addEventListener('message', event => {
    const message = event.data;
    if (message.type === 'project-selected' && ['pathInput', 'compareBaseline', 'compareCurrent'].includes(message.target) && typeof message.path === 'string') {
      $(message.target).value = message.path;
      $(message.target).dispatchEvent(new Event('input'));
      $(message.target).focus();
    }
    if (message.type === 'recent-projects' && Array.isArray(message.paths)) { recentProjects = message.paths.filter(x => typeof x === 'string').slice(0, 6); renderRecentProjects(); }
    if (message.type === 'desktop-error') toast(message.message, true);
  });
  window.chrome?.webview?.postMessage({type: 'get-recent-projects'});
}
