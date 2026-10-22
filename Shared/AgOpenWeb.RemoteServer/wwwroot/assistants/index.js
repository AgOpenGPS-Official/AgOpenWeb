import {rpc,el} from './core.js';
export function install(){
  let installed=false;
  const boot=async()=>{if(installed||!window.FieldAssistantTransport.ready())return;
    const modules=await rpc('system','modules');installed=true;
    const bar=el('div',{id:'field-assistants'});document.body.append(bar);
    for(const info of modules){if(!/^[a-z]+$/.test(info.id))continue;const module=await import('./'+info.id+'.js');module.install(bar);}
  };
  window.addEventListener('assistant:connected',()=>boot().catch(error=>console.error(error)));boot().catch(error=>console.error(error));
}
