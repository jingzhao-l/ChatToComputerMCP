import test from 'node:test';
import assert from 'node:assert/strict';
import {requireNewStableVersion} from '../tests/check_release_order.mjs';
test('stable publication advances latest without version reuse or downgrade',()=>{
  assert.equal(requireNewStableVersion('0.10.0','"0.9.9"'),'0.9.9');
  assert.equal(requireNewStableVersion('1.0.0','["0.99.9"]'),'0.99.9');
  assert.throws(()=>requireNewStableVersion('0.6.1','"0.6.1"'),/does not advance/);
  assert.throws(()=>requireNewStableVersion('0.6.1','"0.6.2"'),/does not advance/);
  assert.throws(()=>requireNewStableVersion('0.6.2-rc.1','"0.6.1"'),/Invalid/);
  assert.throws(()=>requireNewStableVersion('0.6.2','["0.6.1","0.6.0"]'),/not a valid/);
  assert.throws(()=>requireNewStableVersion('0.6.2','{"error":"network"}'),/not a valid/);
});
