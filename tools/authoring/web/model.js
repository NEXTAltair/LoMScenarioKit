/* Shared document format and read-through logic. No browser dependencies. */
(function (root) {
  'use strict';
  const FORMAT = 'lom-event-scripts';
  const TYPES = ['dialogue', 'narration', 'direction', 'choice', 'label', 'jump', 'end'];
  const NAMES = {dialogue:'台詞', narration:'地の文', direction:'演出', choice:'選択肢', label:'場面目印', jump:'移動', end:'終了'};
  const uid = () => 'r_' + (globalThis.crypto?.randomUUID?.() || Date.now().toString(36) + Math.random().toString(36).slice(2));
  const copy = value => JSON.parse(JSON.stringify(value));
  function row(type = 'dialogue', speaker = '') {
    const result = {id:uid(), type, text:''};
    if (type === 'dialogue') Object.assign(result, {speaker, expression:''});
    if (type === 'jump') result.target = '';
    if (type === 'choice') result.options = [option(), option()];
    return result;
  }
  function option() { return {id:uid(), text:'', target:'', condition:''}; }
  function event(source) {
    return {uid:uid(), sourceId:source?.id || '', title:source?.title || '新しい台本',
      period:source?.period || '', route:source?.route || 'common', place:'', status:'draft', verdict:'', notes:'',
      rows:[row('narration'), row('dialogue')]};
  }
  function project(events = []) { return {format:FORMAT, version:1, updatedAt:'', activeId:events[0]?.uid || '', events}; }
  function validate(raw) {
    const fail = message => { throw new Error(message); };
    const obj = (x, where) => { if (!x || typeof x !== 'object' || Array.isArray(x)) fail(where + 'の形式が不正です。'); };
    const string = (x, where) => { if (typeof x !== 'string') fail(where + 'が文字列ではありません。'); return x; };
    const list = (x, where) => { if (!Array.isArray(x)) fail(where + 'が一覧ではありません。'); return x; };
    obj(raw, 'ファイル');
    if (raw.format !== FORMAT || raw.version !== 1) fail('このエディタのバックアップ（形式1）を選んでください。');
    const events = list(raw.events, '台本');
    if (events.length > 1000) fail('台本が多すぎます（上限1000件）。');
    const eventIds = new Set();
    let totalRows = 0;
    const clean = project(events.map((e, ei) => {
      obj(e, '台本');
      const out = {};
      for (const key of ['uid','sourceId','title','period','route','place','status','notes']) out[key] = string(e[key], `台本${ei+1}の${key}`);
      if (!out.uid || eventIds.has(out.uid)) fail('台本の識別子が空または重複しています。');
      eventIds.add(out.uid);
      if (!out.route.trim()) fail('ルートの値が不正です。');
      if (!['draft','writing','review','done'].includes(out.status)) fail('執筆状況の値が不正です。');
      out.verdict = e.verdict ?? '';
      if (!['','good','fix','hold','reject'].includes(out.verdict)) fail('判定タグが不正です。');
      const rows = list(e.rows, '行');
      totalRows += rows.length;
      if (totalRows > 30000) fail('行数が多すぎます（上限30000行）。');
      const ids = new Set();
      out.rows = rows.map(r => {
        obj(r, '行');
        const item = {id:string(r.id,'行ID'), type:string(r.type,'行の種類'), text:string(r.text,'本文')};
        if (!item.id || ids.has(item.id)) fail('同じ台本内の行IDが空または重複しています。');
        ids.add(item.id);
        if (!TYPES.includes(item.type)) fail('未対応の行の種類です。');
        if (item.type === 'dialogue') Object.assign(item, {speaker:string(r.speaker,'話者'), expression:string(r.expression,'表情')});
        if (item.type === 'jump') item.target = string(r.target, '移動先');
        if (item.type === 'choice') {
          const opts = list(r.options,'選択肢');
          if (opts.length > 100) fail('一行の選択肢は100件までです。');
          const optIds = new Set();
          item.options = opts.map(o => {
            obj(o,'選択肢');
            const opt = {};
            for (const key of ['id','text','target','condition']) opt[key] = string(o[key], '選択肢の'+key);
            if (!opt.id || optIds.has(opt.id)) fail('選択肢IDが空または重複しています。');
            optIds.add(opt.id);
            return opt;
          });
        }
        return item;
      });
      return out;
    }));
    clean.updatedAt = string(raw.updatedAt, '保存日時');
    const active = string(raw.activeId, '選択中の台本');
    clean.activeId = eventIds.has(active) ? active : clean.events[0]?.uid || '';
    return clean;
  }
  function inspect(e) {
    const issues = [];
    const labels = new Set(e.rows.filter(r=>r.type==='label').map(r=>r.id));
    const add = (r, message) => issues.push({id:r?.id || '', message});
    if (!e.title.trim()) add(null, '台本のタイトルが空です。');
    for (const [i,r] of e.rows.entries()) {
      const prefix = `${i+1}行目：`;
      if (['dialogue','narration','direction','label'].includes(r.type) && !r.text.trim()) add(r, prefix+NAMES[r.type]+'が空です。');
      if (r.type==='dialogue' && !r.speaker.trim()) add(r, prefix+'話者を入力してください。');
      if (r.type==='jump' && !labels.has(r.target)) add(r, prefix+'移動先の場面目印がありません。');
      if (r.type==='choice') {
        if (r.options.length<2) add(r, prefix+'選択肢が2件未満です。');
        for (const [j,o] of r.options.entries()) {
          if (!o.text.trim()) add(r, prefix+`選択肢${j+1}の本文が空です。`);
          if (o.target && !labels.has(o.target)) add(r, prefix+`選択肢${j+1}の分岐先がありません。`);
        }
      }
    }
    return issues;
  }
  // Resolve control cards; visible cards are advanced only by the reader.
  function beat(rows, start) {
    let index = start;
    const visited = new Set();
    while (index < rows.length) {
      if (index < 0) return {error:'分岐先の場面目印が見つかりません。'};
      if (visited.has(index)) return {error:'移動が循環しています。場面目印と移動先を確認してください。'};
      visited.add(index);
      const r = rows[index];
      if (r.type==='label') { index++; continue; }
      if (r.type==='jump') {
        index = rows.findIndex(x=>x.type==='label' && x.id===r.target);
        continue;
      }
      if (['dialogue','narration','direction'].includes(r.type) && !r.text.trim()) { index++; continue; }
      if (r.type==='end') return {end:true, index, row:r};
      return {index, row:r};
    }
    return {end:true, index:rows.length};
  }
  function choose(rows, index, opt) {
    return beat(rows, opt.target ? rows.findIndex(r=>r.type==='label' && r.id===opt.target) : index+1);
  }
  function markdown(events) {
    return events.map(e => {
      const lines = ['# '+e.title, '', `- イベントID：${e.sourceId || '未設定'}`, `- 時期：${e.period || '未設定'}`,
        `- 場所：${e.place || '未設定'}`, `- ルート：${e.route}`,
        `- 判定：${({good:'良い',fix:'要修正',hold:'保留・別案',reject:'ボツ'})[e.verdict]||'未判定'}`, `- 執筆状況：${({draft:'未着手',writing:'執筆中',review:'推敲中',done:'執筆完了'})[e.status]}`, '', '## 台本', ''];
      const targetName = id => e.rows.find(r=>r.type==='label' && r.id===id)?.text || '未設定の分岐先';
      for (const [i,r] of e.rows.entries()) {
        lines.push(`### ${String(i+1).padStart(3,'0')}｜${NAMES[r.type]}`, '');
        if (r.type==='dialogue') lines.push(`【${r.speaker || '話者未設定'}${r.expression ? '／'+r.expression : ''}】`, r.text);
        else if (r.type==='jump') lines.push('→ '+targetName(r.target));
        else if (r.type==='choice') {
          if (r.text) lines.push(r.text, '');
          r.options.forEach(o=>lines.push(`- ${o.text || '（未記入）'} → ${o.target ? targetName(o.target) : '次の行'}${o.condition ? '／条件：'+o.condition : ''}`));
        } else lines.push(r.text || (r.type==='end' ? '（この場面を終了）' : '（未記入）'));
        lines.push('');
      }
      if (e.notes) lines.push('## 制作メモ', '', e.notes, '');
      return lines.join('\n');
    }).join('\n---\n\n');
  }
  const api = {FORMAT,TYPES,NAMES,uid,copy,row,option,event,project,validate,inspect,beat,choose,markdown};
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.ScriptEditorModel = api;
})(globalThis);
