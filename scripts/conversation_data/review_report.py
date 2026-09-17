"""Readable paired rollouts; leaves every independent human rating unset."""
import argparse
import collections
import json
from pathlib import Path
from common import read_jsonl,digest


def main():
    parser=argparse.ArgumentParser();parser.add_argument('baseline');parser.add_argument('pilot');parser.add_argument('output');args=parser.parse_args()
    old={(r['sessionId'],r['turnIndex']):r for r in read_jsonl(args.baseline)}
    new={(r['sessionId'],r['turnIndex']):r for r in read_jsonl(args.pilot)}
    if old.keys()!=new.keys():raise ValueError('Rollout scenarios differ')
    groups=collections.defaultdict(list)
    for key in new:
        assert old[key]['input']==new[key]['input']
        groups[key[0]].append(key)
    lines=['# Paired conversation rollouts','',
           'Each model receives the same player turns, but uses its own replies and reduced state as history. These are actual runtime rollouts, not gold-history interpretation tests.',
           '', 'Naturalness, relevance and unsupported claims need review. No human ratings have been filled in. The original JSONL exports retain the fields used by `conversation-gate`.', '',
           f'Sessions: {len(groups)}. Player turns: {len(new)}. Multi-turn sessions: {sum(len(g)>1 for g in groups.values())}.', '',
           f'Baseline export SHA-256: `{digest(Path(args.baseline).read_bytes())}`.',
           f'Pilot export SHA-256: `{digest(Path(args.pilot).read_bytes())}`.', '']
    def cell(text):return text.replace('&','&amp;').replace('<','&lt;').replace('>','&gt;').replace('|','\\|').replace('\n',' ')
    for session,keys in groups.items():
        lines += ['## '+session,'','| Player | Failed model | Conversation-v4 pilot |','| --- | --- | --- |']
        for key in sorted(keys):
            a,b=old[key],new[key]
            lines.append('| '+cell(b['input'])+' | '+cell(a['modelResponse'])+' | '+cell(b['modelResponse'])+' |')
        lines.append('')
    Path(args.output).write_text('\n'.join(lines),encoding='utf-8')


if __name__=='__main__':main()
