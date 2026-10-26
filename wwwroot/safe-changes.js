function bindSafeChanges(){
  $('safeSelectAll').addEventListener('click',()=>{const candidates=safeVisibleCandidates();const allSelected=candidates.length&&candidates.every(item=>state.safeChangeSelection.has(safeKey(item)));state.safeChangeSelection=new Set(allSelected?[]:candidates.map(safeKey));state.safeChangePlan=null;renderSafeChanges()});
  $('safePlanButton').addEventListener('click',createSafeChangePlan);
  $('safeCandidateSearch').addEventListener('input',renderSafeChanges);
}

function safeAllCandidates(){return (state.data?.removalCandidates||[]).filter(item=>item.status==='candidate')}
function safeObjectHidden(item){const table=state.data?.tables?.find(t=>t.name===item.tableName);if(!table)return false;const objects=item.objectType==='measure'?table.measures:table.columns;return objects?.find(x=>x.name===item.objectName)?.isHidden===true}
function safeCandidates(){return safeAllCandidates().filter(item=>!safeObjectHidden(item))}
function safeHiddenCandidates(){return safeAllCandidates().filter(safeObjectHidden)}
function safeVisibleCandidates(){const query=$('safeCandidateSearch')?.value.trim().toLowerCase()||'';return query?safeCandidates().filter(item=>`${item.tableName} ${item.objectName} ${item.reasons.join(' ')}`.toLowerCase().includes(query)):safeCandidates()}
function safeKey(item){return JSON.stringify([item.objectType,item.tableName,item.objectName])}
function safeProjectIsWritableCopySource(){return state.data&&!state.data.liveModel&&state.data.path!=='内置演示数据'&&String(state.data.format||'').toUpperCase().includes('TMDL')}

function renderSafeChangeStats(){
  const hidden=safeHiddenCandidates().length,note=$('safeCandidateNote');
  $('safeChangeStats').innerHTML=[['可隔离候选',safeCandidates().length],['已选择',state.safeChangeSelection.size],['高风险阻断',(state.data?.removalCandidates||[]).filter(x=>x.status==='blocked').length],['已隐藏无需隔离',hidden]].map(x=>`<div class="mini-stat"><b>${fmt(x[1])}</b><span>${x[0]}</span></div>`).join('');
  note.hidden=!hidden;note.textContent=hidden?`已自动排除 ${fmt(hidden)} 个已隐藏对象：它们无需再次隔离，仍可在“模型优化”中作为删除候选审查。`:'';
}
function safeSelectAllLabel(visible){if(visible.length&&visible.every(item=>state.safeChangeSelection.has(safeKey(item))))return '清除选择';return $('safeCandidateSearch').value.trim()?'选择当前列表':'选择全部'}
function updateSafeChangeButtons(){const visible=safeVisibleCandidates();$('safeSelectAll').textContent=safeSelectAllLabel(visible);$('safePlanButton').disabled=!state.safeChangeSelection.size}

function renderSafeChanges(){
  if(!state.data)return;
  if(state.safeChangeProject!==state.data.path){state.safeChangeProject=state.data.path;state.safeChangeSelection=new Set();state.safeChangePlan=null}
  const candidates=safeCandidates(),validKeys=new Set(candidates.map(safeKey));
  state.safeChangeSelection=new Set([...state.safeChangeSelection].filter(key=>validKeys.has(key)));
  renderSafeChangeStats();
  if(!safeProjectIsWritableCopySource()){
    $('safeCandidateList').innerHTML='<div class="empty-state"><b>当前来源保持只读</b>安全修改仅支持已保存的 PBIP/TMDL 项目目录。实时 PBIX、model.bim 和示例数据只能分析。</div>';
    $('safePlanButton').disabled=true;$('safeSelectAll').disabled=true;$('safeCandidateSearch').disabled=true;renderSafePlanDetail();return;
  }
  $('safeCandidateSearch').disabled=false;
  const visible=safeVisibleCandidates();
  $('safeSelectAll').disabled=!visible.length;
  $('safeSelectAll').textContent=safeSelectAllLabel(visible);
  $('safeCandidateList').innerHTML=visible.length?visible.map(item=>{const key=safeKey(item),selected=state.safeChangeSelection.has(key);return `<label class="safe-candidate ${selected?'selected':''}"><input type="checkbox" data-safe-key="${escapeHtml(key)}" ${selected?'checked':''}/><span><b>${item.objectType==='measure'?'度量值':'字段'} · ${escapeHtml(item.tableName)}[${escapeHtml(item.objectName)}]</b><small>${escapeHtml(item.reasons.join(' · '))}</small></span><em class="risk-score ${riskClass(item.riskScore)}" title="风险评分 ${item.riskScore}/100，数值越低越接近可删除">${riskLabel(item.riskScore)} ${item.riskScore}</em></label>`}).join(''):`<div class="empty-state"><b>${candidates.length?'没有匹配的候选对象':'没有可隔离候选'}</b>${candidates.length?'调整搜索关键词后再试。':'可隔离对象为空：已被引用阻断或已经隐藏。'}</div>`;
  document.querySelectorAll('[data-safe-key]').forEach(input=>input.addEventListener('change',()=>{if(input.checked)state.safeChangeSelection.add(input.dataset.safeKey);else state.safeChangeSelection.delete(input.dataset.safeKey);input.closest('.safe-candidate')?.classList.toggle('selected',input.checked);state.safeChangePlan=null;renderSafeChangeStats();updateSafeChangeButtons()}));
  $('safePlanButton').disabled=!state.safeChangeSelection.size;renderSafePlanDetail();
}

async function safeRequest(url,body){const response=await fetch(url,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)});let result={};try{result=await response.json()}catch{}if(!response.ok)throw new Error(result.error||result.detail||'安全修改请求失败');return result}

async function createSafeChangePlan(){
  if(state.safeBusy||state.projectBusy)return;
  state.safeBusy=true;setSafeBusy(true);const button=$('safePlanButton');button.textContent='正在生成计划…';
  try{const operations=[...state.safeChangeSelection].map(key=>{const [objectType,tableName,objectName]=JSON.parse(key);return {objectType,tableName,objectName}});state.safeChangePlan=await safeRequest('/api/v1/changes/plan',{projectPath:state.data.path,operations});renderSafePlanDetail();toast('修改计划已生成，请核对后输入确认短语')}
  catch(error){toast(error.message,true)}finally{state.safeBusy=false;setSafeBusy(false);button.textContent='生成修改计划';button.disabled=!state.safeChangeSelection.size}
}

function setSafeBusy(busy){
  ['projectButton','sampleButton','liveButton','refreshButton','openButton','safePlanButton','safeSelectAll','safeCandidateSearch'].forEach(id=>$(id).disabled=busy);
  $('safeCandidateList').querySelectorAll('input').forEach(input=>input.disabled=busy);
  if(!busy){$('safePlanButton').disabled=!state.safeChangeSelection.size||!safeProjectIsWritableCopySource();$('safeSelectAll').disabled=!safeVisibleCandidates().length||!safeProjectIsWritableCopySource();$('safeCandidateSearch').disabled=!safeProjectIsWritableCopySource()}
  $('safechanges').setAttribute('aria-busy',String(busy));
}
async function applySafeChange(){await executeSafeChange('apply')}
async function rollbackSafeChange(){await executeSafeChange('rollback')}
async function executeSafeChange(action){
  if(state.safeBusy||state.projectBusy||!state.safeChangePlan)return;
  const plan=state.safeChangePlan,phrase=$('safeConfirmation').value,expected=action==='rollback'?plan.rollbackPhrase:plan.confirmationPhrase;
  if(phrase!==expected){toast('确认短语不匹配，请按上方提示完整输入。',true);$('safeConfirmation').focus();return}
  state.safeBusy=true;setSafeBusy(true);const button=$(action==='rollback'?'safeRollbackButton':'safeApplyButton');button.disabled=true;button.textContent=action==='rollback'?'正在恢复副本…':'正在创建并修改隔离副本…';$('safeConfirmation').disabled=true;
  try{state.safeChangePlan=await safeRequest(`/api/v1/changes/${action}`,{planId:plan.planId,confirmationPhrase:phrase});renderSafePlanDetail();toast(action==='rollback'?'隔离副本已从备份恢复':'已应用到隔离副本，源项目未改动')}
  catch(error){toast(error.message,true);try{const response=await fetch(`/api/v1/changes/${plan.planId}`);if(response.ok)state.safeChangePlan=await response.json()}catch{}renderSafePlanDetail()}
  finally{state.safeBusy=false;setSafeBusy(false);updateSafeConfirmation()}
}
function updateSafeConfirmation(){const plan=state.safeChangePlan;if(!plan||!$('safeConfirmation'))return;const rollback=['applied','applying','apply-failed'].includes(plan.status);const expected=rollback?plan.rollbackPhrase:plan.confirmationPhrase;const button=$('safeRollbackButton')||$('safeApplyButton');if(button)button.disabled=state.safeBusy||$('safeConfirmation').value!==expected}

function renderSafePlanDetail(){
  const plan=state.safeChangePlan;$('safePlanStatus').textContent=plan?({'planned':'待确认','applying':'应用中','applied':'已应用','apply-failed':'应用失败','rolled-back':'已回滚'}[plan.status]||plan.status):'未生成';$('safePlanStatus').className=`badge ${plan?.status==='applied'?'good':''}`;
  if(!plan){$('safePlanDetail').innerHTML='<div class="empty-state">先从左侧选择候选对象并生成计划。</div>';return}
  const canRollback=['applied','applying','apply-failed'].includes(plan.status),phrase=canRollback?plan.rollbackPhrase:plan.confirmationPhrase;
  $('safePlanDetail').innerHTML=`<div class="safe-plan-summary"><b>${escapeHtml(plan.projectName)} · ${plan.operations.length} 项操作</b><code>计划 ${escapeHtml(plan.planId)} · 指纹 ${escapeHtml(plan.sourceFingerprint)}</code></div><div class="safe-warning-list">${plan.warnings.map(item=>`<div class="safe-warning">${escapeHtml(item)}</div>`).join('')}</div><div class="safe-operation-list">${plan.operations.map(item=>`<div class="safe-operation"><b>${escapeHtml(item.tableName)}[${escapeHtml(item.objectName)}]</b><small>${escapeHtml(item.preview)} · ${escapeHtml(item.sourceFile)}</small></div>`).join('')}</div>${plan.workspacePath?`<div class="safe-result"><b>受控隔离副本</b><code>${escapeHtml(plan.workspacePath)}</code>${plan.auditPath?`<code>审计：${escapeHtml(plan.auditPath)}</code>`:''}</div>`:''}${plan.status!=='rolled-back'?`<div class="safe-confirm"><label>输入完整确认短语：<b>${escapeHtml(phrase)}</b></label><input id="safeConfirmation" autocomplete="off" spellcheck="false" placeholder="${escapeHtml(phrase)}"/><div class="safe-plan-actions">${canRollback?'<button id="safeRollbackButton" class="button secondary">从备份回滚副本</button>':'<button id="safeApplyButton" class="button primary">应用到隔离副本</button>'}</div></div>`:''}<div class="safe-audit">${plan.auditTrail.map(item=>`<div class="safe-audit-item"><span>${new Date(item.at).toLocaleString('zh-CN',{hour12:false})}</span><b>${escapeHtml(item.eventType)}</b><div>${escapeHtml(item.detail)}</div></div>`).join('')}</div>`;
  $('safeApplyButton')?.addEventListener('click',applySafeChange);$('safeRollbackButton')?.addEventListener('click',rollbackSafeChange);
  $('safeConfirmation')?.setAttribute('aria-label','完整确认短语');
  $('safeConfirmation')?.addEventListener('input',updateSafeConfirmation);updateSafeConfirmation();
}
