import {rpc,el,text,button,tr,notify} from './core.js';
import {setLayer,layerEpoch} from './row-map.js';

// A/B records the host's canonical pivot. Closing this panel never stops a recording.
export function installSavedRows() {
  const panel=document.getElementById('savedrows'),root=document.getElementById('savedrows-content');
  const shortcut=document.getElementById('rn-savedrows');
  const list=el('select',{id:'savedrows-list',size:'4','aria-label':'Choose a saved row'});
  const status=el('p',{id:'savedrows-status',role:'status'});
  const automatic=el('input',{type:'checkbox'});
  const capture=el('input',{type:'number',min:'10',max:'200',step:'1',value:'50','aria-label':'Row capture distance (cm)'});
  let state,revision=-1,recording=[],paths=[],listKey='',mapKey='',busy=false,optionsDirty=false,generation=0;
  const command=async(operation,args={})=>{
    generation++;const result=await rpc('savedrows',operation,args);apply(result);return result;
  };
  const begin=button('A · start row',()=>command('begin'));
  const finish=button('B · save row',async()=>{await command('finish');notify('Row saved in the open field');},true);
  const cancel=button('Cancel recording',()=>command('cancel'));
  const mode=button('Follow saved rows',()=>command('mode',{enabled:!state?.enabled}),true);
  const save=button('Save options',async()=>{
    if(!capture.checkValidity()){capture.reportValidity();return;}
    await command('options',{autoSelect:automatic.checked,captureCm:Number(capture.value)});optionsDirty=false;
  });
  root.append(text('Each row has its own recorded path'),
    el('div',{class:'savedrows-record'},begin,finish,cancel),list,
    el('div',{class:'savedrows-options'},el('label',{},automatic,el('span',{text:'Automatic row selection'})),
      el('label',{},el('span',{text:'Capture (cm)'}),capture),save),mode,status);
  root.firstChild.className='savedrows-intro';
  list.addEventListener('change',()=>command('select',{id:list.value}).catch(error=>notify(error.message,true)));
  automatic.addEventListener('change',()=>optionsDirty=true);capture.addEventListener('input',()=>optionsDirty=true);
  const apply=result=>{
    state=result;document.body.toggleAttribute('data-savedrows-mode',result.enabled);
    const rows=result.rows||[],newKey=JSON.stringify(rows);
    if(newKey!==listKey){listKey=newKey;list.replaceChildren(...rows.map(row=>el('option',{value:row.id,translate:'no',
      text:(/^Row \d+$/.test(row.name)?tr('Row {n}',{n:row.name.slice(4)}):row.name)+(row.simulator?' · '+tr('Simulator'):'')+
        ' · '+Math.round(row.lengthM).toLocaleString(window.FieldAssistantTransport.language())+' m'})));
      list.size=Math.min(5,Math.max(2,rows.length));list.style.height=(list.size*38+4)+'px';}
    list.value=rows[result.selected]?.id||'';
    if(!optionsDirty){automatic.checked=result.autoSelect;capture.value=result.captureCm;}
    const control=window.FieldAssistantTransport.hasControl(),ready=control&&result.fieldOpen;
    begin.disabled=!ready||result.recording;finish.disabled=!ready||!result.recording;cancel.disabled=!ready||!result.recording;
    mode.disabled=!ready||!rows.length;save.disabled=!ready;list.disabled=!ready||!rows.length;
    automatic.disabled=capture.disabled=!ready;
    mode.textContent=tr(result.enabled?'Saved row guidance ON':'Follow saved rows');
    status.textContent=result.recording?tr('Recording: {count} points · B saves the row',{count:result.recordedCount}):tr(result.status);
    shortcut.classList.toggle('md-active',result.enabled);shortcut.classList.toggle('md-recording',result.recording);
    shortcut.setAttribute('aria-pressed',String(result.enabled));
    if(result.paths){paths=result.paths;revision=result.revision;}
    if(result.recordedCount<recording.length||!result.recording)recording=[];
    recording.push(...(result.recordingPoints||[]));
    // Keep map data in field scope. Epoch guards against a field switch during RPC.
    const key=[revision,recording.length,result.selected].join('|');
    if(key!==mapKey){mapKey=key;if(paths.length||recording.length)setLayer('savedrows',{paths,recording:[...recording],selected:result.selected});else setLayer('savedrows',null);}
  };
  window.addEventListener('assistant:fieldchanged',()=>{revision=-1;paths=[];recording=[];listKey='';mapKey='';optionsDirty=false;generation++;state=null;document.body.removeAttribute('data-savedrows-mode');shortcut.setAttribute('aria-pressed','false');shortcut.classList.remove('md-recording');for(const control of [begin,finish,cancel,mode,save,list,automatic,capture])control.disabled=true;});
  window.addEventListener('assistant:languagechanged',()=>{listKey='';if(state)apply({...state,recordingPoints:[]});});
  setInterval(async()=>{
    if(busy||document.hidden||!window.FieldAssistantTransport.ready())return;
    busy=true;const epoch=layerEpoch(),version=generation;
    try{const result=await rpc('savedrows','state',{revision,recordedCount:recording.length},5000);if(epoch===layerEpoch()&&version===generation)apply(result);}
    catch(error){for(const control of [begin,finish,cancel,mode,save,list,automatic,capture])control.disabled=true;if(panel.classList.contains('open'))status.textContent=tr(error.message);}
    finally{busy=false;}
  },500);
}

export function install(bar){
  document.head.append(el('link',{rel:'stylesheet',href:'/assistants/row-style.css'}));
  const root=el('div',{id:'savedrows-content'});
  const panel=el('section',{id:'savedrows',class:'assistant-panel',hidden:'','aria-label':'Individual rows'},el('h2',{text:'Individual rows'}),button('Close',()=>{panel.hidden=true;panel.classList.remove('open');}),root);
  document.body.append(panel);
  const shortcut=button('Individual rows',()=>{panel.hidden=!panel.hidden;panel.classList.toggle('open',!panel.hidden);});shortcut.id='rn-savedrows';bar.append(shortcut);installSavedRows();
}
