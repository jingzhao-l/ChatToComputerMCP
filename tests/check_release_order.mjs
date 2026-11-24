/* SPDX-License-Identifier: Apache-2.0 */
import fs from 'node:fs';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
import {compareVersions,parseRegistryLatest,validVersion} from '../bin/update-support.mjs';

export function requireNewStableVersion(candidate, registryOutput) {
  if (!validVersion(candidate)) throw Error('Invalid release version');
  const latest = parseRegistryLatest(registryOutput);
  if (compareVersions(candidate, latest) <= 0)
    throw Error('Stable release '+candidate+' does not advance npm latest '+latest);
  return latest;
}
if (process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const version=JSON.parse(fs.readFileSync(new URL('../package.json',import.meta.url),'utf8')).version;
  const latest=requireNewStableVersion(version,fs.readFileSync(process.argv[2],'utf8'));
  console.log('Verified stable release '+version+' advances '+latest);
}
