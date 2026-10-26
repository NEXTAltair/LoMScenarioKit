/* User-confirmed spelling rules. Keep literal matching in sync with review_rules.py. */
(function(root){
  'use strict';
  function applies(rule,event){
    if(!event)return false;
    if(rule.scope==='event')return rule.eventUid===event.uid;
    if(rule.scope==='speaker')return event.rows.some(r=>r.type==='dialogue'&&r.speaker===rule.speaker);
    return true;
  }
  function matches(text,rule){
    const {bad,good}=rule;if(!bad)return false;
    if(rule.match==='exact')return text===bad;
    if(!good||!good.includes(bad))return text.includes(bad);
    let preferred=text.indexOf(good);
    for(let at=text.indexOf(bad);at>=0;at=text.indexOf(bad,at+1)){
      while(preferred>=0&&preferred+good.length<at+bad.length)preferred=text.indexOf(good,preferred+1);
      if(preferred<0||preferred>at)return true;
    }
    return false;
  }
  function inspect(event,rules=[]){
    const issues=[],reminders=[];if(!event)return {issues,reminders};
    for(const rule of rules){
      if(rule.status!=='active'||!applies(rule,event))continue;
      if(rule.kind==='reminder'){reminders.push(rule);continue;}
      const fields=[];
      if(['all','metadata'].includes(rule.field)&&rule.scope!=='speaker')for(const key of ['title','place','period'])fields.push(['',0,key,'',event[key]||'']);
      event.rows.forEach((r,i)=>{
        if(rule.scope==='speaker'&&(r.type!=='dialogue'||r.speaker!==rule.speaker))return;
        if(['all','text'].includes(rule.field)){
          fields.push([r.id,i+1,'text','',r.text]);
          for(const o of r.options||[])fields.push([r.id,i+1,'option',o.id,o.text]);
        }
        for(const key of ['speaker','expression'])if(['all',key].includes(rule.field)&&r.type==='dialogue')fields.push([r.id,i+1,key,'',r[key]]);
      });
      for(const [rowId,rowNumber,field,optionId,text] of fields)if(matches(text,rule))issues.push({ruleId:rule.id,eventUid:event.uid,sourceId:event.sourceId,rowId,rowNumber,field,optionId,bad:rule.bad,good:rule.good,reason:rule.reason,commentId:rule.commentId,text});
    }
    return {issues,reminders};
  }
  function create({$,esc,active,getEvents,getShared,request,save,isDirty,toast,openModal,closeModal,onUpdate,onFinding}){
    const statusNames={active:'確定',proposed:'確認待ち',disabled:'無効'};
    const fieldNames={text:'本文・選択肢',speaker:'話者名',expression:'表情',metadata:'タイトル・場所・時期',all:'すべての原稿欄'};
    const scopeName=r=>r.scope==='project'?'全台本':r.scope==='speaker'?'話者：'+r.speaker:'台本：'+(getEvents().find(e=>e.uid===r.eventUid)?.title||'削除された台本');
    const report=()=>inspect(active(),getShared()?.rules||[]);
    let findings=[];
    if(!getShared()){
      $('rules-panel').innerHTML='<h3>表記と再発防止</h3><p class="hint">共有保存の台本室で使えます。コメントを表記ルールや注意事項にして、次回の執筆・添削でも参照します。</p><pre class="setup-command">python3 tools/authoring/author.py --project /path/to/project serve</pre><a href="http://127.0.0.1:8765/script-editor.html">共有保存の台本室を開く ↗</a>';
      return {report,refresh(){},open(){}};
    }
    $('rules-panel').innerHTML=`<h3>表記と再発防止</h3><p class="hint">指摘を次の執筆にも引き継ぎます。確定したNG表記は自動検査し、文体などの注意事項は通読時に確認します。</p>
      <button id="new-review-rule">＋ ルール・注意事項を登録</button><div id="review-findings"></div>
      <label class="rule-filter">登録済みの指摘<select id="rule-filter"><option value="applicable">この台本に適用</option><option value="all">すべて</option></select></label><div id="rule-list"></div>`;
    function sourceHTML(source){
      if(!source)return '';
      return `<details class="rule-source"><summary>元のコメント（${source.actor==='human'?'ユーザー':'エージェント'}）</summary><blockquote>${esc(source.quote)}</blockquote><p class="comment-body">${esc(source.text)}</p></details>`;
    }
    function refresh(){
      const {issues,reminders}=report();findings=issues;
      $('rules-tab').textContent='表記'+(issues.length?' '+issues.length:'');
      if(!Array.isArray(getShared().rules)){$('review-findings').textContent='この機能を使うにはサーバーを再起動してください。';$('new-review-rule').disabled=true;return;}
      $('new-review-rule').disabled=!active();
      $('review-findings').innerHTML=`<h4>この台本の検査 · ${issues.length}件</h4>`+(issues.length?issues.map((x,i)=>`<button class="review-finding" data-finding="${i}"><small>${x.rowNumber?x.rowNumber+'行目':esc(fieldNames.metadata)}</small><strong>${esc(x.bad)} → ${esc(x.good)}</strong><span>${esc(x.reason)}</span></button>`).join(''):'<p class="hint">確定した表記ルールに該当するNG表記はありません。</p>')+
        (reminders.length?`<h4>通読時の注意事項 · ${reminders.length}件</h4>`+reminders.map(r=>`<p class="review-reminder">${esc(r.reason)}</p>`).join(''):'');
      const rules=getShared().rules.filter(r=>$('rule-filter').value==='all'||applies(r,active()));
      $('rule-list').innerHTML=rules.map(r=>`<article class="rule-card ${r.status}" data-rule="${esc(r.id)}"><div class="comment-author"><span class="rule-status">${statusNames[r.status]}</span><span>${esc(scopeName(r))}</span></div><strong>${r.kind==='literal'?esc(r.bad)+' → '+esc(r.good):'通読時の注意事項'}</strong><p class="comment-body">${esc(r.reason)}</p>${r.kind==='literal'?`<small>${esc(fieldNames[r.field])} · ${r.match==='exact'?'欄全体の一致':'文字列を含む場合'}</small>`:''}${sourceHTML(r.source)}<div class="rule-actions"><button class="quiet small" data-rule-edit="${esc(r.id)}">編集</button>${r.status!=='active'?`<button class="small" data-rule-status="active" data-rule-id="${esc(r.id)}">${r.status==='proposed'?'採用する':'有効にする'}</button>`:''}${r.status!=='disabled'?`<button class="quiet small" data-rule-status="disabled" data-rule-id="${esc(r.id)}">${r.status==='proposed'?'採用しない':'無効にする'}</button>`:''}</div></article>`).join('')||'<p class="hint">まだルールはありません。添削コメントの「ルールにする」からも登録できます。</p>';
    }
    function open(source=null,existing=null){
      if(!active()||!Array.isArray(getShared().rules)){toast('共有保存の台本を開いてください。');return;}
      const event=getEvents().find(e=>e.uid===(existing?.eventUid||source?.event_uid))||active(),row=event.rows.find(r=>r.id===source?.row_id);
      const draft=existing||{kind:source?'reminder':'literal',bad:'',good:'',reason:source?.text||'',scope:'event',eventUid:event.uid,speaker:row?.speaker||'',field:'text',match:'contains',status:'active',commentId:source?.id||''};
      source=existing?.source||source;
      $('modal-body').onclick=null;
      const options=(map,selected)=>Object.entries(map).map(([value,label])=>`<option value="${value}" ${selected===value?'selected':''}>${label}</option>`).join('');
      openModal(existing?'再発防止ルールを編集':'この指摘を次回にも引き継ぐ',`<form id="rule-form" class="rule-form"><p class="hint">表記の修正は文字列で検査します。文体・設定などは「通読時の注意事項」に残してください。</p>
        <label>種類<select id="rule-kind">${options({literal:'表記ルール（自動検査）',reminder:'通読時の注意事項'},draft.kind)}</select></label>
        <div id="literal-fields" class="rule-pair"><label>NG表記<input id="rule-bad" maxlength="1000" value="${esc(draft.bad)}" placeholder="繰り返したくない表記"></label><label>推奨表記<input id="rule-good" maxlength="1000" value="${esc(draft.good)}" placeholder="正しい表記"></label></div>
        <label>理由・注意事項<textarea id="rule-reason" rows="3" maxlength="20000">${esc(draft.reason)}</textarea></label>
        <div class="rule-pair"><label>適用範囲<select id="rule-scope">${options({event:'選択した台本のみ',project:'全台本',speaker:'この話者の台詞（全台本）'},draft.scope)}</select><small>台本限定の対象：${esc(event.title)}</small></label><label id="rule-speaker-label">話者<input id="rule-speaker" list="speakers" value="${esc(draft.speaker)}" placeholder="話者欄と同じ名前"></label></div>
        <div id="rule-match-fields" class="rule-pair"><label>検査する欄<select id="rule-field">${options(fieldNames,draft.field)}</select></label><label>一致の条件<select id="rule-match">${options({contains:'文字列が含まれる場合',exact:'欄全体が一致する場合'},draft.match)}</select></label></div>
        <label>状態<select id="rule-status">${options(statusNames,draft.status)}</select></label>${sourceHTML(source)}
        <p id="rule-error" class="rule-error" role="alert" hidden></p><div class="modal-actions"><button type="button" id="cancel-rule">キャンセル</button><button class="primary" id="save-rule" type="submit">ルールを保存</button></div></form>`);
      function toggle(){const literal=$('rule-kind').value==='literal';$('literal-fields').hidden=!literal;$('rule-match-fields').hidden=!literal;$('rule-bad').required=literal;$('rule-good').required=literal;$('rule-reason').required=!literal;const speaker=$('rule-scope').value==='speaker';$('rule-speaker-label').hidden=!speaker;$('rule-speaker').required=speaker;}
      $('rule-kind').onchange=toggle;$('rule-scope').onchange=toggle;toggle();$('cancel-rule').onclick=closeModal;
      $('rule-form').onsubmit=async ev=>{
        ev.preventDefault();$('save-rule').disabled=true;$('rule-error').hidden=true;
        try{
          if(!await save()||isDirty())throw new Error('原稿の未保存の変更を解消してから登録してください。');
          const rule={...draft};for(const key of ['kind','bad','good','reason','scope','speaker','field','match','status'])rule[key]=$('rule-'+key).value;
          if(rule.scope==='event'&&!rule.eventUid)rule.eventUid=event.uid;
          const result=await request('/api/rules',{rule,id:existing?.id||'',version:existing?.version});
          closeModal();onUpdate(result);toast(rule.status==='active'?'指摘を再発防止ルールに登録しました':'ルールを保存しました');
        }catch(error){$('rule-error').hidden=false;$('rule-error').textContent=error.status===409?'別の操作でルールが更新されました。閉じて最新のルールを開き直してください。':error.message;}
        finally{if($('save-rule'))$('save-rule').disabled=false;}
      };
    }
    $('new-review-rule').onclick=()=>open();$('rule-filter').onchange=refresh;
    $('review-findings').onclick=ev=>{const b=ev.target.closest('[data-finding]');if(b)onFinding(findings[Number(b.dataset.finding)]);};
    $('rule-list').onclick=async ev=>{
      const edit=ev.target.closest('[data-rule-edit]');if(edit){open(null,getShared().rules.find(r=>r.id===edit.dataset.ruleEdit));return;}
      const button=ev.target.closest('[data-rule-status]');if(!button)return;
      const rule=getShared().rules.find(r=>r.id===button.dataset.ruleId);button.disabled=true;
      try{onUpdate(await request('/api/rules',{rule:{...rule,status:button.dataset.ruleStatus},id:rule.id,version:rule.version}));toast('ルールの状態を保存しました');}
      catch(error){toast(error.status===409?'ルールが更新されています。最新の内容を確認してください。':error.message);button.disabled=false;}
    };
    return {report,refresh,open};
  }
  const api={applies,matches,inspect,create};
  if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.ScriptReview=api;
})(globalThis);
