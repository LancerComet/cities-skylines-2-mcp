import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('line model collections require corrected bridge and reject ambiguous choices before IO',async t=>{
  let version='0.10.2'; const requests=[];
  const mock=createServer((req,res)=>{ requests.push(req.url); res.setHeader('Content-Type','application/json'); res.end(JSON.stringify(req.url==='/ping'?{version}:{ok:true})); });
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));
  t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'line-model-test',version:'1'});
  t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));
  const entity={index:123,version:1};
  let r=await client.callTool({name:'cs2_line_vehicles',arguments:entity});
  assert.equal(r.isError,true); assert.deepEqual(requests,['/ping']);
  requests.length=0;
  const args={...entity,primaryModels:['TrainCargoCar01','TrainOilCar01'],secondaryModels:['EU_TrainCargoEngine01','NA_TrainCargoEngine01']};
  r=await client.callTool({name:'cs2_set_line_vehicles',arguments:args});
  assert.equal(r.isError,true); assert.deepEqual(requests,['/ping']);
  version='0.10.3'; requests.length=0;
  r=await client.callTool({name:'cs2_set_line_vehicles',arguments:args});
  assert.notEqual(r.isError,true);
  const url=new URL(requests.at(-1),'http://localhost');
  assert.equal(url.pathname,'/transit/lines/vehicles/set');
  assert.deepEqual(JSON.parse(url.searchParams.get('primaryModels')),args.primaryModels);
  assert.deepEqual(JSON.parse(url.searchParams.get('secondaryModels')),args.secondaryModels);
  for(const bad of [{...entity},{...entity,clear:true,primary:'Bus01'},{...entity,primary:'Bus01',primaryModels:['Bus02']},
      {...entity,primaryModels:['Bus01','bus01']},{...entity,primaryModels:Array(33).fill('Bus01')}]){
    requests.length=0; r=await client.callTool({name:'cs2_set_line_vehicles',arguments:bad});
    assert.equal(r.isError,true); assert.deepEqual(requests,[]);
  }
});
