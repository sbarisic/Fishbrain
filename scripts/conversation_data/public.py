"""Pinned public conversation import. Rejections are data, not silent fallbacks."""
import collections
import gzip
import json
import tarfile

from common import ROOT, digest, public_response_reason, public_context_reason

BST_SHA = '5fbed0068ee89e2d43b93c3ecb341e784617033efa5e8e911a219d4eda6134a6'


def sources():
    original = json.loads((ROOT / 'data/sources.json').read_text())['sources']
    result = [{k: v for k, v in s.items() if k != 'quota'} for s in original if s['name'] in ('OASST1', 'OASST2')]
    result.append(dict(name='BLENDED_SKILL_TALK', revision=BST_SHA, license='CC-BY-4.0',
                       attribution='Blended Skill Talk, Smith et al., Facebook AI Research; conversation contributors',
                       documentation='https://raw.githubusercontent.com/facebookresearch/ParlAI/main/parlai/tasks/blended_skill_talk/README.md',
                       files=[dict(path='blended_skill_talk.tar.gz', sha256=BST_SHA,
                                   url='https://parl.ai/downloads/blended_skill_talk/blended_skill_talk.tar.gz')]))
    for source in result:
        for file in source['files']:
            path = ROOT / 'data/raw' / file['path']
            if digest(path.read_bytes()) != file['sha256']:
                raise ValueError(f'Pinned source checksum mismatch: {path}')
    return result


def import_public(normalize, reject):
    manifests = sources()
    messages, owners = {}, {}
    for source in manifests:
        if not source['name'].startswith('OASST'):
            continue
        with gzip.open(ROOT / 'data/raw' / source['files'][0]['path'], 'rt', encoding='utf-8') as stream:
            for line in stream:
                row = json.loads(line)
                mid = row['message_id']
                if mid in messages:
                    reject(source['name'], mid, 'duplicate_release_message')
                    continue
                messages[mid], owners[mid] = row, source

    for mid, row in messages.items():
        if row['role'] != 'assistant':
            continue
        source = owners[mid]
        reasons = []
        if row.get('lang') != 'en': reasons.append('not_english')
        if row.get('review_result') is not True: reasons.append('not_accepted')
        if row.get('deleted'): reasons.append('deleted')
        if row.get('synthetic'): reasons.append('synthetic')
        labels = row.get('labels') or {}
        quality = labels.get('quality', {}).get('value')
        failure = labels.get('fails_task', {}).get('value')
        if quality is None or quality < .6: reasons.append('missing_or_low_quality')
        if failure is None or failure > .2: reasons.append('missing_or_high_failure_to_answer')
        for label in ('spam', 'pii', 'lang_mismatch'):
            value = labels.get(label, {}).get('value')
            if value is None or value != 0: reasons.append('missing_or_nonzero_' + label)
        if reasons:
            for reason in reasons: reject(source['name'], mid, reason)
            continue
        chain, visited, current = [], set(), row
        while current is not None:
            key = current['message_id']
            if key in visited or current.get('lang') != 'en' or current.get('deleted'):
                chain = []
                break
            visited.add(key)
            chain.append(current)
            parent = current.get('parent_id')
            if parent and parent not in messages:
                chain = []
                break
            current = messages.get(parent)
        chain.reverse()
        if not chain or len(chain) % 2 or any(m['role'] != ('prompter' if i % 2 == 0 else 'assistant') for i, m in enumerate(chain)):
            reject(source['name'], mid, 'invalid_or_incomplete_role_chain')
            continue
        if any(m['role']=='assistant' and m.get('review_result') is not True for m in chain):
            reject(source['name'],mid,'unaccepted_ancestor_response')
            continue
        if any(((m.get('labels') or {}).get(label) or {}).get('value',0)>0 for m in chain for label in ('spam','pii','lang_mismatch')):
            reject(source['name'],mid,'flagged_ancestor_context')
            continue
        turns = [normalize(m['text']) for m in chain]
        if any(not t for t in turns):
            reject(source['name'], mid, 'normalization_rejected')
            continue
        reason = public_response_reason(turns[-1]) or public_context_reason(turns[:-1])
        if reason:
            reject(source['name'], mid, reason)
            continue
        yield dict(id=mid, episode='OASST:' + row['message_tree_id'], source=source,
                   turns=turns[:-1], response=turns[-1], publishedSplit=None,
                   review=dict(quality=quality, failureToAnswer=failure, messageId=mid,accepted=True,deleted=False,language='en',
                               spam=0,personalInformation=0,languageMismatch=0))

    source = next(s for s in manifests if s['name'] == 'BLENDED_SKILL_TALK')
    with tarfile.open(ROOT / 'data/raw/blended_skill_talk.tar.gz', 'r:gz') as archive:
        for filename, split in [('train.json', 'train'), ('valid.json', 'validation'), ('test.json', 'test')]:
            for index, episode in enumerate(json.load(archive.extractfile(filename))):
                eid = f'BST:{split}:{index}'
                if episode.get('bad_workers'):
                    reject(source['name'], eid, 'flagged_worker')
                    continue
                history = [normalize(episode['free_turker_utterance']), normalize(episode['guided_turker_utterance'])]
                for turn, (speaker, raw) in enumerate(episode['dialog']):
                    text = normalize(raw)
                    if speaker != len(history) % 2:
                        reject(source['name'], eid, 'invalid_role_chain')
                        break
                    if speaker == 1:
                        mid = f'{eid}:{turn}'
                        reason = public_response_reason(text) if text else 'normalization_rejected'
                        if any(not t for t in history): reason = 'normalization_rejected_history'
                        elif not reason: reason = public_context_reason(history)
                        if reason:
                            reject(source['name'], mid, reason)
                        else:
                            yield dict(id=mid, episode=eid, source=source, turns=list(history), response=text,
                                       publishedSplit=split, review=dict(actualHumanDialogue=True))
                    history.append(text)


if __name__ == '__main__':
    from common import Normalizer, write_jsonl
    output = ROOT / 'data/training/conversation-v4-public'
    output.mkdir(exist_ok=True)
    rejected = []
    normalizer = Normalizer(ROOT / 'data/training/conversation-v4-tools/Fishbrain.dll')
    try:
        rows = list(import_public(normalizer, lambda source, id, reason: rejected.append(dict(source=source, id=id, reason=reason))))
    finally:
        normalizer.close()
    write_jsonl(output / 'candidates.jsonl', rows)
    write_jsonl(output / 'rejections.jsonl', rejected)
    print(json.dumps(dict(candidates=len(rows), bySource=collections.Counter(r['source']['name'] for r in rows),
                         byHistory=collections.Counter('fresh' if len(r['turns']) == 1 else 'short' if len(r['turns']) <= 5 else 'long' for r in rows),
                         exclusions=collections.Counter(r['reason'] for r in rejected)), indent=2))
