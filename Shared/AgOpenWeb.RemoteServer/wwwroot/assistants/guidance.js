import { rpc,el,text,button,tr,notify } from './core.js';

// Only the host analyzes native coverage. Browser scheduling can offer a proposal;
// an explicit operator click and a fresh host validation are required to apply it.
function resultView(result) {
  const node=el('div',{class:'gapless-result'});
  if(!result.success){node.append(text(tr(result.message)));return node;}
  node.append(text(result.alreadyAligned?'No missed strip detected on this AB line':'A shift towards the mapped strip is proposed. Review before applying.'));
  if(!result.alreadyAligned)node.append(el('strong',{text:tr('AB shift: {cm} cm {direction}',{
    cm:Math.abs(result.nudgeCommandM*100).toLocaleString(window.FieldAssistantTransport.language(),{maximumFractionDigits:1}),
    direction:tr(result.nudgeCommandM>=0?'right':'left')})}));
  if(result.analyzedLengthM>0)node.append(text(tr('Analyzed edge: {m} m · overlap: {min}–{max} cm',{
    m:Math.round(result.analyzedLengthM),min:Math.round(result.minimumOverlapM*100),max:Math.round(result.maximumOverlapM*100)})));
  return node;
}
async function save(proposal,target) {
  await rpc('guidance','apply',{id:proposal.id,target});
  notify(tr(target==='base'?'Base line shift saved.':'Guiding line shift saved.'));
}
function choices(action,disabled) {
  return el('div',{class:'md-actions'},
    button('Save base line shift',()=>action('base'),true,disabled),
    button('Save guiding line shift',()=>action('guiding'),true,disabled));
}
const explanation=()=>text('Base: move saved AB points. Guiding: keep AB points and save the current pass correction.');
export async function renderGaplessAssistant(root) {
  root.append(el('h2',{text:'Missed-strip assistant'}),text('Select a straight AB line, disengage steering and use a field with a saved boundary and mapped coverage.'));
  const output=el('div',{role:'status'});let proposal;
  const targets=choices(async target=>{
    try{await save(proposal,target);output.replaceChildren(text('Shift saved for this field.'));}
    catch(error){output.replaceChildren(text(tr(error.message)));throw error;}
    finally{proposal=null;for(const b of targets.querySelectorAll('button'))b.disabled=true;}
  },()=>!proposal);
  for(const b of targets.querySelectorAll('button'))b.disabled=true;
  root.append(button('Analyze selected AB',async()=>{
    proposal=null;for(const b of targets.querySelectorAll('button'))b.disabled=true;output.replaceChildren(text('Analyzing mapped coverage…'));
    try{const candidate=await rpc('guidance','analyze');output.replaceChildren(resultView(candidate.result));
      if(candidate.result.success&&!candidate.result.alreadyAligned){proposal=candidate;for(const b of targets.querySelectorAll('button'))b.disabled=false;}}
    catch(error){output.replaceChildren(text(tr(error.message)));throw error;}
  }),output,explanation(),targets,text('The assistant also offers a shift automatically after the selected AB line stays stable. It never applies a shift automatically.'));
}
export function installGaplessAssistant() {
  let key='',dismissed='',stable=0,last=0,busy=false,proposal;
  const output=el('div');
  const card=el('section',{id:'gapless-assistant',hidden:'',role:'dialog','aria-label':'Missed-strip assistant'},
    el('div',{class:'ln-hdr'},el('strong',{text:'Missed-strip assistant'})),output);
  const close=async()=>{
    const id=proposal?.id;dismissed=key;proposal=null;card.hidden=true;
    if(id){await rpc('guidance','dismiss',{id}).catch(()=>{});}
  };
  const targets=choices(async target=>{await save(proposal,target);await close();},()=>!proposal);
  card.append(explanation(),targets,button('Dismiss proposal',close));document.body.append(card);
  window.addEventListener('assistant:disconnected',()=>{proposal=null;card.hidden=true;});
  for(const type of ['pointerdown','pointerup','pointermove','wheel','keydown'])card.addEventListener(type,event=>{if(type==='keydown'&&event.key==='Escape')close();event.stopPropagation();});
  setInterval(async()=>{
    if(busy)return;
    if(document.hidden||!window.FieldAssistantTransport.ready()||!window.FieldAssistantTransport.hasControl()) {stable=0;if(proposal)await close();return;}
    const blocked=document.querySelector('.ln-panel.open,#dialoghost.open,.assistant-panel:not([hidden])');
    busy=true;
    try {
      const current=await rpc('guidance','context',{},5000);
      if(!current.ready||current.key!==key){await close();key=current.ready?current.key:'';dismissed='';stable=0;last=0;return;}
      if(blocked){stable=0;if(!card.hidden)await close();return;}
      if(!key||dismissed===key||!card.hidden||++stable<3||Date.now()-last<6000)return;
      last=Date.now();const candidate=await rpc('guidance','analyze');
      const fresh=await rpc('guidance','context',{},5000);
      if(document.hidden||!fresh.ready||fresh.key!==key||document.querySelector('.ln-panel.open,#dialoghost.open,.assistant-panel:not([hidden])'))return;
      if(!candidate.result.success)return;
      if(candidate.result.alreadyAligned||Math.abs(candidate.result.nudgeCommandM)<.03){dismissed=key;return;}
      proposal=candidate;targets.hidden=false;
      output.replaceChildren(resultView(candidate.result));for(const b of targets.querySelectorAll('button'))b.disabled=false;card.hidden=false;
    }catch {await close();}finally{busy=false;}
  },1500);
}

export function install(bar){
  const panel=el('section',{class:'assistant-panel',hidden:'','aria-label':'Missed-strip assistant'});
  panel.append(button('Close',()=>panel.hidden=true));renderGaplessAssistant(panel);document.body.append(panel);
  bar.append(button('Missed-strip assistant',()=>panel.hidden=!panel.hidden));installGaplessAssistant();
}
