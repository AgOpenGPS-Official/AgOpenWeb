// The single authority-bearing socket carries both built-in controls and module RPC.
const pending = new Map();
window.addEventListener('assistant:rpc', ({ detail: reply }) => {
  const call = pending.get(reply.id); if (!call) return;
  pending.delete(reply.id); clearTimeout(call.timer);
  reply.ok ? call.resolve(reply.data) : call.reject(new Error(reply.error || 'Module failed'));
});
window.addEventListener('assistant:disconnected', () => {
  for (const call of pending.values()) { clearTimeout(call.timer); call.reject(new Error('Connection lost')); }
  pending.clear();
});
export function rpc(module, operation, args = {}, timeoutMs = 240000) {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('Request timed out')); }, timeoutMs);
    pending.set(id, { resolve, reject, timer });
    if (!window.FieldAssistantTransport.send('assistant.rpc|' + JSON.stringify({ id, module, operation, args }))) {
      pending.delete(id); clearTimeout(timer); reject(new Error('Host is not connected'));
    }
  });
}
export function el(tag, options = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(options)) {
    if (key === 'text') node.textContent = value ?? '';
    else if (key === 'class') node.className = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else if (key === 'dataset') Object.assign(node.dataset, value);
    else if (value != null) node.setAttribute(key, String(value));
  }
  for (const child of children.flat()) if (child != null) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  return node;
}
export const text = value => el('p', { text: value });
export const tr = (key,params={}) => window.FieldAssistantTransport.translate(key,params);
export const heading = value => el('h2', { text: value });
export function button(label, action, primary = false, disabled = () => false) {
  return el('button', { type: 'button', text: label, class: primary ? 'md-button md-primary' : 'md-button', onclick: async event => {
    const b = event.currentTarget; b.disabled = true;
    try { await action(); } catch (error) { notify(error.message, true); } finally { b.disabled = disabled(); }
  } });
}
export function notify(message,error=false){let node=document.getElementById('assistant-notice');if(!node){node=el('div',{id:'assistant-notice',role:'status'});document.body.append(node);}node.textContent=tr(message);node.hidden=false;clearTimeout(node.timer);node.timer=setTimeout(()=>node.hidden=true,error?6000:4000);}
