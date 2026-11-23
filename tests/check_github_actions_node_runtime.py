#!/usr/bin/env python3
"""Prevent known Node 20 GitHub Actions majors from returning.

This is a defense-in-depth source guard, not a universal oracle for arbitrary
third-party or SHA-pinned Actions. Dependabot and CI logs remain authoritative.
"""
from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parent.parent / '.github/workflows'
# First official Node24-compatible majors confirmed against action.yml.
minimum = {
    'actions/checkout': 5,
    'actions/setup-node': 5,
    'actions/setup-go': 6,
    'actions/setup-dotnet': 5,
    'actions/upload-artifact': 6,
    'actions/download-artifact': 7,
    'actions/cache': 5,
    'actions/setup-python': 6,
    'actions/setup-java': 5,
    'actions/github-script': 8,
}
bad = []
for filename in sorted(root.glob('*')):
    if filename.suffix not in ('.yml', '.yaml'):
        continue
    text = filename.read_text()
    for index,line in enumerate(text.splitlines(), 1):
        match = re.search(
            r'^\s*(?:-\s*)?uses:\s*(actions/[a-z-]+)@(v(\d+)|[0-9a-f]{40})',
            line
        )
        if not match: continue
        action,ref,major = match.group(1),match.group(2),match.group(3)
        if action not in minimum: continue
        if major is None:
            bad.append(f'{filename.name}:{index}: SHA pin for {action} needs runtime inspection')
        elif int(major) < minimum[action]:
            bad.append(
                f'{filename.name}:{index}: {action}@{ref} may use Node 20; '
                f'require v{minimum[action]}+'
            )
if bad:
    print('\n'.join(bad),file=sys.stderr)
    raise SystemExit(1)
print('Known official GitHub Actions use Node 24 majors; verify third-party Actions separately.')
