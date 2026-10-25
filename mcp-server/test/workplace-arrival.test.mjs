import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname, resolve} from 'node:path';

test('员工分页只读：旧桥接拒绝新参数、旧调用兼容，未知/失效员工与原生到岗状态原样返回', async t => {
  const requests = [];
  let version = '0.10.4';
  let roster = {available:true,count:4,offset:1,returned:2,complete:false,nextOffset:3,
    employees:[{employeeIndex:1,citizen:{index:33,version:1},liveCitizen:false,citizenState:null,atEmployerBuilding:null},
      {employeeIndex:2,citizen:{index:34,version:1},liveCitizen:true,atEmployerBuilding:false,
        citizenState:{flags:'Commuter',work:{lastCommuteTimeRaw:128}}}]};
  const server = createServer((req,res) => {
    const url = new URL(req.url,'http://127.0.0.1');
    requests.push({method:req.method,path:url.pathname,params:Object.fromEntries(url.searchParams)});
    res.setHeader('content-type','application/json');
    res.end(JSON.stringify(url.pathname === '/ping' ? {version} : {employees:4,employeeRoster:roster}));
  });
  await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const client = new Client({name:'workplace-arrival-contract',version:'1'});
  const transport = new StdioClientTransport({command:process.execPath,
    args:[resolve(dirname(fileURLToPath(import.meta.url)),'../dist/index.js')],
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${server.address().port}`},stderr:'pipe'});
  t.after(async()=>{await client.close();server.closeAllConnections();server.close();});
  await client.connect(transport);
  const tool = (await client.listTools()).tools.find(x=>x.name==='cs2_inspect');
  assert.equal(tool.annotations.readOnlyHint,true);
  assert.equal(tool.inputSchema.properties.employeeLimit.maximum,64);
  const args = {index:12,version:1,employeeLimit:2,employeeOffset:1};
  assert.equal((await client.callTool({name:'cs2_inspect',arguments:args})).isError,true);
  assert.deepEqual(requests.map(x=>x.path),['/ping']);
  requests.length=0;
  await client.callTool({name:'cs2_inspect',arguments:{index:12,version:1}});
  assert.deepEqual(requests,[{method:'GET',path:'/entity/inspect',params:{index:'12',version:'1'}}]);
  requests.length=0;version='0.10.5';
  const result=await client.callTool({name:'cs2_inspect',arguments:args});
  assert.notEqual(result.isError,true);
  assert.deepEqual(JSON.parse(result.content[0].text).employeeRoster,roster);
  assert.deepEqual(requests.at(-1),{method:'GET',path:'/entity/inspect',params:{index:'12',version:'1',employeeLimit:'2',employeeOffset:'1'}});
  roster={available:false,count:null,reason:'native_employee_buffer_absent'};
  const missing=await client.callTool({name:'cs2_inspect',arguments:{index:12,version:1,employeeLimit:0}});
  assert.deepEqual(JSON.parse(missing.content[0].text).employeeRoster,roster);
  requests.length=0;
  for (const bad of [{employeeLimit:65},{employeeLimit:-1},{employeeLimit:1.5},{employeeLimit:'2'},
    {employeeLimit:1,employeeOffset:65536},{employeeLimit:1,employeeOffset:-1},{employeeOffset:1},
    {employeeLimit:0,employeeOffset:1}]) {
    assert.equal((await client.callTool({name:'cs2_inspect',arguments:{index:12,version:1,...bad}})).isError,true);
  }
  assert.deepEqual(requests,[]);
});
