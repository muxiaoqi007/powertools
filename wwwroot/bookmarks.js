function allBookmarks(){return state.data?.bookmarks||[]}
function currentBookmark(){return allBookmarks().find(x=>x.name===state.selectedBookmark)||null}
function bookmarkPage(bookmark){return state.data?.pages?.find(x=>x.name===bookmark?.activePage)||null}
function bookmarkPageName(bookmark){const page=bookmarkPage(bookmark);return page?.displayName||(bookmark?.activePage?`${bookmark.activePage}（页面不存在）`:'未指定页面')}
function bookmarkStateFor(pageName,visualName){const bookmark=currentBookmark();if(!bookmark)return null;return bookmark.visualStates?.find(x=>x.pageName===pageName&&x.visualName===visualName)||null}
function bookmarkVisualHidden(pageName,visual){const value=bookmarkStateFor(pageName,visual.name)?.isHidden;return value===null||value===undefined?visual.isHidden:value}

function renderBookmarks(){
  if(!state.data)return;
  const bookmarks=allBookmarks(),groups=state.data.bookmarkGroups||[];
  const states=bookmarks.flatMap(x=>x.visualStates||[]);
  $('bookmarkStats').innerHTML=[['书签',bookmarks.length],['书签组',groups.length],['视觉状态',states.length],['目标视觉对象',bookmarks.reduce((n,x)=>n+(x.targetVisualNames?.length||0),0)],['筛选器',bookmarks.reduce((n,x)=>n+(x.reportFilterCount||0)+(x.visualFilterCount||0),0)],['孤立书签',bookmarks.filter(x=>x.activePage&&!bookmarkPage(x)).length]].map(x=>`<div class="mini-stat"><b>${fmt(x[1])}</b><span>${x[0]}</span></div>`).join('');
  $('bookmarkCount').textContent=fmt(bookmarks.length);
  if(!state.selectedBookmark||!bookmarks.some(x=>x.name===state.selectedBookmark))state.selectedBookmark=bookmarks[0]?.name||null;
  renderBookmarkTree();renderBookmarkDetail();renderBookmarkStatePanel();
}

function renderBookmarkTree(){
  const query=$('bookmarkSearch').value.trim().toLowerCase(),bookmarks=allBookmarks(),groups=state.data?.bookmarkGroups||[];
  const byName=new Map(bookmarks.map(x=>[x.name,x])),grouped=new Set(groups.flatMap(x=>x.children||[]));
  const match=bookmark=>(bookmark.displayName+' '+bookmark.name+' '+bookmarkPageName(bookmark)).toLowerCase().includes(query);
  const row=bookmark=>`<button class="bookmark-row ${bookmark.name===state.selectedBookmark?'active':''}" data-bookmark="${escapeHtml(bookmark.name)}"><span class="bookmark-icon">◆</span><span><b>${escapeHtml(bookmark.displayName)}</b><small>${escapeHtml(bookmarkPageName(bookmark))}</small></span><i>${bookmark.visualStates?.length||0}</i></button>`;
  let html='';
  for(const group of groups){const children=(group.children||[]).map(x=>byName.get(x)).filter(Boolean).filter(match);if(query&&!children.length&&!group.displayName.toLowerCase().includes(query))continue;const open=query||children.some(x=>x.name===state.selectedBookmark);html+=`<details class="bookmark-group" ${open?'open':''}><summary><span>▾</span><b>${escapeHtml(group.displayName)}</b><small>${children.length}</small></summary>${children.map(row).join('')}</details>`}
  const loose=bookmarks.filter(x=>!grouped.has(x.name)&&match(x));if(loose.length)html+=`<div class="bookmark-loose-label">未分组</div>${loose.map(row).join('')}`;
  $('bookmarkTree').innerHTML=html||'<div class="empty-state">没有匹配的书签</div>';
  document.querySelectorAll('[data-bookmark]').forEach(x=>x.addEventListener('click',()=>selectBookmark(x.dataset.bookmark)));
}

function selectBookmark(name){state.selectedBookmark=name;renderBookmarkTree();renderBookmarkDetail();renderBookmarkStatePanel()}

function renderBookmarkDetail(){
  const bookmark=currentBookmark();if(!bookmark){$('bookmarkDetail').innerHTML='<div class="empty-state"><b>未检测到书签</b>该报表没有 PBIR 书签定义。</div>';return}
  const hidden=(bookmark.visualStates||[]).filter(x=>x.isHidden===true).length,visible=(bookmark.visualStates||[]).filter(x=>x.isHidden===false).length;
  const canPreview=!!bookmarkPage(bookmark);
  $('bookmarkDetail').innerHTML=`<div class="bookmark-detail-head"><div><p class="eyebrow">BOOKMARK</p><h2>${escapeHtml(bookmark.displayName)}</h2><code>${escapeHtml(bookmark.name)}</code></div><button id="previewBookmark" class="button primary" ${canPreview?'':'disabled'}>${canPreview?'在页面布局中预览':'引用页面不存在'}</button></div>${canPreview?'':'<div class="bookmark-warning">该书签引用的页面已不在当前 PBIR 页面目录中，状态仍可审计，但不能叠加到现有画布。</div>'}<div class="bookmark-property-grid">${[['活动页面',bookmarkPageName(bookmark)],['应用范围',bookmark.applyOnlyToTargetVisuals?'指定视觉对象':'整页'],['数据状态',bookmark.hasDataState?'已保存':'未保存'],['报告筛选',bookmark.reportFilterCount],['视觉筛选',bookmark.visualFilterCount],['目标对象',bookmark.targetVisualNames?.length||0]].map(x=>`<div><span>${x[0]}</span><b>${escapeHtml(x[1])}</b></div>`).join('')}</div><div class="bookmark-visibility"><h3>显隐状态</h3><div><span class="visible-dot"></span>${visible} 个显示</div><div><span class="hidden-dot"></span>${hidden} 个隐藏</div><div><span class="neutral-dot"></span>${(bookmark.visualStates?.length||0)-visible-hidden} 个仅目标/筛选状态</div></div><div class="bookmark-source"><span>源文件</span><code title="${escapeHtml(bookmark.sourceFile)}">${escapeHtml(bookmark.sourceFile)}</code></div>`;
  if(canPreview)$('previewBookmark').addEventListener('click',()=>openBookmarkInLayout(bookmark));
}

function renderBookmarkStatePanel(){
  const bookmark=currentBookmark();if(!bookmark){$('bookmarkStatePanel').innerHTML='';return}
  const page=state.data.pages?.find(x=>x.name===bookmark.activePage),visualMap=new Map((page?.visuals||[]).map(x=>[x.name,x]));
  const states=(bookmark.visualStates||[]).filter(x=>x.pageName===bookmark.activePage||!bookmark.activePage).sort((a,b)=>(a.isHidden===b.isHidden?0:a.isHidden?1:-1));
  $('bookmarkStatePanel').innerHTML=`<div class="bookmark-state-title"><b>视觉对象状态</b><span>${states.length}</span></div><div class="bookmark-state-list">${states.length?states.map(item=>{const visual=visualMap.get(item.visualName),status=item.isHidden===true?'hidden':item.isHidden===false?'visible':'neutral';return `<button class="bookmark-state ${status}" data-bookmark-visual="${escapeHtml(item.visualName)}"><i></i><span><b>${escapeHtml(visual?.title||item.visualName)}</b><small>${escapeHtml(visualNames[visual?.type]||item.visualType||visual?.type||'视觉对象')}</small></span>${item.filterCount?`<em>${item.filterCount} 筛选</em>`:''}</button>`}).join(''):'<div class="empty-state">书签没有保存本页视觉对象状态</div>'}</div>`;
  document.querySelectorAll('[data-bookmark-visual]').forEach(x=>x.addEventListener('click',()=>{openBookmarkInLayout(bookmark,x.dataset.bookmarkVisual)}));
}

function openBookmarkInLayout(bookmark,visualName=null){if(bookmark.activePage)state.selectedPage=bookmark.activePage;state.selectedBookmark=bookmark.name;state.selectedVisual=visualName;renderPageSelect();renderBookmarkSelect();switchView('layout');renderLayout()}

function renderBookmarkSelect(){
  if(!state.data)return;const options=allBookmarks().filter(x=>!x.activePage||x.activePage===state.selectedPage);
  $('bookmarkSelect').innerHTML=`<option value="">原始页面</option>${options.map(x=>`<option value="${escapeHtml(x.name)}" ${x.name===state.selectedBookmark?'selected':''}>${escapeHtml(x.displayName)}</option>`).join('')}`;
  if(state.selectedBookmark&&!options.some(x=>x.name===state.selectedBookmark))state.selectedBookmark=null;
}
