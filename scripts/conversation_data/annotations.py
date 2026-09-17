"""Explicit authored targets. Public rows never pass through this module."""
import copy

from common import row_base, set_turns, eligible, digest
from episodes import all_episodes

HEADS = ['speechActs', 'domains', 'goals', 'policy', 'slots', 'tool', 'knowledgeTarget',
         'discourseAct', 'discourseSubject', 'discourseTarget', 'factKind', 'factPolarity', 'factSpan', 'antecedent']
SOCIAL_ACTS = {
    'openings': 'GREET GREET GREET GREET ASK ASK GREET GREET GREET GREET REQUEST INFORM ASK GREET GREET INFORM INFORM GREET ASK GREET',
    'feelings': 'INFORM ' * 20,
    'small_talk': 'ASK INFORM ASK ASK INFORM INFORM INFORM INFORM INFORM ASK INFORM INFORM ASK INFORM INFORM ASK INFORM ASK INFORM INFORM',
    'preferences': 'INFORM ' * 20,
    'clarification': 'INFORM INFORM INFORM INFORM INFORM INFORM INFORM ASK INFORM INFORM INFORM INFORM INFORM INFORM INFORM INFORM INFORM INFORM INFORM ASK',
    'repair': 'CORRECT CORRECT CORRECT CORRECT ASK CORRECT INFORM CORRECT CORRECT INFORM CORRECT INFORM INFORM INFORM CORRECT APOLOGIZE INFORM INFORM CORRECT REQUEST',
    'topic_changes': 'REQUEST ASK REQUEST INFORM INFORM REQUEST REQUEST ASK REQUEST REQUEST INFORM REQUEST INFORM REQUEST INFORM INFORM ASK REQUEST INFORM THANK',
    'follow_ups': 'INFORM ' * 20,
}

GOAL_TOPICS = {
    'preferences': ['PAINTING','WALKING','QUIET PLACES','COOKING','STORIES','CROWDS','STONES','MORNINGS','DRAWING','REPAIRING THINGS',
                    'SINGING','COOPERATIVE GAMES','LEARNING WORDS','MEALS','PUZZLES','EXPLORING','GARDENING','LISTENING','GIFTS','DECISIONS'],
    'follow_ups': ['NEW INTERESTS','FINISHING A STORY','LISTENING','CHOOSING A GIFT','A CALMER DAY','A QUIET WALK','LEARNING A SKILL',
                   'A STORY ENDING','MAKING AMENDS','EXPLAINING IDEAS','A DECISION','KEEPING A PROMISE','DRAWING','A QUIET PASTIME',
                   'ASKING FOR HELP','RECONNECTING','PATIENCE','A DISAGREEMENT','RESTING','ENJOYING LEARNING'],
    'repair': ['A MISREAD TONE','CLARIFYING A REQUEST','A PREFERENCE','ADVICE','A JOKE','THE JOURNEY','TIME TO THINK','AN AGREEMENT',
               'A MISUNDERSTANDING','THE ORIGINAL QUESTION','A FEELING','THINKING ALOUD','ASKING POLITELY','DIFFERENT VIEWS','TIMING',
               'REPEATING A POINT','HEARING BOTH SIDES','UNCERTAINTY','A CLAIM','AN EARLIER QUESTION']}


def empty_fact():
    return dict(act='NONE', subject='NONE', target='NONE', negated=False, confidence=1, evidence='AUTHORED_V4')


def frame(text, act, status=None, start=0, tool=None, arguments=None):
    return dict(start=start, length=len(text), speechAct=act, subject='PLAYER', target='NPC', toolName=tool,
                arguments=arguments or [], status=status or ('QUESTION' if act == 'ASK' else 'AFFIRMATIVE'), confidence=1, fact=empty_fact())


def slot(text, value, kind, offset=0):
    return dict(type=kind, value=value, start=offset + text.index(value), length=len(value), tag='B', confidence=1)


def annotate(episode, reference, normalize, checksum):
    category = episode['category']
    turns = [normalize(t) for t in episode['turns']]
    response = normalize(episode['response']) if episode['response'] else None
    if any(not t for t in turns) or episode['response'] and not response:
        raise ValueError('Unnormalizable authored text: ' + episode['id'])
    row = set_turns(row_base(reference), turns, response)
    current = turns[-1]
    row.update(source='AUTHORED_CONVERSATION_V4', family=category, sourceUrl='PROJECT://FISHBRAIN/CONVERSATION_V4',
               attribution='Fishbrain project contributors', supervisedHeads=HEADS)
    row['id'] = 'AUTHORED:' + episode['id']
    row['category'] = category
    eligible(row, 'authored', response is not None, row['id'], row['id'], 'PROJECT-OWNED', 'conversation-v4', checksum)
    if category == 'corrections' or episode.get('augmentationOf'):
        parent='AUTHORED:'+episode.get('augmentationOf',episode['id'].replace('correction-','memory-'))
        row['training']['episodeId']=parent
        row['training']['augmentationFamily']=parent
        row['semanticFamilyId']=parent
    p = row['structuredPerception']
    p.update(domains=['SOCIAL'], goals=['RAPPORT'])
    memory, agenda = [], []
    if category in SOCIAL_ACTS:
        index = int(episode['id'].rsplit('-', 1)[1])
        act = SOCIAL_ACTS[category].split()[index]
        frames = [frame(current, act)]
        plan = 'CLARIFY' if category == 'clarification' else 'ANSWER' if act == 'ASK' else 'ACKNOWLEDGE'
        plans = [dict(act=plan, frameIndex=0)]
        if response and '?' in response and category=='topic_changes':
            # The player has just introduced a new subject; ask for the missing detail.
            # Do not invent a pre-existing agenda entry for a subject introduced this turn.
            plans.append(dict(act='CLARIFY',frameIndex=0))
        elif response and '?' in response and plan!='CLARIFY':
            # The annotated goal licenses a follow-up, rather than a generic question added by routing.
            goal = dict(kind='GOAL', subject=GOAL_TOPICS[category][index], sourceTurn=0, status='ACTIVE')
            row['initialDialogueState']['agenda'] = [goal]
            row['initialDialogueState']['activeGoals'] = ['RAPPORT' if category in ('openings', 'small_talk') else 'INFORMATION_EXCHANGE']
            agenda = [goal]
            if plan != 'CLARIFY': plans.append(dict(act='ASK_FOLLOW_UP', frameIndex=0, subject=goal['subject']))
        if category == 'feelings': p['domains'] = ['WELLBEING']
        if category == 'clarification': p['goals'] = ['CLARIFICATION']
    elif category in ('memory', 'corrections'):
        owner, kind, value = episode['owner'], episode['kind'], episode['value']
        fact = dict(subject=owner, kind=kind, value=value, negated=False, sourceUtterance=0, confidence=1, provenance='SESSION_REPORTED')
        # Attributed reports about the NPC are distinct from its caller-owned persona.
        other_value={'HOME':'BROOK CROSSING','NAME':'SERA','OCCUPATION':'FARMER','ORIGIN':'SOUTH BANK'}[kind]
        other = dict(subject='NPC' if owner == 'PLAYER' else 'PLAYER', kind=kind, value=other_value, negated=False,
                     sourceUtterance=0, confidence=1, provenance='SESSION_REPORTED')
        turns[0] += ' ' + ('YOUR' if other['subject']=='NPC' else 'MY') + ' ' + kind + ' IS '+other_value+'.'
        set_turns(row,turns,response)
        row['initialDialogueState']['sessionFacts'] = [fact, other]
        memory = [fact]
        discourse = dict(act='REFER_BACK' if category == 'memory' else 'CORRECT', subject='PLAYER' if category == 'memory' else owner,
                         target=owner if category == 'memory' else ('NPC' if owner == 'PLAYER' else 'PLAYER'), factKind=kind,
                         negated=False, antecedentUtterance=0, confidence=1, evidence='AUTHORED_V4')
        if category == 'corrections':
            value = episode['corrected']
            discourse['factValueSpan'] = dict(normalizedValue=value, start=current.index(value), length=len(value))
            row['factDelta'] = [other, dict(fact, value=value, sourceUtterance=len(turns)-1)]
            row['discourseResponseAction'] = 'ACKNOWLEDGE_CORRECTION'
        else:
            row['factDelta'] = [fact, other]
        frames = [dict(frame(current, 'ASK' if category == 'memory' else 'CORRECT'), antecedent=0, fact=discourse)]
        p['discourse'] = discourse
        plans = [dict(act='ANSWER' if category == 'memory' else 'CORRECT', frameIndex=0)]
        p['domains'], p['goals'] = ['IDENTITY'], ['INFORMATION_EXCHANGE']
    elif category == 'fresh_clarification':
        frames=[frame(current,episode['act'])]
        plans=[dict(act='CLARIFY',frameIndex=0)]
        p.update(domains=['ASSISTANCE'],goals=['CLARIFICATION'])
    elif category == 'identity':
        frames=[frame(current,'ASK')]
        plans=[dict(act='ANSWER',frameIndex=0)]
        p.update(domains=['IDENTITY'],goals=['INFORMATION_EXCHANGE'],knowledgeTarget=episode['knowledge'])
    elif category == 'agenda':
        kind,value=episode['kind'],episode['value']
        goal=dict(kind='UNANSWERED_QUESTION',subject=kind,sourceTurn=1,status='ACTIVE')
        row['initialDialogueState']['agenda']=[goal]
        agenda=[dict(goal,status='COMPLETED')]
        discourse=dict(act='INFORM',subject='PLAYER',target='NPC',factKind=kind,negated=False,antecedentUtterance=1,
                       factValueSpan=dict(normalizedValue=value,start=current.index(value),length=len(value)),confidence=1,evidence='AUTHORED_V4')
        frames=[dict(frame(current,'INFORM'),antecedent=1,fact=discourse)]
        plans=[dict(act='ACKNOWLEDGE',frameIndex=0)]
        row['factDelta']=[dict(subject='PLAYER',kind=kind,value=value,negated=False,sourceUtterance=len(turns)-1,confidence=1,provenance='SESSION_REPORTED')]
        p.update(discourse=discourse,domains=['IDENTITY'],goals=['INFORMATION_EXCHANGE'])
    elif category == 'tools':
        tool = episode['tool'].replace('GET_INVENTORY', 'LIST_INVENTORY')
        args = [slot(current, episode[key], kind) for key, kind in [('quantity','QUANTITY'), ('item','ITEM')] if episode[key]]
        frames = [frame(current, episode['act'], episode['status'], tool=None if tool == 'NONE' else tool, arguments=args)]
        execute = tool != 'NONE' and (episode['status'] == 'AFFIRMATIVE' or tool in ('GET_BALANCE','LIST_WARES','LIST_INVENTORY'))
        plans = [dict(act='EXECUTE_TOOL' if execute else 'CLARIFY' if episode['status'] == 'QUESTION' else 'ACKNOWLEDGE', frameIndex=0)]
        p.update(domains=['TRADE_ECONOMY'], goals=['TRANSACTION'], toolSchema=tool, slots=args)
    else:
        clauses = [normalize(c) for c in episode['clauses']]
        index = int(episode['id'].rsplit('-', 1)[1])
        # Tool bindings and argument spans are authored explicitly for each compound case.
        bindings = {5:[None,'LIST_WARES'],6:[None,'GET_BALANCE'],7:[None,'LIST_INVENTORY'],15:[None,'GET_BALANCE'],
                    16:['BUY','SELL'],17:['LIST_WARES','BUY'],18:['LIST_INVENTORY','SELL'],19:['BUY','GET_BALANCE']}
        arguments = {16:[[('ONE','QUANTITY'),('ROPE','ITEM')],[('TWO','QUANTITY'),('APPLES','ITEM')]],
                     17:[[],[('ONE','QUANTITY'),('TORCH','ITEM')]],18:[[],[('ONE','QUANTITY'),('SWORD','ITEM')]],
                     19:[[('APPLE','ITEM')],[]]}
        frames, offset = [], 0
        for i, clause in enumerate(clauses):
            args = [slot(clause, value, kind, offset) for value, kind in arguments.get(index, [[],[]])[i]]
            status = 'NEGATED' if i == 0 and index in (5,15,19) else None
            frames.append(frame(clause, episode['acts'][i], status, offset, bindings.get(index,[None,None])[i], args))
            offset += len(clause) + 1
        plans = [dict(act=act, frameIndex=i) for i, act in enumerate(episode['plans'])]
        p['knowledgeTarget'] = episode['knowledge']
        p['slots'] = [s for f in frames for s in f['arguments']]
        executable = next((f['toolName'] for f, act in zip(frames, episode['plans']) if act == 'EXECUTE_TOOL'), None)
        if executable: p.update(toolSchema=executable, domains=['TRADE_ECONOMY'], goals=['TRANSACTION'])
        if any(a['act'] == 'ASK_FOLLOW_UP' for a in plans):
            agenda = [dict(kind='GOAL', subject='CONVERSATION', sourceTurn=0, status='ACTIVE')]
            row['initialDialogueState']['agenda'] = agenda
            for a in plans:
                if a['act'] == 'ASK_FOLLOW_UP': a['subject'] = 'CONVERSATION'
    p['speechActs'] = list(dict.fromkeys(f['speechAct'] for f in frames))
    p['policy'] = 'EXECUTE_TOOL' if any(a['act'] == 'EXECUTE_TOOL' for a in plans) else 'CLARIFY' if any(a['act'] == 'CLARIFY' for a in plans) else 'ANSWER' if any(a['act'] == 'ANSWER' for a in plans) else 'ACKNOWLEDGE'
    row['contextual'] = dict(frames=frames, plan=plans, relevantFacts=memory, agenda=agenda)
    row['input']=normalize(row['input'])
    return row
