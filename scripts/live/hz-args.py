# -*- coding: utf-8 -*-
"""Write an arguments file for hz-call.ps1, with a FRESH idempotency key.

TWO TRAPS THIS EXISTS TO CLOSE, both of which produced convincing-looking
nonsense during the topology experiment:

1. REUSING AN ARGUMENTS FILE REUSES ITS KEY. The bridge answers a repeated
   idempotency_key from cache - correctly, that is the point of the key - so a
   second call returned a probe result from an earlier configuration and an
   inventory from before the fixture existed. Same shape, same fields, no error:
   a cached reply is indistinguishable from a fresh one unless you notice the
   content answers a question you asked earlier.

2. WINDOWS PATHS DO NOT SURVIVE BEING TYPED INTO JSON ON A COMMAND LINE. The
   backslashes are eaten somewhere between the shell and here, and the file
   silently is not written at all - after which hz-call reads whatever artifact
   was already on disk. So paths are never passed in: this builds them.

Usage - simple key=value pairs, no quoting, no backslashes:

    python scripts/live/hz-args.py OUT.json script=wallsplit-topology-probe.py
    python scripts/live/hz-args.py OUT.json ids=1664190 dry=true
    python scripts/live/hz-args.py OUT.json ids=1664190 dry=false token=hz-abc
"""
import io
import json
import os
import sys
import uuid

if len(sys.argv) < 2:
    sys.stderr.write(__doc__)
    sys.exit(2)

out = sys.argv[1]
repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
repo = os.path.join(repo, 'horizun-mcp') if not os.path.isdir(os.path.join(repo, 'scripts')) else repo

args = {"target_document": "HZ_WALLSPLIT"}

for pair in sys.argv[2:]:
    if '=' not in pair:
        sys.stderr.write('not a key=value pair: ' + pair + '\n')
        sys.exit(2)
    k, v = pair.split('=', 1)
    if k == 'script':
        args['code_path'] = os.path.join(repo, 'scripts', 'live', v)
    elif k == 'doc':
        args['target_document'] = v
    elif k == 'ids':
        args['element_ids'] = [int(x) for x in v.split(',') if x]
    elif k == 'dry':
        args['dry_run'] = (v.lower() == 'true')
    elif k == 'token':
        args['confirmation_token'] = v
    elif k == 'path':
        args['path'] = v
    else:
        args[k] = v

args['idempotency_key'] = str(uuid.uuid4())
io.open(out, 'w', encoding='utf-8').write(json.dumps(args))

if 'code_path' in args and not os.path.isfile(args['code_path']):
    sys.stderr.write('SCRIPT NOT FOUND: ' + args['code_path'] + '\n')
    sys.exit(3)
print('wrote ' + os.path.basename(out) + '  key=' + args['idempotency_key'][:8] +
      ('  script=' + os.path.basename(args['code_path']) if 'code_path' in args else ''))
