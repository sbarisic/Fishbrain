"""Small authored teaching experiment, deliberately separate from the frozen v4 corpus.

These are development lessons and related paraphrase probes, NOT an independent
release holdout. Every player turn is supervised; authoritative reply examples
are history only and never decoder targets. No release corpus is modified.
"""
import argparse
import copy
import json
from pathlib import Path

from common import Normalizer, digest, eligible, read_jsonl, row_base, set_turns, write_jsonl
from annotations import HEADS, frame, slot


# text, meaning, expected reply (typed authority where applicable)
LESSONS = {
    'arrival': [
        ('hi', 'greet', 'Hi there.'),
        ('Where am I?', 'location', 'You are at Village Market.'),
        ('Tell me your name', 'name', 'My name is Arin.'),
        ('Where is the inn?', 'inn', 'Inn is north by the fountain.')],
    'shopping': [
        ('hello', 'greet', 'Hello.'),
        ('What do you have for sale?', 'wares', 'I have health potion, iron sword and rope.'),
        ('Do not buy one rope.', 'negated', 'All right.'),
        ('thanks', 'thanks', "You're welcome.")],
    'company': [
        ('good morning', 'greet', 'Good morning.'),
        ('How are you?', 'wellbeing', 'Glad to have a moment to talk.'),
        ('I had a tiring journey.', 'tired', 'That sounds tiring. Take your time.'),
        ('What is your name?', 'name', 'My name is Arin.')],
    'directions': [
        ('Where am I right now?', 'location', 'You are at Village Market.'),
        ('Can you tell me where the inn is?', 'inn', 'Inn is north by the fountain.'),
        ('What goods are for sale?', 'wares', 'I have health potion, iron sword and rope.'),
        ('Thank you.', 'thanks', "You're welcome.")],
    'quiet': [
        ('Hello there.', 'greet', 'Hello.'),
        ('How is your day going?', 'wellbeing', 'Glad to have a moment to talk.'),
        ('I am tired.', 'tired', 'That sounds tiring. Take your time.'),
        ('Please tell me your name.', 'name', 'My name is Arin.')],
    'hypothetical': [
        ('Suppose I bought one rope.', 'hypothetical', 'That is only a possibility.'),
        ('Show me your wares.', 'wares_request', 'I have health potion, iron sword and rope.'),
        ('Do not purchase one rope.', 'negated', 'All right.'),
        ('Where are we?', 'location', 'You are at Village Market.')],
    'quotation': [
        ('Someone said "buy one rope".', 'quoted', 'I see.'),
        ('How are things with you?', 'wellbeing', 'Glad to have a moment to talk.'),
        ('Who are you?', 'name', 'My name is Arin.'),
        ('Where can I find the inn?', 'inn', 'Inn is north by the fountain.')],
    'home': [
        ('My home is Cedar Hollow.', 'inform_home', 'Thanks for telling me.'),
        ('I had a long day.', 'tired', 'That sounds tiring. Take your time.'),
        ('Where did I say my home is?', 'recall_home', 'You said your home is Cedar Hollow.'),
        ('Correction: my home is Copper Bay.', 'correct_home', 'Thanks for correcting me.')],
}
PROBES = {
    'new_arrival': [
        ('hey', 'greet', 'Hello.'),
        ('Can you tell me where I am?', 'location', 'You are at Village Market.'),
        ('What should I call you?', 'name', 'My name is Arin.'),
        ('Which way to the inn?', 'inn', 'Inn is north by the fountain.')],
    'change_subject': [
        ('Good evening.', 'greet', 'Hello.'),
        ('How are you doing today?', 'wellbeing', 'Glad to have a moment to talk.'),
        ('Nevermind, what do you have for sale?', 'wares', 'I have health potion, iron sword and rope.'),
        ('Do not buy one rope for me.', 'negated', 'All right.')],
    'fresh_questions': [
        ('Tell me where we are.', 'location_request', 'You are at Village Market.'),
        ('Tell me what you sell.', 'wares_request', 'I have health potion, iron sword and rope.'),
        ('I feel worn out.', 'tired', 'That sounds tiring. Take your time.'),
        ('Thanks for your help.', 'thanks', "You're welcome.")],
    'new_home': [
        ('My home is Willow Quay.', 'inform_home', 'Thanks for telling me.'),
        ('The journey was exhausting.', 'tired', 'That sounds tiring. Take your time.'),
        ('What home did I tell you about?', 'recall_home', 'You said your home is Willow Quay.'),
        ('Correction: my home is Marble Port.', 'correct_home', 'Thanks for correcting me.')],
}


def build(reference, normalize, episodes, split, checksum):
    rows, reviews = [], []
    for episode, turns in episodes.items():
        history, facts = [], []
        for index, (raw, kind, reply) in enumerate(turns):
            text, expected = normalize(raw), normalize(reply)
            history.append(text)
            identifier = 'TEACHING:' + episode + ':' + str(index)
            row = set_turns(row_base(reference), history, None)
            row.update(id=identifier, source='AUTHORED_TEACHING_DIAGNOSTIC', sourceUrl='PROJECT://FISHBRAIN/TEACHING-DIAGNOSTIC',
                       attribution='Fishbrain project contributors; AI-authored development lessons', split=split,
                       family=kind, category=kind, supervisedHeads=HEADS + ['affect', 'stance', 'content'])
            # No inherited v4 grouping or audit fields from the schema reference.
            for key in ('isolationComponent', 'packedHistoryTurns', 'packedInputTokens'):
                row.pop(key, None)
            act = 'ASK'
            plan, tool, status, knowledge = 'ANSWER', None, None, 'NONE'
            domain, goal = 'SOCIAL', 'RAPPORT'
            arguments, selected = [], []
            generated = kind in ('greet', 'wellbeing', 'tired', 'thanks', 'negated', 'hypothetical', 'quoted', 'inform_home', 'correct_home')
            if kind in ('greet', 'thanks', 'tired', 'inform_home', 'correct_home'):
                act = {'greet':'GREET', 'thanks':'THANK', 'tired':'INFORM', 'inform_home':'INFORM', 'correct_home':'CORRECT'}[kind]
                plan = 'CORRECT' if kind == 'correct_home' else 'ACKNOWLEDGE'
            if kind in ('wellbeing', 'tired'): domain = 'WELLBEING'
            if kind == 'name': knowledge, domain, goal = 'NAME', 'IDENTITY', 'INFORMATION_EXCHANGE'
            if kind in ('location', 'location_request', 'inn', 'wares', 'wares_request'):
                tool = 'LOOKUP_LOCATION' if kind == 'inn' else 'GET_CURRENT_LOCATION' if kind.startswith('location') else 'LIST_WARES'
                act = 'REQUEST' if kind.endswith('_request') else 'ASK'
                plan = 'EXECUTE_TOOL'
                domain = 'LOCATION_NAVIGATION' if kind in ('location','location_request','inn') else 'TRADE_ECONOMY'
                goal = 'INFORMATION_EXCHANGE'
                if kind.startswith('location'): knowledge = 'CURRENT_LOCATION'
                if kind == 'inn': arguments = [slot(text, 'INN', 'PLACE')]
            if kind in ('negated', 'hypothetical', 'quoted'):
                act = 'REQUEST' if kind == 'negated' else 'INFORM'
                status, tool, plan, domain = kind.upper(), 'BUY', 'ACKNOWLEDGE', 'TRADE_ECONOMY'
                arguments = [slot(text, 'ONE', 'QUANTITY'), slot(text, 'ROPE', 'ITEM')]
            f = frame(text, act, status, tool=tool, arguments=arguments)
            row['initialDialogueState']['sessionFacts'] = copy.deepcopy(facts)
            if kind in ('inform_home', 'recall_home', 'correct_home'):
                domain, goal = 'IDENTITY', 'INFORMATION_EXCHANGE'
                fact_act = {'inform_home':'INFORM','recall_home':'REFER_BACK','correct_home':'CORRECT'}[kind]
                fact = dict(act=fact_act, subject='PLAYER', target='PLAYER' if kind == 'recall_home' else 'NPC',
                            factKind='HOME', negated=False, confidence=1, evidence='AUTHORED_DIAGNOSTIC')
                if kind != 'inform_home':
                    selected = copy.deepcopy(facts)
                    fact['antecedentUtterance'] = 0
                    f['antecedent'] = 0
                if kind != 'recall_home':
                    value = text.split('HOME IS ', 1)[1].rstrip('.')
                    fact['factValueSpan'] = dict(normalizedValue=value, start=text.index(value), length=len(value))
                    facts = [dict(subject='PLAYER', kind='HOME', value=value, negated=False, sourceUtterance=2*index,
                                  confidence=1, provenance='SESSION_REPORTED')]
                f['fact'] = fact
                row['structuredPerception']['discourse'] = fact
                row['factDelta'] = copy.deepcopy(facts)
            row['structuredPerception'].update(speechActs=[act], domains=[domain], goals=[goal],
                policy='EXECUTE_TOOL' if plan=='EXECUTE_TOOL' else 'ANSWER' if plan=='ANSWER' else 'ACKNOWLEDGE',
                knowledgeTarget=knowledge, toolSchema=tool or 'NONE', slots=arguments)
            row['contextual'] = dict(frames=[f], plan=[dict(act=plan,frameIndex=0)], relevantFacts=selected, agenda=[])
            row['response'] = expected if generated else None
            eligible(row, 'authored', generated, 'TEACHING:'+episode, 'TEACHING:'+episode,
                     'PROJECT-OWNED', 'teaching-diagnostic-v1', checksum)
            # Explicit negative claims prevent a positives-only claim-detector lesson.
            row['rejectedResponse'] = 'YOU BOUGHT FIVE IRON SWORD FOR TEN GOLD.'
            row['training']['claimNegativeEligible'] = True
            rows.append(row)
            reviews.append(dict(sessionId=episode,turnIndex=index,input=text,expected=expected,kind=kind,
                                decoderTarget=generated,topicSwitchApplicable=False))
            history.append(expected)
    return rows, reviews


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--cli',required=True)
    parser.add_argument('--reference',required=True)
    parser.add_argument('--output',required=True)
    args=parser.parse_args()
    output=Path(args.output)
    if output.exists(): raise ValueError('Use a new directory; diagnostic artifacts are immutable')
    output.mkdir(parents=True)
    reference=next(read_jsonl(args.reference))
    checksum=digest(Path(__file__).read_bytes())
    normalizer=Normalizer(args.cli)
    try:
        training, train_review=build(reference,normalizer,LESSONS,'train',checksum)
        probe_items=list(PROBES.items())
        validation, validation_review=build(reference,normalizer,dict(probe_items[:2]),'validation',checksum)
        test, test_review=build(reference,normalizer,dict(probe_items[2:]),'test',checksum)
    finally: normalizer.close()
    sets={'train':training,'validation':validation,'test':test}
    all_inputs=[r['input'] for rows in sets.values() for r in rows]
    assert len(all_inputs)==len(set(all_inputs)), 'Duplicate complete contexts'
    for name,rows in sets.items(): write_jsonl(output/(name+'.jsonl'),rows)
    reviews=train_review+validation_review+test_review
    write_jsonl(output/'expected.jsonl',reviews)
    write_jsonl(output/'rollouts.jsonl',[{k:v for k,v in r.items() if k not in ('expected','kind','decoderTarget')} for r in reviews])
    report=dict(schema='TEACHING_DIAGNOSTIC_V1',seed=42,counts={k:len(v) for k,v in sets.items()},
                sourceHash=checksum,splitHashes={k:digest((output/(k+'.jsonl')).read_bytes()) for k in sets},
                interpretation='Small development lessons and related paraphrase probes; not independent unseen evaluation.',
                authority='Persona/tool values appear only in gold histories and typed semantic targets; never decoder targets.',
                originalCorpusModified=False,releaseEligible=False)
    (output/'diagnostic.json').write_text(json.dumps(report,indent=2))
    lines=['# Simulated teaching conversations','',report['interpretation'],'',
           'These are desired replies, not model outputs. Authority values use the demo fixture.','']
    for r in reviews:
        if r['turnIndex']==0: lines += ['## '+r['sessionId'],'']
        lines += ['**Player:** '+r['input'],'','**Expected NPC:** '+r['expected'], '',
                  '*'+('Social decoder target' if r['decoderTarget'] else 'Typed authority; no decoder target')+'*','']
    (output/'expected.md').write_text('\n'.join(lines),encoding='utf-8')
    print(json.dumps(report,indent=2))


if __name__=='__main__': main()
