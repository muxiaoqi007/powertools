function bindUpdates(){
  $('updateButton').addEventListener('click',()=>{showUpdateModal();checkForUpdates(true,true)});
  $('updateClose').addEventListener('click',hideUpdateModal);$('updateLater').addEventListener('click',hideUpdateModal);
  $('updateModal').addEventListener('click',event=>{if(event.target===$('updateModal'))hideUpdateModal()});
}

function showUpdateModal(){ $('updateModal').hidden=false }
function hideUpdateModal(){ $('updateModal').hidden=true }

async function checkForUpdates(refresh=false,showModal=false){
  if(showModal){showUpdateModal();renderUpdateLoading('正在连接 GitHub Release…')}
  try{
    const response=await fetch(`/api/v1/updates/check?refresh=${refresh?'true':'false'}`),body=await response.json();
    if(!response.ok)throw new Error(body.error||body.detail||'更新检查失败');
    state.updateInfo=body;renderUpdateButton();if(showModal||body.updateAvailable)renderUpdateDialog();
  }catch(error){if(showModal){$('updateContent').innerHTML=`<div class="empty-state"><b>无法检查更新</b>${escapeHtml(error.message)}</div>`;$('updateAction').textContent='重试';$('updateAction').disabled=false;$('updateAction').onclick=()=>checkForUpdates(true,true)}}
}

function renderUpdateButton(){const info=state.updateInfo,button=$('updateButton');button.classList.toggle('available',!!info?.updateAvailable);button.querySelector('span').textContent=info?.updateAvailable?`更新 ${info.latestVersion}`:'检查更新'}

function renderUpdateLoading(message){$('updateContent').innerHTML=`<div class="update-message">${escapeHtml(message)}</div><div class="update-progress"><i></i></div>`;$('updateAction').disabled=true;$('updateAction').textContent='请稍候'}

function renderUpdateDialog(){
  const info=state.updateInfo;if(!info)return;
  const modeName={delta:'增量更新',full:'完整安装包',manual:'手工更新',none:'无需更新',disabled:'已禁用'}[info.mode]||info.mode;
  $('updateContent').innerHTML=`<div class="update-version-row"><div class="update-version"><span>当前版本</span><b>${escapeHtml(info.currentVersion)}</b></div><div class="update-arrow">→</div><div class="update-version"><span>${info.updateAvailable?'最新版本':'GitHub 版本'}</span><b>${escapeHtml(info.latestVersion)}</b></div></div><div class="update-message">${escapeHtml(info.message)}</div><div class="update-meta"><span>${escapeHtml(modeName)}</span>${info.assetSize?`<span>${fmtBytes(info.assetSize)}</span>`:''}${info.publishedAt?`<span>${new Date(info.publishedAt).toLocaleDateString('zh-CN')}</span>`:''}${info.assetSha256?'<span>SHA-256 已提供</span>':''}</div>${info.releaseNotes?`<div class="update-notes">${escapeHtml(info.releaseNotes)}</div>`:''}`;
  const action=$('updateAction');action.disabled=false;
  if(!info.updateAvailable){action.textContent='重新检查';action.onclick=()=>checkForUpdates(true,true);return}
  if(!info.automaticInstallSupported||!info.desktopHost){action.textContent='打开 GitHub Release';action.onclick=()=>window.open(info.releaseUrl,'_blank','noopener');return}
  action.textContent=info.mode==='delta'?'下载并增量更新':'下载完整安装包';action.onclick=downloadAndApplyUpdate;
}

async function downloadAndApplyUpdate(){
  const action=$('updateAction');action.disabled=true;renderUpdateLoading(state.updateInfo?.mode==='delta'?'正在下载增量更新包…':'正在下载完整安装包…');
  try{
    const response=await fetch('/api/v1/updates/download',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({refresh:false})}),body=await response.json();
    if(!response.ok)throw new Error(body.error||body.detail||'更新下载失败');
    if(!window.chrome?.webview){throw new Error('更新包已校验并暂存，但当前不是 PowerTools 桌面窗口，请从 GitHub Release 手工安装。')}
    $('updateContent').innerHTML='<div class="update-message">下载和 SHA-256 校验已完成。即将由桌面更新器接管，PowerTools 会关闭并在更新后重新启动。</div>';
    $('updateAction').textContent='等待桌面确认';$('updateAction').disabled=true;
    window.chrome.webview.postMessage({type:'apply-update',packagePath:body.packagePath,packageSha256:body.packageSha256,mode:body.mode,targetVersion:body.targetVersion});
  }catch(error){$('updateContent').innerHTML=`<div class="empty-state"><b>更新未启动</b>${escapeHtml(error.message)}</div>`;action.textContent='重试下载';action.disabled=false;action.onclick=downloadAndApplyUpdate}
}
