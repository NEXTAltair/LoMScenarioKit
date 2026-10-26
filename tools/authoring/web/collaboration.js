/* Comments and audit history stay outside the manuscript interchange format. */
window.createCollaboration = function (app) {
  'use strict';
  const {$,esc,active,request,save,toast,openModal,closeModal,confirmAction,focusRow,getShared,isDirty,updateComments,restore,openRule}=app;
  const shared=!!getShared();
  const actorName=actor=>actor==='agent'?'エージェント':'ユーザー';
  const date=value=>new Date(value).toLocaleString('ja-JP');
  const badge=item=>`<span class="author-badge ${item.actor}">${actorName(item.actor)}</span> <span>${esc(item.name)}</span><time>${esc(date(item.timestamp))}</time>`;
  const kindName={save:'原稿を保存',restore:'原稿を復元',comment:'添削コメント',resolve:'コメントの状態変更',rule:'再発防止ルール'};
  let currentEvent='',replyTo='',posting=false,historyKey='',historyRows=[],historyRequest=0;
  const drafts=new Map();
  if(!shared){
    const guidance='<p class="hint">共有編集・履歴・添削コメントを使うには、作業フォルダで次のコマンドを実行します。</p><pre class="setup-command">python3 tools/authoring/author.py --project /path/to/project serve</pre><p><a href="http://127.0.0.1:8765/script-editor.html">共有保存の台本室を開く ↗</a></p><p class="hint">終了時は原稿の保存を確認し、ターミナルで Ctrl+C を押してください。</p><p class="hint">このブラウザの既存原稿は「書き出す」からJSONに保存し、共有保存の台本室で読み込んでください。</p>';
    $('comments-panel').innerHTML='<h3>添削コメント</h3>'+guidance;
    $('history-panel').innerHTML='<h3>編集履歴</h3>'+guidance;
    return {refresh(){},target(){}};
  }
  $('comments-panel').innerHTML=`<h3>添削コメント</h3><p class="hint">表現の提案や確認事項を、本文と分けて残せます。</p>
    <form id="comment-form"><label>添削する箇所<select id="comment-target"><option value="">台本全体</option></select></label>
    <div id="reply-context" class="hint" hidden></div><textarea id="comment-text" rows="4" maxlength="20000" placeholder="直したい箇所・提案・理由など" aria-label="添削コメント"></textarea>
    <label class="comment-rule-toggle"><input id="comment-make-rule" type="checkbox"> 次回も使うルールにする</label><div class="comment-form-actions"><button type="button" id="cancel-reply" hidden>返信をやめる</button><button type="submit" id="post-comment" class="primary">コメントを保存</button></div></form>
    <label class="resolved-filter"><input type="checkbox" id="show-resolved" checked> 解決済みも表示</label><div id="comment-list"></div>`;
  $('history-panel').innerHTML=`<h3>編集履歴</h3><p class="hint">保存した人と変更内容を確認できます。復元した操作も履歴に残ります。</p>
    <div class="history-filters"><label>編集者<select id="history-actor"><option value="">全員</option><option value="human">ユーザー</option><option value="agent">エージェント</option></select></label>
    <label>対象<select id="history-scope"><option value="event">この台本</option><option value="all">すべての台本</option></select></label></div>
    <div id="history-list"></div><button id="history-more" class="quiet" hidden>以前の履歴を読む</button>`;
  function draftKey(id){return JSON.parse(document.getElementById('project-config').textContent).storageKey+':comment:'+id;}
  function rememberDraft(){
    if(!currentEvent)return;
    const value={text:$('comment-text').value,row:$('comment-target').value,reply:replyTo};
    drafts.set(currentEvent,value);
    try{sessionStorage.setItem(draftKey(currentEvent),JSON.stringify(value));}catch(_){}
  }
  function refreshComments(){
    const e=active(),comments=getShared().comments.filter(c=>c.event_uid===e?.uid);
    if(currentEvent!==(e?.uid||'')){
      rememberDraft();currentEvent=e?.uid||'';
      let draft=drafts.get(currentEvent);
      if(!draft)try{draft=JSON.parse(sessionStorage.getItem(draftKey(currentEvent)));}catch(_){}
      $('comment-text').value=draft?.text||'';replyTo=draft?.reply||'';
      $('comment-target').innerHTML='<option value="">台本全体</option>';
      $('comment-target').dataset.pending=draft?.row||'';
    }
    const selected=$('comment-target').dataset.pending??$('comment-target').value;
    delete $('comment-target').dataset.pending;
    $('comment-target').innerHTML='<option value="">台本全体</option>'+(e?.rows||[]).map((r,i)=>`<option value="${esc(r.id)}">${i+1}行目：${esc(r.text.slice(0,35)||'（本文未記入）')}</option>`).join('');
    if(selected && !e?.rows.some(r=>r.id===selected))$('comment-target').insertAdjacentHTML('beforeend',`<option value="${esc(selected)}">削除された行（対象を選び直してください）</option>`);
    $('comment-target').value=selected;
    const parent=comments.find(c=>c.id===replyTo&&!c.parent_id);
    if(replyTo&&!parent)replyTo='';
    $('reply-context').hidden=!parent;$('cancel-reply').hidden=!parent;
    $('reply-context').textContent=parent?`${actorName(parent.actor)}への返信：${parent.text.slice(0,90)}`:'';
    $('comment-target').disabled=!e||!!parent;
    $('comment-text').disabled=!e;$('post-comment').disabled=!e||posting;
    function body(c){return `<div class="comment-author">${badge(c)}</div><p class="comment-body">${esc(c.text)}</p>`;}
    $('comment-list').innerHTML=comments.filter(c=>!c.parent_id&&($('show-resolved').checked||!c.resolved)).map(c=>{
      const row=e.rows.findIndex(r=>r.id===c.row_id);
      return `<article class="comment-card ${c.resolved?'resolved':''}" data-comment="${esc(c.id)}"><div class="comment-context">${c.row_id?(row>=0?`<button class="quiet small" data-comment-row="${esc(c.row_id)}">${row+1}行目へ ↗</button>`:'削除された行へのコメント'):'台本全体'}${c.resolved?'<span>解決済み</span>':''}</div>
        ${c.row_id?`<blockquote>${esc(c.quote||'（本文未記入）')}</blockquote>`:''}${body(c)}
        ${comments.filter(r=>r.parent_id===c.id).map(r=>`<div class="comment-reply">${body(r)}<button class="quiet small" data-comment-rule="${esc(r.id)}">ルールにする</button></div>`).join('')}
        <div class="comment-actions"><button class="quiet small" data-reply="${esc(c.id)}">返信</button><button class="quiet small" data-resolve="${esc(c.id)}">${c.resolved?'未解決に戻す':'解決済みにする'}</button><button class="quiet small" data-comment-rule="${esc(c.id)}">ルールにする</button></div></article>`;
    }).join('')||'<p class="hint">'+(e?'表示するコメントはありません。':'台本を選ぶとコメントを書けます。')+'</p>';
  }
  async function loadHistory(more=false){
    const event=$('history-scope').value==='event'?active()?.uid||'__none__':'';
    const actor=$('history-actor').value;
    const key=[event,actor,getShared().cursor].join(':');
    if(!more && key===historyKey)return;
    historyKey=key;
    const sequence=++historyRequest;
    if(!more){historyRows=[];$('history-list').innerHTML='<p class="hint">履歴を読み込み中…</p>';$('history-more').hidden=true;}
    $('history-more').disabled=true;
    try{
      const query=new URLSearchParams({event,actor,before:more?historyRows.at(-1)?.id||0:0});
      const rows=await request('/api/history?'+query);
      if(sequence!==historyRequest)return;
      historyRows=more?[...historyRows,...rows]:rows;
      $('history-list').innerHTML=historyRows.map(item=>`<button class="history-entry" data-history="${item.id}"><span class="comment-author">${badge(item)}</span><strong>#${item.id} · ${esc(kindName[item.kind]||item.kind)}</strong><span>${esc(item.message)}</span></button>`).join('')||'<p class="hint">該当する履歴はありません。</p>';
      $('history-more').hidden=rows.length<50;
    }catch(error){if(sequence===historyRequest){historyKey='';$('history-list').textContent=error.message;}}
    finally{if(sequence===historyRequest)$('history-more').disabled=false;}
  }
  const valueText=value=>value===null?'（なし）':typeof value==='string'?value:JSON.stringify(value,null,2);
  async function showEntry(id){
    try{
      const item=await request('/api/history/'+id);
      const changes=item.detail.changes||[];
      const candidates=(item.snapshot?.events||[]).filter(e=>item.event_uids.includes(e.uid));
      let content=`<div class="comment-author">${badge(item)}</div><p>${esc(item.message)}</p>`;
      content+=changes.map(c=>`<section class="history-change"><h4>${esc(c.sourceId||c.eventTitle||'台本一覧')} · ${esc(c.field)}</h4>${c.rowId?`<small>行ID：${esc(c.rowId)}</small>`:''}<div class="change-columns"><div><span>変更前</span><pre>${esc(valueText(c.before))}</pre></div><div><span>変更後</span><pre>${esc(valueText(c.after))}</pre></div></div></section>`).join('');
      if(item.detail.comment){const c=item.detail.comment;content+=`<p class="hint">${c.row_id?'行への添削':'台本全体への添削'}</p><blockquote>${esc(c.quote)}</blockquote><p class="comment-body">${esc(c.text)}</p>`;}
      if(item.kind==='resolve')content+=`<p class="comment-body">${esc(item.detail.text)}</p>`;
      if(item.kind==='rule')content+=`<div class="change-columns"><div><span>変更前</span><pre>${esc(valueText(item.detail.before))}</pre></div><div><span>変更後</span><pre>${esc(valueText(item.detail.after))}</pre></div></div>`;
      if(candidates.length)content+=`<div class="restore-controls"><label>この時点に戻す台本<select id="restore-event">${candidates.map(e=>`<option value="${esc(e.uid)}" ${e.uid===active()?.uid?'selected':''}>${esc(e.sourceId+' '+e.title)}</option>`).join('')}</select></label><button id="restore-history">この台本を復元</button><p class="hint">選んだ台本を復元します。他の台本と添削コメントはそのまま残ります。</p></div>`;
      $('modal-body').onclick=null;openModal('編集履歴 #'+id,content);
      if(candidates.length)$('restore-history').onclick=()=>{
        const uid=$('restore-event').value;
        confirmAction('過去の台本を復元',`履歴 #${id} の内容に戻し、ユーザーの編集として新しく保存します。`,async()=>{
          try{await restore(id,uid);toast('過去の台本を復元しました');refresh();}catch(error){toast(error.message);}
        },'復元して保存');
      };
    }catch(error){toast(error.message);}
  }
  function refresh(){refreshComments();if(!$('history-panel').hidden)loadHistory();}
  function target(row){replyTo='';refreshComments();$('comment-target').value=row;rememberDraft();$('comment-text').focus();$('comments-panel').scrollIntoView({block:'nearest'});}
  $('comment-text').oninput=rememberDraft;$('comment-target').onchange=rememberDraft;
  $('cancel-reply').onclick=()=>{replyTo='';rememberDraft();refreshComments();};
  $('show-resolved').onchange=refreshComments;
  $('comment-form').onsubmit=async ev=>{
    ev.preventDefault();if(posting||!active())return;
    const text=$('comment-text').value,eventUid=active().uid,rowId=$('comment-target').value,parentId=replyTo,makeRule=$('comment-make-rule').checked;
    const known=new Set(getShared().comments.map(c=>c.id));
    if(!text.trim()){toast('コメントを入力してください。');return;}
    posting=true;$('post-comment').disabled=true;
    try{
      if(!await save()||isDirty())throw new Error('原稿の未保存の変更を解消してからコメントを保存してください。');
      const snapshot=await request('/api/comments',{revision:getShared().revision,eventUid,rowId,parentId,text});
      if(active()?.uid===eventUid&&$('comment-text').value===text){$('comment-text').value='';replyTo='';rememberDraft();}
      else if(active()?.uid!==eventUid && drafts.get(eventUid)?.text===text){drafts.delete(eventUid);try{sessionStorage.removeItem(draftKey(eventUid));}catch(_){}}
      updateComments(snapshot);toast('コメントを保存しました');
      if(makeRule){const source=snapshot.comments.find(c=>snapshot.commentId?c.id===snapshot.commentId:!known.has(c.id)&&c.actor==='human'&&c.event_uid===eventUid&&c.text===text);if(source){$('comment-make-rule').checked=false;openRule(source);}}
    }catch(error){toast(error.message);}
    finally{posting=false;refreshComments();}
  };
  $('comment-list').onclick=async ev=>{
    const rule=ev.target.closest('[data-comment-rule]');if(rule){openRule(getShared().comments.find(c=>c.id===rule.dataset.commentRule));return;}
    const row=ev.target.closest('[data-comment-row]');if(row){focusRow(row.dataset.commentRow);return;}
    const reply=ev.target.closest('[data-reply]');if(reply){replyTo=reply.dataset.reply;refreshComments();rememberDraft();$('comment-text').focus();return;}
    const resolve=ev.target.closest('[data-resolve]');if(!resolve)return;
    const c=getShared().comments.find(c=>c.id===resolve.dataset.resolve);resolve.disabled=true;
    try{updateComments(await request('/api/comments/'+c.id+'/resolve',{resolved:!c.resolved}));}
    catch(error){toast(error.message);resolve.disabled=false;}
  };
  $('history-actor').onchange=()=>loadHistory();$('history-scope').onchange=()=>loadHistory();
  $('history-more').onclick=()=>loadHistory(true);
  $('history-list').onclick=ev=>{const b=ev.target.closest('[data-history]');if(b)showEntry(b.dataset.history);};
  return {refresh,target};
};
