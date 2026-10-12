import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {mkdtemp,writeFile,mkdir,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const guid='1234567890abcdef1234567890abcdef';

for (const binaryResponse of [false,true]) {
  test(`CLI keeps the received result when local ${binaryResponse?'image':'journal'} persistence fails, without replay`,async t=>{
    const scratch=await mkdtemp(resolve(tmpdir(),'cs2-cli-persistence-'));
    let mutationCalls=0;
    const server=createServer((req,res)=>{
      const path=new URL(req.url,'http://localhost').pathname;
      if(path==='/state')return res.end(JSON.stringify({cityLoaded:true,isLoading:false,cityName:'Example City',loadedSaveGuid:guid,loadPurpose:'LoadGame'}));
      if(path==='/ping')return res.end(JSON.stringify({version:'0.10.3',ok:true}));
      mutationCalls++;
      if(binaryResponse){res.setHeader('Content-Type','image/png');return res.end(Buffer.from('isolated image witness'));}
      res.end(JSON.stringify({applied:true,marker:'native-result-witness'}));
    });
    await new Promise(r=>server.listen(0,'127.0.0.1',r));
    t.after(async()=>{server.closeAllConnections();server.close();await rm(scratch,{recursive:true,force:true});});
    const journal=resolve(scratch,'journal');await mkdir(journal);
    if(binaryResponse)await writeFile(resolve(journal,'游戏截图'),'blocks image directory');
    else await mkdir(resolve(journal,'操作记录.jsonl'));
    const context=resolve(scratch,'context.json');
    await writeFile(context,JSON.stringify({cityId:'example-city',cityName:'Example City',manualSavePath:'./example.cok',journalDirectory:'./journal',loadedSaveGuid:guid}));
    const args=resolve(scratch,'args.json');await writeFile(args,JSON.stringify(binaryResponse?{}:{speed:4}));
    const tool=binaryResponse?'cs2_screenshot':'cs2_set_simulation';
    const child=spawn(process.execPath,[resolve(root,'agent-control.mjs'),'--context',context,tool,args],
      {cwd:root,env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${server.address().port}`}});
    let stdout='',stderr='';child.stdout.on('data',c=>stdout+=c);child.stderr.on('data',c=>stderr+=c);
    const code=await new Promise((r,reject)=>{child.on('error',reject);child.on('close',r);});
    assert.equal(code,1,stderr);assert.equal(mutationCalls,1);
    const result=JSON.parse(stdout),failure=JSON.parse(result.content[0].text);
    assert.equal(result.isError,true);assert.equal(failure.failurePhase,'local_result_persistence');
    assert.equal(failure.toolResponseReceived,true);assert.equal(failure.mayHaveApplied,true);assert.equal(failure.safeToRetry,false);
    if(binaryResponse){
      const image=failure.toolResult.content[0];assert.equal(image.type,'image_metadata');assert.equal(image.mimeType,'image/png');
      assert.equal(image.data,undefined);assert.equal(image.byteLength,Buffer.byteLength('isolated image witness'));
      assert.equal(stdout.includes(Buffer.from('isolated image witness').toString('base64')),false);
    } else assert.deepEqual(JSON.parse(failure.toolResult.content[0].text),{applied:true,marker:'native-result-witness'});
  });
}
