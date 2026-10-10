import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

test('facility parking rejects old bridges before sending writes and uses native policy endpoint',async t=>{
  let version='0.4.0'; const requests=[];
  const mock=createServer((req,res)=>{requests.push(req.url);res.setHeader('content-type','application/json');res.end(JSON.stringify(req.url==='/ping'?{version}:{ok:true}));});
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));
  t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'fee-test',version:'1'});
  const transport=new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'});
  t.after(()=>client.close());await client.connect(transport);
  const args={index:123,version:1,fee:50};
  let result=await client.callTool({name:'cs2_set_facility_parking_fee',arguments:args});
  assert.equal(result.isError,true);assert.deepEqual(requests,['/ping']);
  version='0.10.1';requests.length=0;
  result=await client.callTool({name:'cs2_set_facility_parking_fee',arguments:args});
  assert.notEqual(result.isError,true);
  const url=new URL(requests.at(-1),'http://localhost');assert.equal(url.pathname,'/building/parking-fee/set');
  assert.equal(url.searchParams.get('fee'),'50');assert.equal(url.searchParams.get('index'),'123');assert.equal(url.searchParams.has('force'),false);
  requests.length=0;
  result=await client.callTool({name:'cs2_facility_parking_fees',arguments:{}});assert.notEqual(result.isError,true);
  assert.equal(requests.at(-1),'/city/facility-parking');
});
