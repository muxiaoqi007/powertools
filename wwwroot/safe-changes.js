function bindSafeChanges(){
  $('safeSelectAll').addEventListener('click',()=>{const candidates=safeCandidates();const allSelected=candidates.length&&candidates.every(item=>state.safeChangeSelection.has(safeKey(item)));state.safeChangeSelection=new Set(allSelected?[]:candidates.map(safeKey));state.safeChangePlan=null;renderSafeChanges()});
  $('safePlanButton').addEventListener('click',createSafeChangePlan);
}

function safeCandidates(){return (state.data?.removalCandidates||[]).filter(item=>item.status==='candidate')}
function safeKey(item){return JSON.stringify([item.objectType,item.tableName,item.objectName])}
function safeProjectIsWritableCopySource(){return state.data&&!state.data.liveModel&&state.data.path!=='内置演示数据'&&String(state.data.format||'').toUpperCase().includes('TMDL')}

function renderSafeChanges(){
  if(!state.data)return;
  if(state.safeChangeProject!==state.data.path){state.safeChangeProject=state.data.path;state.safeChangeSelection=new Set();state.safeChangePlan=null}
  const candidates=safeCandidates(),validKeys=new Set(candidates.map(safeKey));
  state.safeChangeSelection=new Set([...state.safeChangeSelection].filter(key=>validKeys.has(key)));
  $('safeChangeStats').innerHTML=[['可隔离候选',candidates.length],['已选择',state.safeChangeSelection.size],['高风险阻断',(state.data.removalCandidates||[]).filter(x=>x.status==='blocked').length],['源项目写入',0]].map(x=>`<div class="mini-stat"><b>${fmt(x[1])}</b><span>${x[0]}</span></div>`).join('');
  if(!safeProjectIsWritableCopySource()){
    $('safeCandidateList').innerHTML='<div class="empty-state"><b>当前来源保持只读</b>安全修改仅支持已保存的 PBIP/TMDL 项目目录。实时 PBIX、model.bim 和示例数据只能分析。</div>';
    $('safePlanButton').disabled=true;$('safeSelectAll').disabled=true;renderSafePlanDetail();return;
  }
  $('safeSelectAll').disabled=!candidates.length;
  $('safeSelectAll').textContent=candidates.length&&candidates.every(item=>state.safeChangeSelection.has(safeKey(item)))?'清除选择':'选择全部';
  $('safeCandidateList').innerHTML=candidates.length?candidates.map(item=>{const key=safeKey(item),selected=state.safeChangeSelection.has(key);return `<label class="safe-candidate ${selected?'selected':''}"><input type="checkbox" data-safe-key="${escapeHtml(key)}" ${selected?'checked':''}/><span><b>${item.objectType==='measure'?'度量值':'字段'} · ${escapeHtml(item.tableName)}[${escapeHtml(item.objectName)}]</b><small>${escapeHtml(item.reasons.join(' · '))}</small></span><em>风险 ${item.riskScore}</em></label>`}).join(''):'<div class="empty-state"><b>没有可隔离候选</b>存在引用的对象已被风险门禁阻断。</div>';
  document.querySelectorAll('[data-safe-key]').forEach(input=>input.addEventListener('change',()=>{if(input.checked)state.safeChangeSelection.add(input.dataset.safeKey);else state.safeChangeSelection.delete(input.dataset.safeKey);state.safeChangePlan=null;renderSafeChanges()}));
  $('safePlanButton').disabled=!state.safeChangeSelection.size;renderSafePlanDetail();
}

async function safeRequest(url,body){const response=await fetch(url,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)});let result={};try{result=await response.json()}catch{}if(!response.ok)throw new Error(result.error||result.detail||'安全修改请求失败');return result}

async function createSafeChangePlan(){
  const button=$('safePlanButton');button.disabled=true;
  try{const operations=[...state.safeChangeSelection].map(key=>{const [objectType,tableName,objectName]=JSON.parse(key);return {objectType,tableName,objectName}});state.safeChangePlan=await safeRequest('/api/v1/changes/plan',{projectPath:state.data.path,operations});renderSafePlanDetail();toast('修改计划已生成，请核对后输入确认短语')}
  catch(error){toast(error.message,true)}finally{button.disabled=!state.safeChangeSelection.size}
}

async function applySafeChange(){const plan=state.safeChangePlan,phrase=$('safeConfirmation').value;try{state.safeChangePlan=await safeRequest('/api/v1/changes/apply',{planId:plan.planId,confirmationPhrase:phrase});renderSafePlanDetail();toast('已应用到隔离副本，源项目未改动')}catch(error){toast(error.message,true)}}
async function rollbackSafeChange(){const plan=state.safeChangePlan,phrase=$('safeConfirmation').value;try{state.safeChangePlan=await safeRequest('/api/v1/changes/rollback',{planId:plan.planId,confirmationPhrase:phrase});renderSafePlanDetail();toast('隔离副本已从备份恢复')}catch(error){toast(error.message,true)}}

function renderSafePlanDetail(){
  const plan=state.safeChangePlan;$('safePlanStatus').textContent=plan?({'planned':'待确认','applying':'应用中','applied':'已应用','apply-failed':'应用失败','rolled-back':'已回滚'}[plan.status]||plan.status):'未生成';$('safePlanStatus').className=`badge ${plan?.status==='applied'?'good':''}`;
  if(!plan){$('safePlanDetail').innerHTML='<div class="empty-state">先从左侧选择候选对象并生成计划。</div>';return}
  const canRollback=['applied','applying','apply-failed'].includes(plan.status),phrase=canRollback?plan.rollbackPhrase:plan.confirmationPhrase;
  $('safePlanDetail').innerHTML=`<div class="safe-plan-summary"><b>${escapeHtml(plan.projectName)} · ${plan.operations.length} 项操作</b><code>计划 ${escapeHtml(plan.planId)} · 指纹 ${escapeHtml(plan.sourceFingerprint)}</code></div><div class="safe-warning-list">${plan.warnings.map(item=>`<div class="safe-warning">${escapeHtml(item)}</div>`).join('')}</div><div class="safe-operation-list">${plan.operations.map(item=>`<div class="safe-operation"><b>${escapeHtml(item.tableName)}[${escapeHtml(item.objectName)}]</b><small>${escapeHtml(item.preview)} · ${escapeHtml(item.sourceFile)}</small></div>`).join('')}</div>${plan.workspacePath?`<div class="safe-result"><b>受控隔离副本</b><code>${escapeHtml(plan.workspacePath)}</code>${plan.auditPath?`<code>审计：${escapeHtml(plan.auditPath)}</code>`:''}</div>`:''}${plan.status!=='rolled-back'?`<div class="safe-confirm"><label>输入完整确认短语：<b>${escapeHtml(phrase)}</b></label><input id="safeConfirmation" autocomplete="off" spellcheck="false" placeholder="${escapeHtml(phrase)}"/><div class="safe-plan-actions">${canRollback?'<button id="safeRollbackButton" class="button secondary">从备份回滚副本</button>':'<button id="safeApplyButton" class="button primary">应用到隔离副本</button>'}</div></div>`:''}<div class="safe-audit">${plan.auditTrail.map(item=>`<div class="safe-audit-item"><span>${new Date(item.at).toLocaleString('zh-CN',{hour12:false})}</span><b>${escapeHtml(item.eventType)}</b><div>${escapeHtml(item.detail)}</div></div>`).join('')}</div>`;
  $('safeApplyButton')?.addEventListener('click',applySafeChange);$('safeRollbackButton')?.addEventListener('click',rollbackSafeChange);
}
