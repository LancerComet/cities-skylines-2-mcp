import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { assertBridgeCompatibility } from '../dist/bridge-compatibility.js';

test('current control contract is independent of the normalized product release', () => {
  for (const version of ['0.9.1', '0.9.1-local'])
    assert.doesNotThrow(() => assertBridgeCompatibility({version, controlApiVersion:1}, 10007, 'Hub rebuild'));
  for (const version of ['0.9.0','0.9.1','unknown'])
    assert.throws(() => assertBridgeCompatibility({version}, 10007, 'Hub rebuild'), /control API 1/);
});

test('unknown control contracts cannot fall back to a large product version', () => {
  for (const controlApiVersion of [0,2,'1',true,null])
    assert.throws(() => assertBridgeCompatibility({version:'999.0.0',controlApiVersion}, 9007, 'native control'), /No operation was submitted/);
});

test('historical bridges retain the original minimum and suffix compatibility', () => {
  assert.doesNotThrow(() => assertBridgeCompatibility({version:'0.9.119-local'}, 9118, 'Hub rebuild'));
  assert.doesNotThrow(() => assertBridgeCompatibility({version:'0.10.7'}, 10007, 'Hub rebuild'));
  assert.throws(() => assertBridgeCompatibility({version:'0.9.117-local'}, 9118, 'Hub rebuild'));
  assert.throws(() => assertBridgeCompatibility({version:'0.10.6'}, 10007, 'Hub rebuild'));
});

test('a normalized bridge accepts native Hub preview only with the explicit control contract', async t => {
  let controlApiVersion=1;
  const requests=[];
  const mock=createServer((req,res)=>{
    requests.push({url:req.url,method:req.method});
    res.end(JSON.stringify(req.url==='/ping'?{version:'0.9.1',controlApiVersion}:{applied:false}));
  });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));
  t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'normalized-version-contract',version:'1'});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));
  t.after(()=>client.close());
  const args={index:123,version:4,parent:{index:45,version:6},expectedLoadedSaveGuid:'b'.repeat(32)};
  const call=()=>client.callTool({name:'cs2_rebuild_industry_hub',arguments:args});
  assert.notEqual((await call()).isError,true);
  assert.equal(requests.length,2);
  assert.equal(requests[1].method,'POST');
  assert.equal(new URL(requests[1].url,'http://localhost').searchParams.get('dryRun'),'true');
  for (const advertised of [undefined,2]) {
    controlApiVersion=advertised; requests.length=0;
    assert.equal((await call()).isError,true);
    assert.deepEqual(requests,[{url:'/ping',method:'GET'}]);
  }
});

