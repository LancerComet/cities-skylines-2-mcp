import test from 'node:test';
import assert from 'node:assert/strict';
import {helperSchema,entityIdentity} from '../dist/mod-tools.js';
import {isMutation} from '../dist/bridge-client.js';

test('Anarchy helpers reject bypass settings and fractional refresh periods',()=>{
  for(const input of [{AnarchyEnabled:true},{MinimumClearanceBelowElevatedNetworks:0},{AllowPlacingMultipleUniqueBuildings:true},{PropRefreshFrequency:1.5},{PropRefreshFrequency:0}]) assert.equal(helperSchema.safeParse(input).success,false);
  assert.equal(helperSchema.safeParse({ElevationStepSlider:true,PropRefreshFrequency:60}).success,true);
});
test('mod entity identity requires version and rejects stale-looking and extra fields',()=>{
  for(const entity of [{index:20},{index:20,version:0},{index:20,version:1,force:true}]) assert.equal(entityIdentity.safeParse(entity).success,false);
});
test('asynchronous mod and prop mutations retain uncertain outcomes on transport failure',()=>{
  for(const path of ['/mods/anarchy/props/refresh','/mods/anarchy/helpers/set','/build/mod/action?action=signals.setPhase','/build/mod/action?action=network.prepare']) assert.equal(isMutation(path),true);
  for(const path of ['/mods/catalog','/mods/inspect?mod=Traffic','/mods/operation']) assert.equal(isMutation(path),false);
});
