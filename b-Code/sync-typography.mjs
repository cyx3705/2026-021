// WEB-FONT-002: acquire the authoritative object through Diana, never sibling filesystem paths.
import {readFile,writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
export const source='2026-031-OneHistorySite/z-OneHistoryID/typography.json';
export function validate(tokens) {
  if(tokens.schemaVersion!==1 || tokens.id!=='onehistory.typography') throw new Error('Unsupported typography schema');
  for(const key of ['serif','numerals','mono','desktopSerif']) {
    const names=tokens.families?.[key];
    if(!Array.isArray(names) || !names.length || names.some(x=>typeof x!=='string' || !/^[A-Za-z0-9 -]+$/.test(x))) throw new Error('Invalid font family: '+key);
  }
  for(const key of ['body','heading']) if(!Number.isInteger(tokens.weights?.[key]) || tokens.weights[key]<100 || tokens.weights[key]>900) throw new Error('Invalid weight: '+key);
  return tokens;
}
export async function acquire(url,fetcher=fetch) {
  const response=await fetcher(new URL('/api/command',url),{method:'POST',headers:{'Content-Type':'application/json','X-Aurora-Web':'1'},body:JSON.stringify({text:`diana.z.read path=${source} encoding=text`}),signal:AbortSignal.timeout(15000)});
  if(!response.ok) throw new Error('Diana transport failed: HTTP '+response.status);
  const result=await response.json(),data=result.data;
  if(!result.success || data?.path!==source || data.encoding!=='text' || data.eof!==true || typeof data.content!=='string') throw new Error('Diana did not return the complete typography object: '+(result.message||''));
  const sha256=createHash('sha256').update(data.content).digest('hex').toUpperCase();
  if(sha256!==data.sha256?.toUpperCase()) throw new Error('Typography SHA-256 mismatch');
  return {source,sha256,tokens:validate(JSON.parse(data.content))};
}
export function cssFrom(snapshot) {
  const t=snapshot.tokens;
  const stack=key=>t.families[key].map(name=>['serif','monospace'].includes(name)?name:JSON.stringify(name)).join(',');
  return `/* Generated from ${snapshot.source}\n   SHA-256: ${snapshot.sha256}; do not edit. */\n:root{\n  --oh-font-family-serif:${stack('serif')};\n  --oh-font-family-numerals:${stack('numerals')};\n  --oh-font-family-mono:${stack('mono')};\n  --oh-font-weight-body:${t.weights.body};\n  --oh-font-weight-heading:${t.weights.heading};\n}\n`;
}
export async function sync({url='http://127.0.0.1:5280',check=false}={}) {
  const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
  const snapshot=await acquire(url);
  const outputs=[['b-Code-MercuryDock/Dock/typography.snapshot.json',JSON.stringify(snapshot,null,2)+'\n']];
  // Validate every output before writing; --check never changes the checkout.
  if(check) {
    for(const [relative,content] of outputs) if(await readFile(path.join(root,relative),'utf8')!==content) throw new Error('Stale typography snapshot: '+relative);
  } else {
    for(const [relative,content] of outputs) await writeFile(path.join(root,relative),content,'utf8');
  }
  console.log(`${check?'Verified':'Consumed'} ${source} SHA-256 ${snapshot.sha256}`);
}
if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const option=process.argv.find(x=>x.startsWith('--url='));
  await sync({check:process.argv.includes('--check'),...(option?{url:option.slice(6)}:{})});
}