// WEB-COLOR-001: acquire the authoritative object through Diana, never sibling filesystem paths.
import {readFile,writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
export const source='2026-031-OneHistorySite/z-OneHistoryID/colors.json';
export function validate(tokens) {
  if(tokens.schemaVersion!==1 || tokens.id!=='onehistory.colors') throw new Error('Unsupported color schema');
  const color=value=>typeof value==='string' && (/^#[A-Fa-f0-9]{6}$/.test(value) || /^rgba\((?:\d{1,3},){3}(?:0|1|0?\.\d+)\)$/.test(value) && (()=>{const v=value.slice(5,-1).split(',').map(Number);return v.slice(0,3).every(x=>x<=255)&&v[3]<=1;})());
  const groups=[tokens.common,tokens.themes?.light,tokens.themes?.dark,tokens.desktop];
  for(const group of groups) if(!group || typeof group!=='object' || Array.isArray(group) || !Object.keys(group).length || Object.entries(group).some(([key,value])=>!/^[-a-z0-9]+$/.test(key)||!color(value))) throw new Error('Invalid color token');
  for(const key of ['accent','accent-soft','accent-wash']) if(!color(tokens.common[key])) throw new Error('Missing common color: '+key);
  const required=['canvas','surface','surface-2','surface-hover','text','text-2','ink','on-ink','hairline','hairline-2','code-bg','text-2-on-ink','pressed','danger','danger-soft','text-disabled','scroll-thumb','scroll-thumb-hover','selection-fill'];
  for(const theme of ['light','dark']) for(const key of required) if(!color(tokens.themes[theme][key])) throw new Error('Missing theme color: '+key);
  const desktop=['tile-base','tile-tint-target','tile-text','icon-text','icon-blue','icon-green','icon-red','icon-purple','icon-gold','icon-teal'];
  for(const key of desktop) if(!/^#[A-Fa-f0-9]{6}$/.test(tokens.desktop[key]||'')) throw new Error('Missing desktop color: '+key);
  return tokens;
}
export async function acquire(url,fetcher=fetch) {
  const response=await fetcher(new URL('/api/command',url),{method:'POST',headers:{'Content-Type':'application/json','X-Aurora-Web':'1'},body:JSON.stringify({text:`diana.z.read path=${source} encoding=text`}),signal:AbortSignal.timeout(15000)});
  if(!response.ok) throw new Error('Diana transport failed: HTTP '+response.status);
  const result=await response.json(),data=result.data;
  if(!result.success || data?.path!==source || data.encoding!=='text' || data.eof!==true || typeof data.content!=='string') throw new Error('Diana did not return the complete color object: '+(result.message||''));
  const sha256=createHash('sha256').update(data.content).digest('hex').toUpperCase();
  if(sha256!==data.sha256?.toUpperCase()) throw new Error('Color SHA-256 mismatch');
  return {source,sha256,tokens:validate(JSON.parse(data.content))};
}
export function cssFrom(snapshot) {
  const t=validate(snapshot.tokens);
  const declarations=group=>Object.entries(group).map(([key,value])=>'  --oh-color-'+key+':'+value+';').join('\n');
  return '/* Generated from '+snapshot.source+'\n   SHA-256: '+snapshot.sha256+'; do not edit. */\n:root{\n'+declarations(t.common)+'\n}\n:root,:root[data-theme=light]{\n'+declarations(t.themes.light)+'\n}\n:root[data-theme=dark]{\n'+declarations(t.themes.dark)+'\n}\n';
}
export async function sync({url='http://127.0.0.1:5280',check=false}={}) {
  const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
  const snapshot=await acquire(url);
  const outputs=[['b-Code-MercuryDock/Dock/colors.snapshot.json',JSON.stringify(snapshot,null,2)+'\n']];
  // Validate every output before writing; --check never changes the checkout.
  if(check) {
    for(const [relative,content] of outputs) if(await readFile(path.join(root,relative),'utf8')!==content) throw new Error('Stale color snapshot: '+relative);
  } else {
    for(const [relative,content] of outputs) await writeFile(path.join(root,relative),content,'utf8');
  }
  console.log(`${check?'Verified':'Consumed'} ${source} SHA-256 ${snapshot.sha256}`);
}
if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const option=process.argv.find(x=>x.startsWith('--url='));
  await sync({check:process.argv.includes('--check'),...(option?{url:option.slice(6)}:{})});
}