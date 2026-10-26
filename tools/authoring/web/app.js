(async function () {
  'use strict';
  const M = window.ScriptEditorModel;
  const catalog = JSON.parse(document.getElementById('event-catalog').textContent);
  // Hash navigation keeps the same local file URL/storage location for every event.
  const linkedEventCode = () => new URLSearchParams(location.hash.slice(1)).get('event');
  const freshProject = () => M.project([]);
  const config = JSON.parse(document.getElementById('project-config').textContent);
  const KEY = config.storageKey + ':local';
  const $ = id => document.getElementById(id);
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const routeNames = config.routes;
  const verdictNames = {'':'未判定',good:'良い',fix:'要修正',hold:'保留・別案',reject:'ボツ'};
  $('event-route').innerHTML=Object.entries(routeNames).map(([id,label])=>`<option value="${esc(id)}">${esc(label)}</option>`).join('');
  $('speakers').innerHTML=(config.speakers||[]).map(name=>`<option>${esc(name)}</option>`).join('');
  $('catalog-button').hidden = !catalog.length;
  const statusNames = {draft:'未着手',writing:'執筆中',review:'推敲中',done:'執筆完了'};
  const icons = {dialogue:'「 」',narration:'文',direction:'◇',choice:'⑂',label:'⚑',jump:'→',end:'□'};
  let state, dirty = false, storagePaused = false, corruptRaw = '', lastSavedRaw = null;
  let saveTimer, toastTimer, lastEdit = {key:'',time:0}, reader = null, pendingImport = null;
  const undo = [], redo = [];
  let lastRow = '';
  let shared = null, saving = null, editVersion = 0, remoteBusy = false, collaboration, chart, review;
  let sharedProblem = '', pendingCache = null;
  const CACHE = config.storageKey + ':shared-draft';
  async function request(path, payload) {
    const response = await fetch(path, {cache:'no-store', signal:AbortSignal.timeout(10000),
      ...(payload === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json','X-Script-Editor':'1'},body:JSON.stringify(payload)})});
    if (response.status === 404) { const error=new Error('共有保存サーバーが見つかりません。');error.status=404;throw error; }
    const value = await response.json();
    if (!response.ok) { const error=new Error(value.error||'保存に失敗しました。');error.status=response.status;throw error; }
    return value;
  }
  if (location.protocol === 'http:' || location.protocol === 'https:') {
    document.querySelector('.layout').inert = true;
    try { pendingCache=JSON.parse(sessionStorage.getItem(CACHE)); } catch (_) {}
    try {
      const snapshot=await request('/api/project');
      state=M.validate(snapshot.document);shared=snapshot;
      if (pendingCache?.pending) {
        state=M.validate(pendingCache.document);dirty=true;
        if (pendingCache.revision!==shared.revision) {
          shared.revision=pendingCache.revision;
          sharedProblem='未保存の原稿を復旧しました。共有原稿にも変更があるため、自動保存を停止しています。';
        }
      } else if (state.events.some(e=>e.uid===pendingCache?.document?.activeId)) state.activeId=pendingCache.document.activeId;
    } catch (error) {
      if (error.status!==404 || pendingCache?.pending) {
        shared={revision:pendingCache?.revision??0,comments:[],cursor:0};
        try {state=pendingCache?M.validate(pendingCache.document):M.project([]);} catch (_) {state=M.project([]);}
        dirty=!!pendingCache?.pending;
        sharedProblem='共有保存に接続できません。サーバーを起動してから「接続を再試行」を押してください。';
      }
    }
    document.querySelector('.layout').inert = false;
  }
  if (!shared) try {
    lastSavedRaw = localStorage.getItem(KEY);
    state = lastSavedRaw ? M.validate(JSON.parse(lastSavedRaw)) : freshProject();
  } catch (error) {
    state = freshProject();
    storagePaused = true;
    corruptRaw = lastSavedRaw || '';
    showStorageWarning(corruptRaw ? '保存データを読み込めませんでした。元データを退避してから保存を再開できます。' : 'ブラウザへの保存を利用できません。書き出しで台本を保存してください。', !!corruptRaw);
  }
  const active = () => state.events.find(e=>e.uid===state.activeId);
  const findRow = id => active()?.rows.find(r=>r.id===id);
  function cacheShared() {
    if (!shared) return;
    try { sessionStorage.setItem(CACHE,JSON.stringify({revision:shared.revision,document:state,pending:dirty})); }
    catch (_) { /* Server persistence still works; beforeunload protects unsaved work. */ }
  }
  function sharedStatus() {
    $('save-state').textContent=dirty?'未保存の変更あり':`共有保存済み · #${shared.revision}`;
    $('save-state').className='save-state '+(dirty?'':'saved');
  }
  function acceptRemote(snapshot) {
    const selected=state.activeId;
    state=M.validate(snapshot.document);
    if(state.events.some(e=>e.uid===selected))state.activeId=selected;
    shared=snapshot;dirty=false;storagePaused=false;reader=null;
    undo.length=0;redo.length=0;lastEdit={key:'',time:0};
    $('storage-warning').hidden=true;cacheShared();sharedStatus();render();
  }
  function pauseShared(message) {
    storagePaused=true;cacheShared();
    const bar=$('storage-warning');bar.hidden=false;
    bar.innerHTML=`<span>${esc(message)}</span><button id="conflict-export">原稿をJSONに退避</button><button id="conflict-latest">退避して共有原稿を開く</button><button id="retry-shared">接続を再試行</button>`;
    $('save-state').textContent='共有保存を停止中';$('save-state').className='save-state error';
    $('conflict-export').onclick=exportProject;
    $('conflict-latest').onclick=()=>confirmAction('共有原稿を開く','このタブの原稿をJSONに退避してから、共有原稿を読み込みます。必要な変更はJSONから取り込めます。',async()=>{
      try { const snapshot=await request('/api/project');exportProject();acceptRemote(snapshot); }
      catch(error){toast(error.message);}
    },'退避して開く');
    $('retry-shared').onclick=async()=>{
      try {
        const snapshot=await request('/api/project');
        if(snapshot.revision!==shared.revision && dirty){toast('共有原稿に変更があります。原稿を退避してから最新版を開いてください。');return;}
        if(!dirty)acceptRemote(snapshot);
        else {storagePaused=false;$('storage-warning').hidden=true;await save();}
      }catch(error){toast(error.message);}
    };
  }
  function toast(message) {
    $('toast').textContent = message;
    $('toast').hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(()=>$('toast').hidden=true, 3800);
  }
  function download(content, name, type) {
    const url = URL.createObjectURL(new Blob([content], {type}));
    const a = document.createElement('a');
    a.href = url; a.download = name; document.body.append(a); a.click(); a.remove();
    setTimeout(()=>URL.revokeObjectURL(url), 30000);
  }
  const fileName = title => (title || '台本').replace(/[\\/:*?"<>|\u0000-\u001f]/g,'_').slice(0,100);
  const stamp = () => new Date().toISOString().replace(/[:.]/g,'-');
  function exportProject() {
    const out = M.copy(state); out.updatedAt = new Date().toISOString();
    download(JSON.stringify(out,null,2), '台本_'+stamp()+'.json', 'application/json');
  }
  function showStorageWarning(message, canRecover = false) {
    const bar = $('storage-warning');
    bar.hidden = false; bar.replaceChildren();
    const text = document.createElement('span'); text.textContent = message; bar.append(text);
    const backup = document.createElement('button'); backup.textContent = '台本を書き出す'; backup.onclick=exportProject; bar.append(backup);
    if (canRecover) {
      const recover = document.createElement('button'); recover.textContent = '元データを退避して保存を再開';
      recover.onclick = () => {
        download(corruptRaw, '台本_読み込み前データ_'+stamp()+'.json', 'application/json');
        storagePaused = false; bar.hidden = true; dirty = true; save(true);
      }; bar.append(recover);
    }
    const reload = document.createElement('button'); reload.textContent='保存済みデータを開き直す';
    reload.onclick=()=>confirmAction('保存済みデータを開き直す', 'このタブの内容をJSONに書き出してから、保存済みデータを開き直します。', ()=>{exportProject();dirty=false;location.reload();}, '書き出して開き直す');
    bar.append(reload);
    $('save-state').textContent='保存停止中'; $('save-state').className='save-state error';
  }
  function save(force = false) {
    clearTimeout(saveTimer);
    if (shared) return saveShared();
    if (!dirty || storagePaused) return;
    try {
      if (!force && localStorage.getItem(KEY) !== lastSavedRaw) {
        storagePaused = true;
        showStorageWarning('別のタブで保存内容が変わりました。このタブの自動保存を停止しています。');
        return;
      }
      state.updatedAt = new Date().toISOString();
      const data = JSON.stringify(state);
      localStorage.setItem(KEY, data);
      lastSavedRaw = data; dirty = false;
      $('save-state').textContent='ブラウザに保存済み'; $('save-state').className='save-state saved';
    } catch (_) {
      $('save-state').textContent='保存できません'; $('save-state').className='save-state error';
      showStorageWarning('ブラウザに保存できませんでした。空き容量や保存設定を確認し、JSONを書き出してください。');
    }
  }
  async function saveShared() {
    if (saving) {await saving;return dirty&&!storagePaused?saveShared():!dirty&&!storagePaused;}
    if(storagePaused || remoteBusy)return false;
    if(!dirty)return true;
    const version=editVersion;
    const document=M.copy(state);
    $('save-state').textContent='共有保存中…';
    saving=(async()=>{
      try {
        const snapshot=await request('/api/save',{revision:shared.revision,document});
        shared=snapshot;
        if(version===editVersion){dirty=false;state.updatedAt=snapshot.document.updatedAt;}
        cacheShared();sharedStatus();collaboration?.refresh();review?.refresh();updateStats();
        return true;
      }catch(error){pauseShared(error.status===409?error.message:'共有保存に失敗しました。接続を確認して再試行してください。');return false;}
    })();
    const ok=await saving;saving=null;
    if(ok && dirty)return saveShared();
    return ok;
  }
  function changed() {
    dirty = true;editVersion++;cacheShared();
    if (!storagePaused) { $('save-state').textContent='保存中…'; $('save-state').className='save-state'; }
    clearTimeout(saveTimer); saveTimer = setTimeout(()=>save(),shared?900:400);
    updateStats(); renderSidebar(); updateUndo();
    chart?.invalidate();review?.refresh();
  }
  function checkpoint(key = '') {
    const now = Date.now();
    if (!key || lastEdit.key !== key || now-lastEdit.time > 1000) {
      undo.push(M.copy(state)); if (undo.length>70) undo.shift(); redo.length=0;
    }
    lastEdit = {key,time:now};
  }
  function mutation(fn, focusId = '') {
    checkpoint(); fn(); reader=null; changed(); render();
    if (focusId) focusRow(focusId);
  }
  function updateUndo() { $('undo-button').disabled=!undo.length; $('redo-button').disabled=!redo.length; }
  function travel(from,to) {
    if (!from.length) return;
    to.push(M.copy(state)); state=from.pop(); lastEdit={key:'',time:0}; reader=null;
    changed(); render();
  }
  function updateStats() {
    const e=active();
    if (!e) { $('document-stats').textContent=''; return; }
    const characters=e.rows.reduce((n,r)=>n+r.text.length+(r.options||[]).reduce((m,o)=>m+o.text.length,0),0);
    $('document-stats').textContent=`${e.rows.length}行 · ${characters.toLocaleString()}字`;
    $('issue-count').textContent=(M.inspect(e).length+(review?.report().issues.length||0))+'件';
  }
  function renderSidebar() {
    const query=$('draft-search').value.toLowerCase().trim();
    $('draft-count').textContent=state.events.length;
    const filtered=state.events.filter(e=>($('verdict-filter').value==='all'||e.verdict===$('verdict-filter').value)).filter(e=>!query || [e.title,e.sourceId,...e.rows.flatMap(r=>[r.text,r.speaker||'',...(r.options||[]).map(o=>o.text)])].join('\n').toLowerCase().includes(query));
    $('draft-list').innerHTML=filtered.map(e=>`<button class="draft-item ${e.uid===state.activeId?'active':''}" data-event="${esc(e.uid)}" ${e.uid===state.activeId?'aria-current="true"':''}><span class="id">${esc(e.sourceId||'ORIGINAL')}</span><span class="name">${esc(e.title||'無題の台本')}</span><span class="sub"><span>${esc(statusNames[e.status])} · ${esc(verdictNames[e.verdict])}</span><span>${e.rows.filter(r=>r.text.trim()).length}行記入</span></span></button>`).join('') || '<p class="empty-list">'+(query?'一致する台本がありません。':'「既存イベントから書く」または<br>「白紙の台本」で始められます。')+'</p>';
  }
  function targetOptions(value, allowNext = true) {
    const labels=active().rows.filter(r=>r.type==='label');
    let text=`<option value="">${allowNext?'次の行へ進む':'移動先を選ぶ'}</option>`;
    if (value && !labels.some(r=>r.id===value)) text+=`<option value="${esc(value)}" selected>⚠ 削除された場面目印</option>`;
    return text+labels.map(r=>`<option value="${esc(r.id)}" ${r.id===value?'selected':''}>${esc(r.text||'（名前未記入）')} · ${active().rows.indexOf(r)+1}行目</option>`).join('');
  }
  function rowHTML(r,i) {
    const textarea=(placeholder,extra='')=>`<textarea class="row-text ${extra}" data-field="text" aria-label="${i+1}行目の本文" placeholder="${esc(placeholder)}" rows="2">${esc(r.text)}</textarea>`;
    let body='';
    if (r.type==='dialogue') body=`<div class="speaker-line"><span class="speaker-dot"></span><input class="speaker-input" data-field="speaker" value="${esc(r.speaker)}" list="speakers" placeholder="話者" aria-label="${i+1}行目の話者"><input class="expression-input" data-field="expression" value="${esc(r.expression)}" list="expressions" placeholder="表情・話し方" aria-label="${i+1}行目の表情"></div>${textarea('この人物が、いま口にする言葉を。')}`;
    if (r.type==='narration') body=textarea('風景、仕草、沈黙。台詞のあいだにあるものを。');
    if (r.type==='direction') body=textarea('立ち絵の切り替え、効果音、間の取り方など。');
    if (r.type==='end') body=textarea('終わり方のメモ（任意）。この行で読み合わせを終了します。');
    if (r.type==='label') body=`<input class="label-input" data-field="text" value="${esc(r.text)}" placeholder="場面の名前（例：手を貸した後）" aria-label="${i+1}行目の場面名">`;
    if (r.type==='jump') body=`<label class="jump-field">次に読む場面<select data-field="target" aria-label="${i+1}行目の移動先">${targetOptions(r.target,false)}</select></label>`;
    if (r.type==='choice') body=textarea('選択肢を出す直前の問いかけ（任意）。')+r.options.map((o,j)=>`<div class="option-card" data-option="${esc(o.id)}"><div class="option-top"><span class="option-number">${String(j+1).padStart(2,'0')}</span><input data-option-field="text" value="${esc(o.text)}" placeholder="選択肢の文言" aria-label="${i+1}行目の選択肢${j+1}"><button class="icon-button" data-action="remove-option" title="選択肢を削除" aria-label="選択肢${j+1}を削除">×</button></div><div class="option-detail"><label>選んだ後の行き先<select data-option-field="target">${targetOptions(o.target)}</select></label><label>条件メモ（任意）<input data-option-field="condition" value="${esc(o.condition)}" placeholder="例：好感度20以上"></label></div></div>`).join('')+'<button data-action="add-option" class="quiet add-option">＋ 選択肢を追加</button>';
    return `<article class="script-row" id="row-${esc(r.id)}" data-row="${esc(r.id)}" data-type="${r.type}"><div class="row-head"><span class="row-number">${String(i+1).padStart(3,'0')}</span><select class="row-kind" data-action="type" aria-label="${i+1}行目の種類">${M.TYPES.map(t=>`<option value="${t}" ${t===r.type?'selected':''}>${M.NAMES[t]}</option>`).join('')}</select><div class="row-actions"><button class="icon-button" data-action="comment" aria-label="${i+1}行目に添削コメント" title="この行に添削コメント">✎</button><button class="icon-button" data-action="play" aria-label="${i+1}行目から読む" title="この行から読む">▷</button><button class="icon-button" data-action="up" aria-label="${i+1}行目を上へ" title="上へ" ${i===0?'disabled':''}>↑</button><button class="icon-button" data-action="down" aria-label="${i+1}行目を下へ" title="下へ" ${i===active().rows.length-1?'disabled':''}>↓</button><button class="icon-button" data-action="duplicate" aria-label="${i+1}行目を複製" title="行を複製">⧉</button><button class="icon-button" data-action="delete" aria-label="${i+1}行目を削除" title="行を削除（元に戻せます）">×</button></div></div><div class="row-body">${body}</div><button class="row-insert" data-action="insert" aria-label="${i+1}行目の後に台詞を追加" title="ここに台詞を追加">＋</button></article>`;
  }
  function resizeTextareas() {
    document.querySelectorAll('.row-text').forEach(el=>{el.style.height='auto';el.style.height=Math.max(el.scrollHeight,el.closest('[data-type="direction"]')?44:65)+'px';});
  }
  function focusRow(id) {
    switchDocument('script');
    lastRow=id;
    const el=$('row-'+id);
    if (!el) return;
    const field=el.querySelector('[data-field="text"]') || el.querySelector('input,textarea,select');
    field?.focus(); el.scrollIntoView({block:'nearest',behavior:'smooth'});
  }
  function renderRows() {
    const e=active();
    $('manuscript').innerHTML=e ? e.rows.map(rowHTML).join('') : '<div class="stage-empty"><div class="stage-mark">書</div><strong>最初の一行から。</strong><p>既存イベントを選ぶか、白紙の台本を作ってください。</p><button class="primary" id="empty-new">白紙から書く</button></div>';
    if (e && !e.rows.length) $('manuscript').innerHTML='<p class="empty-list">上の「行を追加」から、最初の台詞や地の文を置いてください。</p>';
    resizeTextareas();
    highlightReader();
  }
  function render() {
    const e=active();
    if(e && !Array.from($('event-route').options).some(o=>o.value===e.route))$('event-route').add(new Option(e.route,e.route));
    $('event-title').value=e?.title || '';
    $('event-code').textContent=e?.sourceId || 'NEW SCRIPT';
    for (const key of ['status','sourceId','period','route','place','notes','verdict']) {
      $('event-'+key).value=e?.[key] || (key==='route'?'common':key==='status'?'draft':'');
      $('event-'+key).disabled=!e;
    }
    $('event-title').disabled=!e;
    for (const id of ['duplicate-event','delete-event','meta-toggle','check-button','add-dialogue-bottom']) $(id).disabled=!e;
    $('add-tools').querySelectorAll('button').forEach(b=>b.disabled=!e);
    renderSidebar(); renderRows(); renderReference(); renderPreview(); updateStats(); updateUndo();collaboration?.refresh();chart?.invalidate();review?.refresh();
  }
  function switchDocument(view) {
    for(const name of ['script','chart']){
      $(name+'-view-tab').setAttribute('aria-selected',view===name);$(name+'-panel').hidden=view!==name;
    }
    if(view==='chart')chart?.render();else resizeTextareas();
  }
  function addRow(type='dialogue', afterId='') {
    const e=active(); if (!e) return;
    const after=e.rows.findIndex(r=>r.id===afterId);
    const speaker=e.rows.slice(0,after>=0?after+1:e.rows.length).reverse().find(r=>r.type==='dialogue')?.speaker || config.speakers?.[0] || '';
    const r=M.row(type,speaker);
    mutation(()=>e.rows.splice(after>=0?after+1:e.rows.length,0,r),r.id);
  }
  function selectEvent(id) {
    save(); state.activeId=id; reader=null; lastRow=''; lastEdit={key:'',time:0};
    if(shared)cacheShared();else{dirty=true;save();}
    render(); $('writing-pane').scrollTop=0; document.querySelector('.sidebar').classList.remove('open');
  }
  function newEvent(source) {
    const existing=source && (active()?.sourceId===source.id ? active() : state.events.find(e=>e.sourceId===source.id));
    if (existing) { selectEvent(existing.uid); closeModal(); toast('既存の台本を開きました'); return; }
    const e=M.event(source);e.route=Object.keys(config.routes)[0];e.rows.filter(r=>r.type==='dialogue').forEach(r=>r.speaker=config.speakers?.[0]||'');
    mutation(()=>{state.events.push(e);state.activeId=e.uid;});
    closeModal(); focusRow(e.rows[0].id); document.querySelector('.sidebar').classList.remove('open');
  }
  function openLinkedEvent() {
    const code=linkedEventCode();
    if (!code) return;
    const existing=state.events.find(e=>e.uid===code||e.sourceId===code);
    if(existing){selectEvent(existing.uid);return;}
    const source=catalog.find(e=>e.id===code);
    if (!source) { toast('指定されたイベントが見つかりません：'+code); return; }
    newEvent(source);
  }
  function openModal(title, html) {
    $('modal-title').textContent=title; $('modal-body').innerHTML=html;
    if (!$('modal').open) $('modal').showModal();
  }
  function closeModal() { $('modal').close(); }
  function confirmAction(title,message,action,label='実行する') {
    openModal(title, `<p class="hint">${esc(message)}</p><div class="modal-actions"><button id="confirm-cancel">キャンセル</button><button class="primary" id="confirm-yes">${esc(label)}</button></div>`);
    $('confirm-cancel').onclick=closeModal;
    $('confirm-yes').onclick=()=>{closeModal();action();};
  }
  function openCatalog() {
    openModal('既存イベントから書く', `<p class="hint">原案を参照しながら、新しい台本を書き始めます。採用イベント ${catalog.length}件。</p><input id="catalog-search" class="modal-search" placeholder="イベント名・ID・登場人物で検索" aria-label="既存イベントを検索"><div id="catalog-list" class="catalog-list"></div>`);
    function update() {
      const q=$('catalog-search').value.toLowerCase();
      const list=catalog.filter(c=>[c.id,c.title,c.cast].join(' ').toLowerCase().includes(q));
      $('catalog-list').innerHTML=list.map(c=>`<button class="catalog-item" data-source="${esc(c.id)}"><small>${esc(c.id)} · ${esc(c.period)} · ${esc(routeNames[c.route])}</small><strong>${esc(c.title)}</strong><small>${state.events.some(e=>e.sourceId===c.id)?'執筆中の台本を開く':esc(c.cast||'台本を作成')}</small></button>`).join('') || '<p class="hint">該当するイベントがありません。</p>';
    }
    $('catalog-search').oninput=update;
    $('catalog-list').onclick=ev=>{const btn=ev.target.closest('[data-source]');if(btn)newEvent(catalog.find(c=>c.id===btn.dataset.source));};
    update(); $('catalog-search').focus();
  }
  function renderReference() {
    const e=active(), source=catalog.find(c=>c.id===e?.sourceId);
    if (!source) { $('reference-panel').innerHTML='<h3>この場面の原案</h3><p class="hint">既存イベントから作成すると、ここで原案を参照できます。イベント情報に既存のIDを指定しても関連付けられます。</p>'; return; }
    $('reference-panel').innerHTML=`<span class="reference-tag">設計資料からの参照 · ${esc(source.id)}</span><h3>${esc(source.title)}</h3>`+
      [['登場人物',source.cast],['時期',source.period],['発生条件',source.condition],...Object.entries(source.steps),['結果・フラグ',source.effect],['劇作メモ',source.memo]].filter(([,v])=>v).map(([k,v])=>`<h4>${esc(k)}</h4><p>${esc(v)}</p>`).join('');
  }
  function switchReader(tab) {
    for(const name of ['preview','reference','comments','history','rules']) {
      $(name+'-tab').setAttribute('aria-selected',tab===name);$(name+'-panel').hidden=tab!==name;
    }
    collaboration?.refresh();review?.refresh();
  }
  function highlightReader() {
    document.querySelectorAll('.script-row.playing').forEach(el=>el.classList.remove('playing'));
    if (reader?.row) $('row-'+reader.row.id)?.classList.add('playing');
  }
  function startReader(index=0) {
    const e=active(); if (!e) return;
    reader=M.beat(e.rows,index); switchReader('preview'); renderPreview(); highlightReader();
  }
  function renderPreview() {
    const e=active(), host=$('preview-stage');
    if (!e) { host.innerHTML='<div class="stage-empty"><strong>台本を選ぶ</strong><p>台詞を書いたら、ここで読み合わせできます。</p></div>';return; }
    let content=`<div class="stage-place">${esc(e.place||'場所未設定')}<br>${esc(e.period||'時期未設定')} · ${esc(routeNames[e.route])}</div>`;
    if (!reader) {
      content+='<div class="stage-empty"><div class="stage-mark">幕</div><strong>ことばの流れを、確かめる。</strong><p>書いた台詞を一行ずつ。<br>選択肢の先まで読んでみましょう。</p><button class="primary" data-reader="start">読み合わせを始める ▷</button></div>';
    } else if (reader.error) content+=`<div class="stage-empty"><strong>行き先を確認してください</strong><p>${esc(reader.error)}</p><button data-reader="start">はじめから</button></div>`;
    else if (reader.end) content+=`<div class="stage-empty"><div class="stage-mark">了</div><strong>この場面は、ここまで。</strong><p>${esc(reader.row?.text||'台本の最後まで読みました。')}</p><button data-reader="start">もう一度読む</button></div>`;
    else {
      const r=e.rows.find(r=>r.id===reader.row.id);
      if (!r) { reader=null; renderPreview(); return; }
      reader.row=r;reader.index=e.rows.indexOf(r);
      content+=`<div class="speech ${r.type==='direction'?'direction':''}"><div class="speech-name">${esc(r.type==='dialogue'?(r.speaker||'話者未設定'):M.NAMES[r.type])}<small>${esc(r.expression||'')}</small></div><div class="speech-text">${esc(r.text||(r.type==='choice'?'どうする？':''))}</div></div><div class="stage-line">${reader.index+1} / ${e.rows.length} 行</div>`;
      if (r.type==='choice') {
        content+='<div class="preview-choices">'+r.options.map((o,i)=>`<button data-reader="choose" data-index="${i}">${esc(o.text||'（未記入の選択肢）')}${o.condition?`<small>条件：${esc(o.condition)}</small>`:''}</button>`).join('')+'</div>';
        if (!r.options.length) content+='<p class="hint">選択肢がありません。カードに追加してください。</p>';
      } else content+='<div class="stage-next"><button class="primary" data-reader="next">次の行へ →</button></div>';
    }
    host.innerHTML=content;
    highlightReader();
  }
  $('preview-stage').onclick=ev=>{
    const btn=ev.target.closest('[data-reader]');if(!btn || !active())return;
    if(btn.dataset.reader==='start')startReader();
    if(btn.dataset.reader==='next' && reader){reader=M.beat(active().rows,reader.index+1);renderPreview();}
    if(btn.dataset.reader==='choose' && reader){const opt=reader.row.options[Number(btn.dataset.index)];if(opt){reader=M.choose(active().rows,reader.index,opt);renderPreview();}}
  };
  $('manuscript').addEventListener('focusin',ev=>{const el=ev.target.closest('[data-row]');if(el)lastRow=el.dataset.row;});
  $('manuscript').addEventListener('input',ev=>{
    const field=ev.target, host=field.closest('[data-row]'); if(!host)return;
    const r=findRow(host.dataset.row);if(!r)return;
    const optionField=field.dataset.optionField, key=field.dataset.field;
    if(!key && !optionField)return;
    const opt=optionField?r.options.find(o=>o.id===field.closest('[data-option]').dataset.option):null;
    checkpoint(r.id+':'+(opt?.id||'')+':'+(key||optionField));
    if(opt) opt[optionField]=field.value; else r[key]=field.value;
    if(active().status==='draft' && field.value.trim()){active().status='writing';$('event-status').value='writing';}
    changed();resizeTextareas();renderPreview();
  });
  $('manuscript').addEventListener('focusout',ev=>{
    const host=ev.target.closest('[data-row]');if(host && findRow(host.dataset.row)?.type==='label' && ev.target.dataset.field==='text') {
      document.querySelectorAll('select[data-field="target"],select[data-option-field="target"]').forEach(sel=>{const val=sel.value;sel.innerHTML=targetOptions(val,!!sel.dataset.optionField);});
    }
  });
  $('manuscript').addEventListener('change',ev=>{
    const field=ev.target;if(field.dataset.action!=='type')return;
    const r=findRow(field.closest('[data-row]').dataset.row), next=field.value;
    const apply=()=>mutation(()=>{
      const replacement=M.row(next,r.speaker||config.speakers?.[0]||'');replacement.id=r.id;replacement.text=r.text;
      active().rows[active().rows.indexOf(r)]=replacement;
    },r.id);
    if(r.type==='choice' && r.options.some(o=>o.text||o.target||o.condition)){
      field.value=r.type;confirmAction('行の種類を変更', '選択肢と分岐先の情報を外して、行の種類を変更します。変更後も「元に戻す」で復元できます。',apply,'変更する');
    }else apply();
  });
  $('manuscript').addEventListener('click',ev=>{
    if(ev.target.id==='empty-new'){newEvent();return;}
    const btn=ev.target.closest('button[data-action]');if(!btn)return;
    const host=btn.closest('[data-row]'), r=findRow(host.dataset.row), rows=active().rows, i=rows.indexOf(r), action=btn.dataset.action;
    if(action==='play'){startReader(i);return;}
    if(action==='comment'){switchReader('comments');collaboration?.target(r.id);return;}
    if(action==='insert'){addRow('dialogue',r.id);return;}
    if(action==='up' && i>0)mutation(()=>[rows[i-1],rows[i]]=[rows[i],rows[i-1]],r.id);
    if(action==='down' && i<rows.length-1)mutation(()=>[rows[i+1],rows[i]]=[rows[i],rows[i+1]],r.id);
    if(action==='duplicate'){
      const clone=M.copy(r);clone.id=M.uid();if(clone.options)clone.options.forEach(o=>o.id=M.uid());
      mutation(()=>rows.splice(i+1,0,clone),clone.id);
    }
    if(action==='delete')mutation(()=>rows.splice(i,1));
    if(action==='add-option')mutation(()=>r.options.push(M.option()),r.id);
    if(action==='remove-option'){
      const oid=btn.closest('[data-option]').dataset.option;
      mutation(()=>r.options=r.options.filter(o=>o.id!==oid),r.id);
    }
  });
  for(const key of ['title','status','sourceId','period','route','place','notes','verdict']){
    $('event-'+key).addEventListener('input',ev=>{
      if(!active())return;checkpoint('event:'+active().uid+':'+key);active()[key]=ev.target.value;
      if(key==='sourceId'){$('event-code').textContent=ev.target.value||'NEW SCRIPT';renderReference();}
      changed();renderPreview();
    });
  }
  $('add-tools').innerHTML=M.TYPES.map(t=>`<button data-add="${t}" title="${M.NAMES[t]}の行を追加"><span aria-hidden="true">${icons[t]}</span> ${M.NAMES[t]}</button>`).join('');
  $('add-tools').onclick=ev=>{const b=ev.target.closest('[data-add]');if(b)addRow(b.dataset.add,lastRow);};
  $('add-dialogue-bottom').onclick=()=>addRow();
  $('draft-list').onclick=ev=>{const b=ev.target.closest('[data-event]');if(b)selectEvent(b.dataset.event);};
  $('verdict-filter').onchange=renderSidebar;
  $('draft-search').oninput=renderSidebar;
  $('new-button').onclick=()=>newEvent(); $('catalog-button').onclick=openCatalog;
  $('undo-button').onclick=()=>travel(undo,redo); $('redo-button').onclick=()=>travel(redo,undo);
  $('meta-toggle').onclick=()=>{$('event-meta').hidden=!$('event-meta').hidden;$('meta-toggle').setAttribute('aria-expanded',!$('event-meta').hidden);};
  $('menu-toggle').onclick=()=>document.querySelector('.sidebar').classList.toggle('open');
  $('preview-tab').onclick=()=>switchReader('preview');$('reference-tab').onclick=()=>switchReader('reference');
  $('script-view-tab').onclick=()=>switchDocument('script');$('chart-view-tab').onclick=()=>switchDocument('chart');
  $('comments-tab').onclick=()=>switchReader('comments');$('history-tab').onclick=()=>switchReader('history');
  $('rules-tab').onclick=()=>switchReader('rules');
  $('restart-button').onclick=()=>startReader();
  $('duplicate-event').onclick=()=>{
    if(!active())return;
    const clone=M.copy(active());clone.uid=M.uid();clone.title+='（複製）';clone.status='draft';
    mutation(()=>{state.events.push(clone);state.activeId=clone.uid;});toast('台本を複製しました');
  };
  $('delete-event').onclick=()=>{
    const e=active();if(!e)return;
    confirmAction('台本を削除', `「${e.title}」を削除します。この画面を閉じる前なら「元に戻す」で復元できます。`,()=>mutation(()=>{state.events=state.events.filter(x=>x.uid!==e.uid);state.activeId=state.events[0]?.uid||'';}),'削除する');
  };
  $('check-button').onclick=()=>{
    const checks=review?.report()||{issues:[],reminders:[]};
    const issues=[...M.inspect(active()),...checks.issues.map(x=>({id:x.rowId,message:`${x.rowNumber?x.rowNumber+'行目':'イベント情報'}：表記「${x.bad}」→「${x.good}」 ${x.reason}`,review:x}))];
    openModal('台本の点検',issues.length?'<p class="hint">項目を押すと、その行へ移動します。</p>'+issues.map(x=>`<button class="issue-link" data-issue="${esc(x.id)}">${esc(x.message)}</button>`).join(''):'<p class="hint">空欄や存在しない分岐先は見つかりませんでした。読み合わせで場面のつながりも確認できます。</p>');
    $('modal-body').querySelectorAll('[data-issue]').forEach((button,i)=>button.onclick=()=>{closeModal();if(issues[i].review)focusFinding(issues[i].review);else if(issues[i].id)focusRow(issues[i].id);else $('event-title').focus();});
    $('modal-body').onclick=null;
    if(checks.reminders.length){const b=document.createElement('button');b.textContent=`通読時の注意事項 ${checks.reminders.length}件を確認`;b.onclick=()=>{closeModal();switchReader('rules');};$('modal-body').append(b);}
  };
  $('export-button').onclick=()=>{
    $('modal-body').onclick=null;
    openModal('台本を書き出す',`<p class="hint">JSONは原稿のみの受け渡し用です。履歴・コメント・表記ルールを含める場合は共有保存データ全体をバックアップしてください。Markdownは台本の確認・共有用です。</p><div class="export-grid">${shared?'<button id="export-database"><strong>原稿・履歴・コメント・表記ルールをバックアップ</strong><small>共有保存データ全体をひとつのファイルに保存。</small></button>':''}<button id="export-json"><strong>すべての原稿を書き出す · JSON</strong><small>${state.events.length}件の台本、台詞・選択肢・分岐先・制作メモを保存。</small></button><button id="export-current" ${!active()?'disabled':''}><strong>この台本を書き出す · Markdown</strong><small>話者・本文・演出・選択肢をまとめた原稿。</small></button><button id="export-all" ${!state.events.length?'disabled':''}><strong>すべての台本を書き出す · Markdown</strong><small>台本一覧の順番で、原稿をひとつのファイルに。</small></button></div>`);
    if(shared)$('export-database').onclick=async()=>{if(!await save()){toast('未保存の変更を解消してからバックアップしてください。');return;}const a=document.createElement('a');a.href='/api/backup';a.download='台本_履歴付き_'+stamp()+'.sqlite3';document.body.append(a);a.click();a.remove();closeModal();};
    $('export-json').onclick=()=>{exportProject();closeModal();};
    $('export-current').onclick=()=>{download(M.markdown([active()]),fileName(active().title)+'.md','text/markdown;charset=utf-8');closeModal();};
    $('export-all').onclick=()=>{download(M.markdown(state.events),'台本集.md','text/markdown;charset=utf-8');closeModal();};
  };
  $('import-button').onclick=()=>$('import-file').click();
  $('import-file').onchange=async ev=>{
    const file=ev.target.files[0];ev.target.value='';if(!file)return;
    if(file.size>20*1024*1024){toast('20MB以下のバックアップを選んでください。');return;}
    try{pendingImport=M.validate(JSON.parse(await file.text()));}
    catch(error){toast('読み込めません：'+error.message);return;}
    $('modal-body').onclick=null;
    openModal('バックアップを読み込む',`<p class="hint">${esc(file.name)}：${pendingImport.events.length}件の台本。追加で取り込むか、現在の台本を置き換えられます。</p><div class="modal-actions"><button id="import-replace">バックアップして置き換え</button><button class="primary" id="import-merge">追加して取り込む</button></div>`);
    $('import-merge').onclick=()=>{
      const imported=M.copy(pendingImport.events);
      const seen=new Set(state.events.map(e=>e.uid));
      for(const e of imported){if(seen.has(e.uid)){e.uid=M.uid();e.title+='（取り込み）';}seen.add(e.uid);}
      mutation(()=>{state.events.push(...imported);if(imported.length)state.activeId=imported[0].uid;});
      closeModal();toast(imported.length+'件の台本を追加しました');
    };
    $('import-replace').onclick=()=>{
      exportProject();
      mutation(()=>state=M.copy(pendingImport));closeModal();toast('現在の台本を書き出してから、バックアップを復元しました');
    };
  };
  $('help-button').onclick=()=>{
    $('modal-body').onclick=null;
    openModal('台本室の使い方',`<ol class="help-list"><li><strong>一行ずつ書く</strong><br>台詞は「話者」と「本文」、地の文や演出は本文を入力します。話者・表情は自由入力できます。</li><li><strong>行をつなぐ</strong><br>上の追加ボタンは最後に触った行の後へ、下の「台詞を続ける」は末尾へ追加します。↑ ↓で並べ替え、⧉で複製できます。<span class="key">Ctrl / ⌘ + Enter</span>で同じ種類の行を続けられます。</li><li><strong>選択肢を作る</strong><br>分岐先に「場面目印」を置き、選択肢の行き先から選びます。分岐の末尾には「移動」で合流先を指定するか、「終了」を置きます。条件メモは自動判定されないので、読み合わせ中に確認してください。</li><li><strong>イベント内の流れを見る</strong><br>「イベント内チャート」で、今の台本の選択肢・移動先・合流・終了を図にします。箱を押すと対応する行カードに戻れます。前の行へ戻る線は破線、移動先の不備は赤で表示します。拡大・縮小と「幅に合わせる」で見やすく調整できます。</li><li><strong>読み合わせる</strong><br>右側の開始ボタン、またはカードの▷から読めます。「原案を参照」で既存の設計を確認できます。</li><li><strong>同じ指摘を繰り返さない</strong><br>添削コメントの「ルールにする」でNG表記と推奨表記、または通読時の注意事項を登録できます。「表記」タブで確認し、全台本・特定の台本・特定の話者に適用できます。解決済みのコメントも次回の執筆に引き継ぎます。</li><li><strong>原稿を持ち出す</strong><br>共有保存モードではローカルの共有原稿へ自動保存し、ユーザーとエージェントの変更を履歴に残します。「添削」には台本全体や各行へのコメントを書けます。作業の区切りには、履歴・コメントを含むバックアップを書き出してください。Markdownは読むための原稿、JSONはこのエディタで編集を再開するためのファイルです。</li></ol><p class="hint">元に戻す：Ctrl / ⌘ Z　・　やり直す：Ctrl / ⌘ Shift Z<br>共有編集は作業フォルダで python3 scripts/script_server.py を実行し、表示されるURLから開きます。起動後はターミナルを閉じても使えます。終了時は保存済み表示を確認して python3 scripts/script_server.py stop を実行します。ファイルを直接開く場合はブラウザ内だけに保存されます。既存原稿はJSONを書き出し、共有保存の台本室へ読み込んでください。</p>`);
  };
  $('modal-close').onclick=closeModal;
  $('modal').addEventListener('click',ev=>{if(ev.target===$('modal')){const r=$('modal').getBoundingClientRect();if(ev.clientX<r.left||ev.clientX>r.right||ev.clientY<r.top||ev.clientY>r.bottom)closeModal();}});
  document.addEventListener('keydown',ev=>{
    if(ev.isComposing || !(ev.ctrlKey||ev.metaKey))return;
    if(remoteBusy){ev.preventDefault();return;}
    if(ev.key.toLowerCase()==='s'){ev.preventDefault();if(ev.shiftKey)exportProject();else explicitSave();return;}
    if(ev.target.closest('#comments-panel'))return;
    if($('modal').open)return;
    if(ev.key.toLowerCase()==='z'){ev.preventDefault();if(ev.shiftKey)travel(redo,undo);else travel(undo,redo);}
    if(ev.key.toLowerCase()==='y'){ev.preventDefault();travel(redo,undo);}
    if(ev.key==='Enter' && active()){
      ev.preventDefault();const current=findRow(lastRow);addRow(current && ['dialogue','narration','direction'].includes(current.type)?current.type:'dialogue',lastRow);
    }
  });
  window.addEventListener('beforeunload',ev=>{save();if(dirty){ev.preventDefault();ev.returnValue='';}});
  document.addEventListener('visibilitychange',()=>{if(document.hidden)save();});
  window.addEventListener('storage',ev=>{
    if(!shared && ev.key===KEY && ev.newValue!==lastSavedRaw){storagePaused=true;showStorageWarning('別のタブで台本が更新されました。このタブの自動保存を停止しています。');}
  });
  async function explicitSave(){await save();toast(dirty||storagePaused?'保存できていません。上部の案内を確認してください。':shared?'共有原稿に保存済みです':'ブラウザに保存済みです');}
  function updateReviewData(snapshot){
    if(snapshot.cursor>=shared.cursor){shared.comments=snapshot.comments;shared.rules=snapshot.rules;shared.cursor=snapshot.cursor;}
    collaboration?.refresh();review?.refresh();updateStats();
  }
  function focusFinding(issue){
    if(!issue.rowId){switchDocument('script');if(issue.field!=='title')$('event-meta').hidden=false;$('event-'+issue.field)?.focus();return;}
    focusRow(issue.rowId);
    const row=$('row-'+issue.rowId);if(!row)return;
    if(issue.optionId){const option=Array.from(row.querySelectorAll('[data-option]')).find(el=>el.dataset.option===issue.optionId);option?.querySelector('[data-option-field="text"]')?.focus();}
    else row.querySelector('[data-field="'+issue.field+'"]')?.focus();
  }
  $('save-button').onclick=explicitSave;
  chart=window.ScriptEditorChart.create({host:$('chart-panel'),active,selectedRow:()=>lastRow,onNavigate:(rowId,optionId)=>{
    focusRow(rowId);
    if(optionId){
      const option=Array.from($('row-'+rowId).querySelectorAll('[data-option]')).find(el=>el.dataset.option===optionId);
      option?.querySelector('[data-option-field="text"]')?.focus();
    }
  }});
  review=window.ScriptReview.create({$,esc,active,getEvents:()=>state.events,getShared:()=>shared,request,save,isDirty:()=>dirty||storagePaused,toast,openModal,closeModal,onUpdate:updateReviewData,onFinding:focusFinding});
  collaboration=window.createCollaboration({$,esc,active,request,save,toast,openModal,closeModal,confirmAction,focusRow,openRule:source=>review.open(source),
    getShared:()=>shared, isDirty:()=>dirty||storagePaused,
    updateComments:updateReviewData,
    restore:async(entryId,eventUid)=>{
      if(!await save())throw new Error('未保存の変更を解消してから復元してください。');
      remoteBusy=true;document.querySelector('.layout').inert=true;document.querySelector('.header-actions').inert=true;
      try{acceptRemote(await request('/api/restore',{revision:shared.revision,entryId,eventUid}));}
      catch(error){if(error.status===409)pauseShared(error.message);throw error;}
      finally{remoteBusy=false;document.querySelector('.layout').inert=false;document.querySelector('.header-actions').inert=false;}
    }});
  $('storage-description').textContent=shared?'共有原稿へ自動保存します。ユーザーとエージェントの変更は履歴で確認できます。':'このブラウザに保存します。共有編集・履歴・添削にはローカルサーバーを起動してください。';
  if(sharedProblem)pauseShared(sharedProblem);
  render();
  openLinkedEvent();
  window.addEventListener('hashchange',openLinkedEvent);
  if(shared){if(dirty&&!storagePaused)save();else if(!storagePaused)sharedStatus();}
  else if(!lastSavedRaw && !storagePaused){dirty=true;save();}
  else if(!storagePaused){$('save-state').textContent='ブラウザに保存済み';$('save-state').className='save-state saved';}
  let polling=false;
  if(shared)setInterval(async()=>{
    if(document.hidden||polling||saving||remoteBusy||storagePaused)return;
    polling=true;
    try {
      const version=editVersion,base=shared.revision,snapshot=await request('/api/project');
      if(saving||remoteBusy||storagePaused||shared.revision!==base)return;
      if(snapshot.revision!==shared.revision){
        if(dirty||version!==editVersion){pauseShared('共有原稿に新しい編集があります。このタブの変更を退避してから最新版を開いてください。');return;}
        acceptRemote(snapshot);toast('共有原稿の変更を読み込みました');
      }else if(snapshot.cursor>shared.cursor)updateReviewData(snapshot);
    }catch(_){pauseShared('共有保存に接続できません。サーバーを確認して「接続を再試行」を押してください。');}
    finally{polling=false;}
  },3000);
})();
