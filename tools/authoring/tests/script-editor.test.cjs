const {test} = require('node:test');
const assert = require('node:assert/strict');
const M = require('../web/model.js');
const C = require('../web/chart.js');
const R = require('../web/review.js');

function branchScript() {
  const choice=M.row('choice'), left=M.row('label'), right=M.row('label');
  left.text='手を貸す';right.text='様子を見る';
  const a=M.row('dialogue','趙活'),b=M.row('dialogue','霍月英');
  a.text='手を貸そう。\nそちらを持ってくれ。';b.text='少し待って。';
  choice.options[0].text='手を貸す';choice.options[0].target=left.id;
  choice.options[1].text='様子を見る';choice.options[1].target=right.id;
  return [choice,left,a,M.row('end'),right,b,M.row('end')];
}

test('JSON backup round-trips Unicode, multiline text, branches and notes',()=>{
  const e=M.event();e.rows=branchScript();e.notes='条件は未確定。';
  const p=M.project([e]);p.updatedAt='2026-10-25T00:00:00Z';
  assert.deepEqual(M.validate(JSON.parse(JSON.stringify(p))),p);
});
test('invalid file formats and partial malformed documents are rejected',()=>{
  assert.throws(()=>M.validate({format:'unrelated',version:1,events:[]}));
  const p=M.project([M.event()]);p.events[0].rows[0].text=42;
  assert.throws(()=>M.validate(p),/文字列/);
  const future=M.project();future.version=2;
  assert.throws(()=>M.validate(future),/形式1/);
});
test('duplicate IDs are rejected within a script, but allowed across copies',()=>{
  const e=M.event();const p=M.project([e]);e.rows.push(M.copy(e.rows[0]));
  assert.throws(()=>M.validate(p),/行ID/);
  e.rows.pop();const clone=M.copy(e);clone.uid=M.uid();
  assert.equal(M.validate(M.project([e,clone])).events.length,2);
});
test('choices reach the correct dialogue and end without falling through',()=>{
  const rows=branchScript();
  const left=M.choose(rows,0,rows[0].options[0]);
  assert.equal(left.row.speaker,'趙活');
  assert.equal(M.beat(rows,left.index+1).end,true);
  const right=M.choose(rows,0,rows[0].options[1]);
  assert.equal(right.row.speaker,'霍月英');
});
test('stable label IDs survive card reordering and renaming',()=>{
  const rows=branchScript();const label=rows[4];label.text='名を変えた場面';
  const rearranged=[rows[0],...rows.slice(4),...rows.slice(1,4)];
  assert.equal(M.choose(rearranged,0,rows[0].options[1]).row.speaker,'霍月英');
});
test('missing targets are reported and cannot restart at the first card',()=>{
  const e=M.event();e.rows=branchScript();e.rows.splice(1,1);
  assert.ok(M.inspect(e).some(x=>x.message.includes('分岐先')));
  assert.match(M.choose(e.rows,0,e.rows[0].options[0]).error,/見つかりません/);
});
test('unconditional jump loops stop while empty prose is skipped',()=>{
  const label=M.row('label'),jump=M.row('jump');jump.target=label.id;
  assert.match(M.beat([label,jump],0).error,/循環/);
  const prose=M.row('narration');prose.text='風が吹く。';
  assert.equal(M.beat([M.row('dialogue'),prose],0).row.id,prose.id);
});
test('Markdown export retains speakers, choice conditions and destinations',()=>{
  const e=M.event();e.rows=branchScript();e.rows[0].options[0].condition='好感度20以上';
  e.rows[2].expression='小声';e.notes='後で推敲する';
  const md=M.markdown([e]);
  for(const text of ['趙活／小声','そちらを持ってくれ。','条件：好感度20以上','→ 手を貸す','後で推敲する']) assert.ok(md.includes(text));
});

test('event chart follows choices to stable label IDs and keeps ends terminal',()=>{
  const rows=branchScript();rows[0].options[0].condition='好感度20以上';
  const original=M.copy(rows),graph=C.build(rows),byId=new Map(graph.nodes.map(n=>[n.id,n]));
  for(const option of rows[0].options){
    const node=graph.nodes.find(n=>n.optionId===option.id&&n.kind==='option');
    const edge=graph.edges.find(e=>e.from===node.id);
    assert.equal(byId.get(edge.to).rowIds[0],option.target);
    assert.equal(node.condition,option.condition);
  }
  for(const node of graph.nodes.filter(n=>n.kind==='end'))assert.equal(graph.edges.some(e=>e.from===node.id),false);
  assert.ok(graph.nodes.every(n=>n.reachable));
  assert.deepEqual(rows,original,'viewing the chart must never alter manuscript data');
});
test('event chart combines prose and preserves an implicit next-row branch and final fallthrough',()=>{
  const rows=[M.row('dialogue','趙活'),M.row('narration'),M.row('choice'),M.row('direction')];
  rows[0].text='続けよう。';rows[2].options[1].target='deleted';
  const graph=C.build(rows),first=graph.nodes.find(n=>n.kind==='body');
  assert.deepEqual(first.rowIds,[rows[0].id,rows[1].id]);
  const branch=graph.nodes.find(n=>n.optionId===rows[2].options[0].id);
  const edge=graph.edges.find(e=>e.from===branch.id);
  assert.equal(graph.nodes.find(n=>n.id===edge.to).rowIds[0],rows[3].id);
  assert.ok(graph.nodes.some(n=>n.kind==='finish'&&n.reachable));
  assert.ok(graph.nodes.some(n=>n.kind==='missing'&&n.optionId===rows[2].options[1].id));
});
test('event chart displays merges once, backward links and disconnected prose without looping',()=>{
  const start=M.row('label'),choice=M.row('choice'),a=M.row('label'),b=M.row('label'),join=M.row('label');
  start.text='入口';a.text='分岐A';b.text='分岐B';join.text='合流';
  choice.options[0].target=a.id;choice.options[1].target=b.id;
  const ja=M.row('jump'),jb=M.row('jump'),back=M.row('jump');ja.target=join.id;jb.target=join.id;back.target=start.id;
  const lost=M.row('dialogue','趙活');lost.text='先頭からはつながっていない。';
  const rows=[start,choice,a,ja,b,jb,join,back,lost];
  const graph=C.build(rows),target=graph.nodes.find(n=>n.rowIds[0]===join.id);
  assert.equal(graph.edges.filter(e=>e.to===target.id).length,2);
  assert.ok(graph.edges.some(e=>e.back));
  assert.equal(graph.nodes.find(n=>n.rowIds.includes(lost.id)).reachable,false);
  const diagram=C.layout(graph);
  assert.ok(diagram.width>0&&diagram.height>0);
  for(const n of diagram.nodes){assert.ok(Number.isFinite(n.x)&&Number.isFinite(n.y));assert.ok(n.x+n.width<=diagram.width);}
  for(const e of diagram.edges)assert.ok(e.path&&!e.path.includes('NaN'));
  for(let i=0;i<diagram.nodes.length;i++)for(let j=i+1;j<diagram.nodes.length;j++){
    const a=diagram.nodes[i],b=diagram.nodes[j];
    assert.ok(a.x+a.width<=b.x||b.x+b.width<=a.x||a.y+a.height<=b.y||b.y+b.height<=a.y,'node boxes must not overlap');
  }
});
test('event chart rejects non-label targets and retains warnings for empty choices',()=>{
  const jump=M.row('jump'),prose=M.row('dialogue'),choice=M.row('choice');
  jump.target=prose.id;choice.options=[];
  const graph=C.build([jump,prose,choice]);
  assert.equal(graph.nodes.filter(n=>n.kind==='missing').length,1);
  assert.ok(graph.nodes.find(n=>n.kind==='choice').warning);
  assert.ok(!graph.nodes.find(n=>n.kind==='body').reachable);
});
test('empty and long cyclic event charts have finite layouts',()=>{
  const empty=C.layout(C.build([]));assert.equal(empty.nodes.length,2);
  const rows=[];
  for(let i=0;i<1200;i++){const label=M.row('label'),jump=M.row('jump');label.text='場面'+i;jump.target=label.id;rows.push(label,jump);}
  const diagram=C.layout(C.build(rows));
  assert.equal(diagram.nodes.filter(n=>n.kind==='label').length,1200);
  assert.ok(Number.isFinite(diagram.height));
});

test('editor spelling checks share literal behavior with the server',()=>{
  for(const c of require('./review-rule-cases.json'))assert.equal(R.matches(c.text,c),c.expected,JSON.stringify(c));
});
test('editor checks only confirmed rules in scope and keeps reminders separate',()=>{
  const event=M.event();event.rows=[M.row('dialogue','霍月英'),M.row('dialogue','趙活'),M.row('choice')];
  event.rows[0].text=event.rows[1].text='古い表記';event.rows[2].options[0].text='古い表記';
  const rule={id:'test',kind:'literal',status:'active',scope:'speaker',speaker:'霍月英',eventUid:'',field:'text',match:'contains',bad:'古い表記',good:'新しい表記',reason:'表記の指摘',commentId:'source'};
  assert.equal(R.inspect(event,[rule]).issues.length,1);
  assert.equal(R.inspect(event,[{...rule,scope:'project'}]).issues.length,3);
  assert.equal(R.inspect(event,[{...rule,status:'proposed'}]).issues.length,0);
  assert.equal(R.inspect(event,[{...rule,scope:'event',eventUid:'other'}]).issues.length,0);
  const report=R.inspect(event,[{...rule,kind:'reminder'}]);
  assert.equal(report.issues.length,0);assert.equal(report.reminders.length,1);
});
