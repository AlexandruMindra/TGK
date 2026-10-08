#!/usr/bin/env bash
# stills.sh <lang> <scene> <fraction...>: renders preview frames of one scene into build/<lang>/stills
cd "$(dirname "$0")"
lang=$1; sc=$2; shift 2
T=$(python3 -c "
import json,sys;t=json.load(open('build/$lang/timing.json'))
s=[x for x in t['scenes'] if x['id']=='$sc'][0]
print(' '.join(str(round(s['start']+s['dur']*float(f),2)) for f in sys.argv[1:]))" "$@")
node render.mjs $lang stills $T && echo $T
