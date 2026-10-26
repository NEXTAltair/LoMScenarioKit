/* Derive a read-only control-flow graph from the manuscript; no saved layout or CDN. */
(function (root) {
  'use strict';
  const PROSE = new Set(['dialogue', 'narration', 'direction']);
  const KINDS = {start:'開始',body:'本文',label:'場面目印',choice:'選択肢',option:'選択',jump:'移動',end:'終了',finish:'末尾',missing:'要確認'};
  const excerpt = row => (row.type==='dialogue' ? (row.speaker||'話者未記入')+'：' : '')+(row.text||'（本文未記入）');

  function build(rows) {
    const nodes=[],edges=[],byRow=new Map(),labels=new Map(),blocks=[];
    const add = (kind,order,fields={}) => {
      const node={id:'node-'+nodes.length,kind,order,rowIds:[],title:'',text:'',condition:'',...fields};
      nodes.push(node);return node;
    };
    const start=add('start',-1,{title:'イベント開始'});
    for(let i=0;i<rows.length;){
      const row=rows[i],from=i;
      let node;
      if(PROSE.has(row.type)){
        const group=[];
        while(i<rows.length&&PROSE.has(rows[i].type))group.push(rows[i++]);
        const speakers=[...new Set(group.filter(r=>r.type==='dialogue').map(r=>r.speaker||'話者未記入'))];
        node=add('body',from*1024,{title:speakers.join('・')||'地の文・演出',text:group.map(excerpt).join('\n'),rowIds:group.map(r=>r.id)});
      }else{
        i++;
        node=add(row.type,from*1024,{title:row.type==='label'?(row.text||'名前未記入の場面'):KINDS[row.type],text:row.type==='label'?'':row.text,rowIds:[row.id]});
        if(row.type==='label')labels.set(row.id,node);
        if(row.type==='choice'&&!row.options.length)node.warning='選択肢がありません';
      }
      node.from=from;node.to=i-1;
      for(const id of node.rowIds)byRow.set(id,node);
      blocks.push(node);
    }
    let finish;
    const ending=()=>finish||(finish=add('finish',rows.length*1024,{title:'台本の末尾'}));
    const connect=(a,b,kind='next')=>edges.push({id:'edge-'+edges.length,from:a.id,to:b.id,kind,back:b.order<=a.order});
    connect(start,blocks[0]||ending());
    for(let i=0;i<blocks.length;i++){
      const node=blocks[i],row=rows[node.from];
      const next=()=>blocks[i+1]||ending();
      const destination=(id,source,offset)=>{
        if(labels.has(id))return labels.get(id);
        return add('missing',node.order+offset,{title:id?'分岐先が見つかりません':'移動先が未設定',
          text:'クリックして移動先の場面目印を選び直してください。',warning:'移動先を確認',
          rowIds:[row.id],optionId:source?.id||'',from:node.from,to:node.to});
      };
      if(node.kind==='end')continue;
      if(node.kind==='jump'){
        const target=destination(row.target,null,201);
        node.text=target.kind==='missing'?target.title:'→ '+target.title;
        connect(node,target,'jump');
      }else if(node.kind==='choice'){
        row.options.forEach((option,j)=>{
          const branch=add('option',node.order+j+1,{title:option.text||'選択肢の文言が未記入',text:'',condition:option.condition,
            rowIds:[row.id],optionId:option.id,from:node.from,to:node.to,number:j+1});
          const target=option.target?destination(option.target,option,201+j):next();
          branch.text=option.target?'→ '+target.title:'→ 次の行へ';
          connect(node,branch,'choice');connect(branch,target,'choice');
        });
      }else connect(node,next());
    }
    const byId=new Map(nodes.map(n=>[n.id,n])),outgoing=new Map(nodes.map(n=>[n.id,[]]));
    for(const edge of edges)outgoing.get(edge.from).push(edge.to);
    const pending=[start.id],visited=new Set();
    while(pending.length){
      const id=pending.pop();if(visited.has(id))continue;
      visited.add(id);pending.push(...outgoing.get(id));
    }
    for(const node of nodes)node.reachable=visited.has(node.id);
    for(const edge of edges)edge.reachable=byId.get(edge.from).reachable;
    return {nodes,edges};
  }

  function layout(graph) {
    // Document order breaks backward links without recursing through loops.
    const nodes=graph.nodes.map(n=>({...n,rank:0})).sort((a,b)=>a.order-b.order);
    const byId=new Map(nodes.map(n=>[n.id,n])),incoming=new Map(nodes.map(n=>[n.id,[]])),outgoing=new Map(nodes.map(n=>[n.id,[]]));
    for(const edge of graph.edges){
      if(!edge.back){incoming.get(edge.to).push(byId.get(edge.from));outgoing.get(edge.from).push(byId.get(edge.to));}
    }
    const layers=[];
    for(const node of nodes){
      if(node.kind!=='start')node.rank=Math.max(1,node.rank);
      (layers[node.rank]||=[]).push(node);
      for(const child of outgoing.get(node.id))child.rank=Math.max(child.rank,node.rank+1);
    }
    const width=224,gap=38,maxColumns=Math.max(1,...layers.map(l=>l?.length||0));
    const backCount=graph.edges.filter(e=>e.back).length;
    const skipCount=graph.edges.filter(e=>!e.back&&byId.get(e.to).rank>byId.get(e.from).rank+1).length;
    const left=36+Math.min(backCount,10)*12,right=36+Math.min(skipCount,10)*12;
    const contentWidth=maxColumns*width+(maxColumns-1)*gap;
    let y=28;
    for(const layer of layers){
      if(!layer)continue;
      const center=n=>{const parents=incoming.get(n.id);return parents.length?parents.reduce((sum,p)=>sum+(p.x||0)+width/2,0)/parents.length:Infinity;};
      layer.sort((a,b)=>center(a)-center(b)||a.order-b.order);
      const offset=left+(contentWidth-(layer.length*width+(layer.length-1)*gap))/2;
      for(const [i,node] of layer.entries()){
        node.x=offset+i*(width+gap);node.y=y;node.width=width;
        node.height=['start','finish'].includes(node.kind)?48:node.kind==='option'?156:node.kind==='label'?100:124;
      }
      const bottom=y+Math.max(...layer.map(n=>n.height));
      layer.forEach(n=>n.layerBottom=bottom);
      y=bottom+72;
    }
    let backLane=0,skipLane=0;
    const edges=graph.edges.map(edge=>{
      const a=byId.get(edge.from),b=byId.get(edge.to);
      const ax=a.x+a.width/2,ay=a.y+a.height,bx=b.x+b.width/2,by=b.y;
      let path;
      if(edge.back){
        const lane=18+(backLane++%10)*12;
        path=`M ${ax} ${ay} V ${a.layerBottom+24} H ${lane} V ${by-24} H ${bx} V ${by-5}`;
      }else if(b.rank>a.rank+1){
        const lane=left+contentWidth+18+(skipLane++%10)*12;
        path=`M ${ax} ${ay} V ${a.layerBottom+26} H ${lane} V ${by-26} H ${bx} V ${by-5}`;
      }else{
        path=`M ${ax} ${ay} C ${ax} ${ay+36}, ${bx} ${by-36}, ${bx} ${by-5}`;
      }
      return {...edge,path};
    });
    return {nodes,edges,width:left+contentWidth+right,height:y-44};
  }

  function create({host,active,onNavigate,selectedRow}) {
    const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const $=id=>host.querySelector('#'+id);
    let diagram=null,scale=1,eventId='',needsRender=true,timer,autoFit='width';
    host.innerHTML=`<div class="chart-heading"><div><h2>イベント内チャート</h2><p id="chart-summary" class="hint"></p></div>
      <div class="chart-controls"><button id="chart-zoom-out" aria-label="チャートを縮小">−</button><output id="chart-scale" aria-live="polite">100%</output><button id="chart-zoom-in" aria-label="チャートを拡大">＋</button><button id="chart-fit">幅に合わせる</button><button id="chart-overview">全体を表示</button></div></div>
      <p class="chart-guide">箱を押すと、その行を編集できます。連続する台詞・地の文・演出はまとめて表示します。</p>
      <div class="chart-legend"><span>実線：進行</span><span class="return-key">破線：前の行へ</span><span class="missing-key">赤：移動先の不備</span><span>淡色：先頭から未接続</span></div>
      <div id="chart-viewport" class="chart-viewport" tabindex="0" aria-label="イベント内の分岐チャート。矢印キーでスクロールできます。"><div id="chart-stage"><div id="chart-canvas"></div></div></div>
      <p class="hint chart-footnote">選択肢の条件はメモとして表示します。条件の成否は判定しません。</p>`;
    function zoom(value){
      scale=Math.min(1.5,Math.max(.01,value));
      $('chart-canvas').style.transform=`scale(${scale})`;
      $('chart-stage').style.width=(diagram?.width||0)*scale+'px';$('chart-stage').style.height=(diagram?.height||0)*scale+'px';
      $('chart-scale').textContent=Math.round(scale*100)+'%';
      $('chart-zoom-out').disabled=!diagram||scale<=.01;$('chart-zoom-in').disabled=!diagram||scale>=1.5;$('chart-fit').disabled=!diagram;$('chart-overview').disabled=!diagram;
    }
    function fit(mode=autoFit||'width'){
      if(!diagram)return;
      autoFit=mode;
      const viewport=$('chart-viewport');
      zoom(Math.min(1,(viewport.clientWidth-24)/diagram.width,mode==='all'?(viewport.clientHeight-24)/diagram.height:1));
      if(mode==='all'){viewport.scrollTop=0;viewport.scrollLeft=0;}
    }
    function render(){
      clearTimeout(timer);if(host.hidden)return;
      if(!needsRender){mark();return;}
      needsRender=false;
      const event=active(),changedEvent=eventId!==(event?.uid||'');eventId=event?.uid||'';
      if(!event||!event.rows.length){
        diagram=null;$('chart-canvas').innerHTML='';zoom(1);
        $('chart-summary').textContent=event?'行カードを追加すると、ここに流れを表示します。':'台本を選ぶと、そのイベントの流れを表示します。';return;
      }
      diagram=layout(build(event.rows));
      const counts=diagram.nodes.reduce((sum,n)=>{sum[n.kind]=(sum[n.kind]||0)+1;return sum;},{});
      const unreachable=diagram.nodes.filter(n=>!n.reachable&&!['option','missing','finish'].includes(n.kind)).length;
      $('chart-summary').textContent=`${event.rows.length}行 · 選択肢 ${counts.choice||0}か所 · 場面目印 ${counts.label||0}か所`+
        (counts.missing?` · 移動先の不備 ${counts.missing}件`:'')+(unreachable?` · 未接続 ${unreachable}ブロック`:'');
      const {width,height}=diagram;
      $('chart-canvas').style.width=width+'px';$('chart-canvas').style.height=height+'px';
      $('chart-canvas').innerHTML=`<svg class="chart-lines" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" aria-hidden="true"><defs><marker id="chart-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#819b8d"/></marker></defs>${diagram.edges.map(e=>`<path class="chart-edge ${e.back?'back':''} ${e.reachable?'':'unreachable'}" d="${e.path}" marker-end="url(#chart-arrow)"/>`).join('')}</svg>`+
        diagram.nodes.map(n=>{
          const range=n.from===undefined?'':n.from===n.to?`${n.from+1}行目`:`${n.from+1}〜${n.to+1}行目`;
          const caption=(n.number?`選択 ${n.number}`:KINDS[n.kind])+(range?' · '+range:'');
          const text=[caption,n.title,n.condition?'条件：'+n.condition:'',n.text,n.warning||'',!n.reachable?'先頭から未接続':''].filter(Boolean).join('\n');
          const tag=n.rowIds.length?'button':'div';
          return `<${tag} class="chart-node ${n.kind} ${n.reachable?'':'unreachable'} ${n.warning?'warning':''}" data-chart-node="${n.id}" style="left:${n.x}px;top:${n.y}px;width:${n.width}px;height:${n.height}px" title="${esc(text)}" ${tag==='button'?`aria-label="${esc(text+'。行カードを編集する')}"`:''}>
            ${n.kind==='start'||n.kind==='finish'?`<strong>${esc(n.title)}</strong>`:`<span class="chart-node-type">${esc(caption)}${!n.reachable?' · 未接続':''}</span><strong>${esc(n.title)}</strong>${n.condition?`<span class="chart-condition">条件：${esc(n.condition)}</span>`:''}${n.text?`<span class="chart-excerpt">${esc(n.text)}</span>`:''}${n.warning?`<span class="chart-node-warning">${esc(n.warning)}</span>`:''}`}</${tag}>`;
        }).join('');
      if(changedEvent){autoFit='width';$('chart-viewport').scrollTop=0;$('chart-viewport').scrollLeft=0;}
      if(autoFit)fit();else zoom(scale);
      mark();
    }
    function mark(){
      const selected=selectedRow();
      const byId=new Map((diagram?.nodes||[]).map(n=>[n.id,n]));
      host.querySelectorAll('[data-chart-node]').forEach(el=>{
        const node=byId.get(el.dataset.chartNode);
        el.classList.toggle('selected',!!selected&&!!node?.rowIds.includes(selected)&&node.kind!=='option'&&node.kind!=='missing');
      });
    }
    function invalidate(){needsRender=true;clearTimeout(timer);if(!host.hidden)timer=setTimeout(render,180);}
    $('chart-zoom-out').onclick=()=>{autoFit=false;zoom(scale-.15);};
    $('chart-zoom-in').onclick=()=>{autoFit=false;zoom(scale+.15);};
    $('chart-fit').onclick=()=>fit('width');$('chart-overview').onclick=()=>fit('all');
    $('chart-canvas').onclick=ev=>{
      const el=ev.target.closest('button[data-chart-node]');if(!el)return;
      const node=diagram.nodes.find(n=>n.id===el.dataset.chartNode);
      onNavigate(node.rowIds[0],node.optionId||'');
    };
    new ResizeObserver(()=>{if(!host.hidden&&autoFit)fit();}).observe($('chart-viewport'));
    return {invalidate,render};
  }
  const api={build,layout,create};
  if(typeof module!=='undefined'&&module.exports)module.exports=api;
  else root.ScriptEditorChart=api;
})(globalThis);
