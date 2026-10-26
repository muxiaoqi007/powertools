function usageData() { return state.data?.usage || null; }

function bindUsage() {
  document.querySelectorAll('[data-usage-tab]').forEach(button => button.addEventListener('click', () => {
    state.usageTab = button.dataset.usageTab;
    renderUsage();
  }));
  $('usageSearch').addEventListener('input', renderUsage);
  for (const [id, key] of [
    ['usagePageType', 'usagePageType'],
    ['usagePageSort', 'usagePageSort'],
    ['usageObjectType', 'usageObjectType'],
    ['usageStatus', 'usageStatus'],
    ['usageTable', 'usageTable'],
    ['usageObjectSort', 'usageObjectSort'],
  ]) {
    $(id).addEventListener('change', () => { state[key] = $(id).value; renderUsage(); });
  }
  $('usageReset').addEventListener('click', resetUsageFilters);
}

function resetUsageFilters() {
  state.usagePageType = 'all'; state.usagePageSort = 'visuals';
  state.usageObjectType = 'all'; state.usageStatus = 'all'; state.usageTable = 'all'; state.usageObjectSort = 'pages';
  $('usageSearch').value = '';
  renderUsage();
}

function usagePageTypeLabels(page) {
  const labels = [];
  if (page.isHidden) labels.push('隐藏');
  if (page.isTooltip) labels.push('Tooltip');
  if (page.isDrillthrough) labels.push('钻取');
  return labels.length ? labels : ['普通'];
}

function usageStatusLabel(status) {
  return status === 'page' ? '页面使用' : status === 'model' ? '仅模型使用' : status === 'unused' ? '未引用' : status;
}

function usageObjectTypeLabel(type) { return type === 'measure' ? '度量值' : '字段'; }

function usagePageText(page) {
  const tops = (page.topObjects || []).map(item => `${item.tableName}[${item.objectName}]`).join(' ');
  return `${page.displayName} ${page.pageName} ${usagePageTypeLabels(page).join(' ')} ${tops}`.toLowerCase();
}

function usageObjectText(item) {
  const pages = (item.pages || []).map(page => page.displayName).join(' ');
  return `${item.objectName} ${item.tableName} ${usageObjectTypeLabel(item.objectType)} ${usageStatusLabel(item.usageStatus)} ${pages} ${item.tableName}[${item.objectName}]`.toLowerCase();
}

function filterUsagePages(pages, options = {}) {
  const query = (options.query || '').trim().toLowerCase();
  const rows = (pages || []).filter(page => {
    if (options.type === 'normal' && (page.isHidden || page.isTooltip || page.isDrillthrough)) return false;
    if (options.type === 'hidden' && !page.isHidden) return false;
    if (options.type === 'tooltip' && !page.isTooltip) return false;
    if (options.type === 'drillthrough' && !page.isDrillthrough) return false;
    return !query || usagePageText(page).includes(query);
  });
  const comparers = {
    visuals: (a, b) => b.visualCount - a.visualCount,
    columns: (a, b) => b.columnCount - a.columnCount,
    measures: (a, b) => b.measureCount - a.measureCount,
    filters: (a, b) => b.filterCount - a.filterCount,
    name: (a, b) => a.displayName.localeCompare(b.displayName, 'zh-CN'),
  };
  const compare = comparers[options.sort] || comparers.visuals;
  rows.sort((a, b) => compare(a, b) || a.displayName.localeCompare(b.displayName, 'zh-CN'));
  return rows;
}

function filterUsageObjects(objects, options = {}) {
  const query = (options.query || '').trim().toLowerCase();
  const table = (options.table || 'all').toLowerCase();
  const rows = (objects || []).filter(item => {
    if (options.type && options.type !== 'all' && item.objectType !== options.type) return false;
    if (options.status && options.status !== 'all' && item.usageStatus !== options.status) return false;
    if (table !== 'all' && item.tableName.toLowerCase() !== table) return false;
    return !query || usageObjectText(item).includes(query);
  });
  const statusRank = { unused: 0, model: 1, page: 2 };
  const comparers = {
    pages: (a, b) => b.pageCount - a.pageCount || b.visualCount - a.visualCount,
    visuals: (a, b) => b.visualCount - a.visualCount || b.pageCount - a.pageCount,
    status: (a, b) => (statusRank[a.usageStatus] ?? 3) - (statusRank[b.usageStatus] ?? 3),
    name: (a, b) => a.objectName.localeCompare(b.objectName, 'zh-CN'),
  };
  const compare = comparers[options.sort] || comparers.pages;
  rows.sort((a, b) => compare(a, b) || a.tableName.localeCompare(b.tableName, 'zh-CN') || a.objectName.localeCompare(b.objectName, 'zh-CN'));
  return rows;
}

function renderUsage() {
  if (!state.data) return;
  document.querySelectorAll('[data-usage-tab]').forEach(button => {
    const active = button.dataset.usageTab === state.usageTab;
    button.classList.toggle('active', active);
    button.setAttribute('aria-selected', String(active));
  });
  $('usagePageFilters').hidden = state.usageTab !== 'pages';
  $('usageObjectFilters').hidden = state.usageTab !== 'objects';
  $('usagePageType').value = state.usagePageType;
  $('usagePageSort').value = state.usagePageSort;
  $('usageObjectType').value = state.usageObjectType;
  $('usageStatus').value = state.usageStatus;
  $('usageObjectSort').value = state.usageObjectSort;

  const usage = usageData();
  if (!usage) {
    $('usageStats').innerHTML = '';
    $('usageResultCount').textContent = '';
    $('usageNotice').textContent = '当前项目快照没有使用情况数据。请重新扫描项目后再查看。';
    $('usageContent').innerHTML = '<div class="panel empty-state"><b>暂无使用情况数据</b>重新扫描项目后即可按页面和对象汇总。</div>';
    return;
  }
  renderUsageStats(usage.summary || {});
  syncUsageTableOptions();
  $('usageNotice').textContent = usageNoticeText(usage);
  if (state.usageTab === 'objects') renderUsageObjectsTable(usage);
  else renderUsagePagesTable(usage);
}

function renderUsageStats(summary) {
  const stats = [
    { value: summary.pageCount, label: '页面', title: `隐藏 ${fmt(summary.hiddenPageCount)} · Tooltip ${fmt(summary.tooltipPageCount)} · 钻取 ${fmt(summary.drillthroughPageCount)}` },
    { value: summary.dataVisualCount, label: '数据视觉', title: `全部视觉对象 ${fmt(summary.visualCount)}` },
    { value: summary.referencedColumnCount, label: '被引用字段', title: '至少被一个视觉对象引用的字段' },
    { value: summary.referencedMeasureCount, label: '被引用度量值', title: '至少被一个视觉对象引用的度量值' },
    { value: summary.crossPageObjectCount, label: '跨页复用对象', title: '被两个或更多页面使用的字段/度量值' },
    { value: summary.unusedObjectCount, label: '未引用对象', title: `仅模型使用 ${fmt(summary.modelOnlyObjectCount)} 个`, className: 'usage-stat-unused' },
  ];
  $('usageStats').innerHTML = stats.map(item => `<div class="mini-stat ${item.className || ''}" title="${escapeHtml(item.title || '')}"><b>${fmt(item.value || 0)}</b><span>${escapeHtml(item.label)}</span></div>`).join('');
}

function usageNoticeText(usage) {
  const summary = usage.summary || {};
  const parts = ['统计口径：视觉对象字段绑定；页面/报表筛选器字段暂未纳入。“未引用”表示当前项目内未发现视觉绑定或模型引用，不代表外部 PBIX/Excel 未使用。'];
  if (summary.hiddenPageCount || summary.tooltipPageCount || summary.drillthroughPageCount)
    parts.push(`页面包含隐藏 ${fmt(summary.hiddenPageCount)}、Tooltip ${fmt(summary.tooltipPageCount)}、钻取 ${fmt(summary.drillthroughPageCount)} 页。`);
  if (summary.unresolvedReferenceCount) {
    const list = (summary.unresolvedReferences || []).slice(0, 5).join('、');
    parts.push(`另有 ${fmt(summary.unresolvedReferenceCount)} 个字段引用未匹配到模型对象${list ? `：${list}` : ''}${summary.unresolvedReferenceCount > 5 ? ' 等' : ''}。`);
  }
  return parts.join(' ');
}

function syncUsageTableOptions() {
  const select = $('usageTable');
  if (!select) return;
  const names = [...new Set((state.data.tables || []).map(table => table.name))].sort((a, b) => a.localeCompare(b, 'zh-CN'));
  const signature = names.join('\u001f');
  if (select.dataset.signature !== signature) {
    select.innerHTML = ['<option value="all">全部表</option>', ...names.map(name => `<option value="${escapeHtml(name)}">${escapeHtml(name)}</option>`)].join('');
    select.dataset.signature = signature;
  }
  if (!names.includes(state.usageTable)) state.usageTable = 'all';
  select.value = state.usageTable;
}

function usageCoverage(count, total) {
  if (!total) return '<span class="muted">—</span>';
  const rate = Math.round((count || 0) / total * 100);
  return `${rate}%<small class="usage-sub">${fmt(count || 0)}/${fmt(total)}</small>`;
}

function usageTopObjects(objects) {
  if (!objects?.length) return '<span class="muted">—</span>';
  return objects.map(item => `<span class="usage-chip" title="${escapeHtml(`${item.tableName}[${item.objectName}]`)}">${item.objectType === 'measure' ? 'ƒ' : '│'} ${escapeHtml(item.objectName)} ×${fmt(item.visualCount)}</span>`).join('');
}

function usagePageChips(pages) {
  if (!pages?.length) return '<span class="muted">—</span>';
  const shown = pages.slice(0, 4).map(page => `<button type="button" class="usage-page-chip" data-usage-page-chip="${escapeHtml(page.name)}" title="${escapeHtml(page.displayName)}">${escapeHtml(page.displayName)}</button>`).join('');
  const rest = pages.length > 4 ? `<span class="usage-chip">+${fmt(pages.length - 4)}</span>` : '';
  return shown + rest;
}

function renderUsagePagesTable(usage) {
  const rows = filterUsagePages(usage.pages, { query: $('usageSearch').value, type: state.usagePageType, sort: state.usagePageSort });
  $('usageResultCount').textContent = `显示 ${fmt(rows.length)} / ${fmt((usage.pages || []).length)} 个页面`;
  if (!rows.length) {
    $('usageContent').innerHTML = '<div class="panel empty-state"><b>没有匹配的页面</b>调整搜索或页面类型后再试。</div>';
    return;
  }
  $('usageContent').innerHTML = `<div class="table-scroll usage-table-wrap"><table class="usage-table"><thead><tr><th>页面</th><th>视觉对象</th><th>字段</th><th>度量值</th><th>筛选器</th><th>标题覆盖</th><th>替代文本</th><th>常用对象</th></tr></thead><tbody>${rows.map(page => `<tr data-usage-page-row="${escapeHtml(page.pageName)}" tabindex="0" role="button"><td><b>${escapeHtml(page.displayName)}</b><small class="usage-sub">${escapeHtml(usagePageTypeLabels(page).join(' · '))}</small></td><td>${fmt(page.visualCount)}<small class="usage-sub">${fmt(page.dataVisualCount)} 数据视觉</small></td><td>${fmt(page.columnCount)}</td><td>${fmt(page.measureCount)}</td><td title="页面 ${fmt(page.pageFilterCount)} · 视觉 ${fmt(page.visualFilterCount)}">${fmt(page.filterCount)}</td><td>${usageCoverage(page.titleCount, page.dataVisualCount)}</td><td>${usageCoverage(page.altTextCount, page.dataVisualCount)}</td><td>${usageTopObjects(page.topObjects)}</td></tr>`).join('')}</tbody></table></div>`;
  document.querySelectorAll('#usageContent [data-usage-page-row]').forEach(row => {
    row.addEventListener('click', () => openUsagePage(row.dataset.usagePageRow));
    row.addEventListener('keydown', event => {
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); openUsagePage(row.dataset.usagePageRow); }
    });
  });
}

function renderUsageObjectsTable(usage) {
  const rows = filterUsageObjects(usage.objects, {
    query: $('usageSearch').value,
    type: state.usageObjectType,
    status: state.usageStatus,
    table: state.usageTable,
    sort: state.usageObjectSort,
  });
  $('usageResultCount').textContent = `显示 ${fmt(rows.length)} / ${fmt((usage.objects || []).length)} 个对象`;
  if (!rows.length) {
    $('usageContent').innerHTML = '<div class="panel empty-state"><b>没有匹配的对象</b>调整对象类型、使用状态、所属表或搜索关键词后再试。</div>';
    return;
  }
  $('usageContent').innerHTML = `<div class="table-scroll usage-table-wrap"><table class="usage-table usage-object-table"><thead><tr><th>对象</th><th>类型</th><th>页面数</th><th>视觉引用</th><th>状态</th><th>使用页面</th></tr></thead><tbody>${rows.map(item => `<tr data-usage-object-row="${escapeHtml(item.objectType)}:${escapeHtml(item.tableName)}:${escapeHtml(item.objectName)}" data-usage-object-type="${escapeHtml(item.objectType)}" data-usage-object-table="${escapeHtml(item.tableName)}" data-usage-object-name="${escapeHtml(item.objectName)}"><td><button type="button" class="usage-object-link">${escapeHtml(item.objectName)}</button><small class="usage-sub">${escapeHtml(item.tableName)}${item.isHidden ? ' · 隐藏' : ''}</small></td><td><span class="usage-type ${escapeHtml(item.objectType)}">${escapeHtml(usageObjectTypeLabel(item.objectType))}</span></td><td>${fmt(item.pageCount)}</td><td>${fmt(item.visualCount)}</td><td><span class="usage-status ${escapeHtml(item.usageStatus)}">${escapeHtml(usageStatusLabel(item.usageStatus))}</span></td><td>${usagePageChips(item.pages)}</td></tr>`).join('')}</tbody></table></div>`;
  document.querySelectorAll('#usageContent [data-usage-object-row]').forEach(row => row.addEventListener('click', () => focusUsageObject(row)));
  document.querySelectorAll('#usageContent [data-usage-page-chip]').forEach(chip => {
    chip.addEventListener('click', event => { event.stopPropagation(); openUsagePage(chip.dataset.usagePageChip); });
  });
}

function openUsagePage(pageName) {
  if (!pageName || !state.data?.pages.some(page => page.name === pageName)) {
    toast('页面已不存在，请重新扫描项目。', true);
    return;
  }
  state.selectedPage = pageName;
  state.layoutBookmark = null;
  state.selectedVisual = null;
  renderPageSelect();
  renderBookmarkSelect();
  switchView('layout');
  renderLayout();
}

function focusUsageObject(row) {
  const type = row.dataset.usageObjectType;
  const table = row.dataset.usageObjectTable;
  const name = row.dataset.usageObjectName;
  if (type === 'measure') {
    const measure = allMeasures().find(item => item.table === table && item.name === name);
    if (!measure) { toast('未找到该度量值，请重新扫描项目。', true); return; }
    state.selectedMeasure = measure.key;
    $('daxSearch').value = '';
    renderMeasures();
    switchView('dax');
    return;
  }
  state.selectedTable = table;
  state.objectTab = 'columns';
  $('modelSearch').value = '';
  renderTableList();
  renderTableDetail();
  switchView('model');
}
