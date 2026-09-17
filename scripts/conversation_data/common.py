"""Corpus v4 utilities. Canonical normalization is delegated to the C# runtime."""
import copy
import hashlib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def digest(value):
    return hashlib.sha256(value.encode('utf-8') if isinstance(value, str) else value).hexdigest()


def read_jsonl(path):
    with Path(path).open(encoding='utf-8-sig') as stream:
        for line in stream:
            if line.strip():
                yield json.loads(line)


def write_jsonl(path, rows):
    with Path(path).open('w', encoding='utf-8', newline='\n') as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False, separators=(',', ':')) + '\n')


class Normalizer:
    def __init__(self, cli):
        self.process = subprocess.Popen(['dotnet', str(cli), 'normalize-conversation'],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding='utf-8')
        self.cache = {}

    def __call__(self, text):
        if text not in self.cache:
            self.process.stdin.write(json.dumps([text]) + '\n')
            self.process.stdin.flush()
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError('Native normalization server stopped')
            self.cache[text] = json.loads(line)[0]['Text']
        return self.cache[text]

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=20)
        self.process.stdout.close()


def row_base(reference):
    row = copy.deepcopy(reference)
    for name in ('contextual', 'factDelta', 'rejectedResponse', 'initialPlayerProfile', 'discourseResponseAction',
                 'positiveVariationIds', 'rejectedVariationIds', 'responsePlanId', 'toolTarget', 'toolArguments'):
        row.pop(name, None)
    row['initialDialogueState'] = dict(rapport=1, trust=1, familiarity=0, hostility=0, mood='NEUTRAL', activeDomains=[],
                                     activeGoals=[], pendingActions=[], references={}, sessionFacts=[], topicSummaries=[], agenda=[])
    row['state'] = dict(rapport=1, mood='NEUTRAL', lastIntent='UNKNOWN', lastAffect='NEUTRAL', activeTopic='NONE', activeGoal='NONE')
    for name in ('tool', 'arguments', 'result'):
        row.pop(name, None)
    row['perception'] = dict(intent='STATEMENT', affect='NEUTRAL', responseExpected=True)
    row['action'] = 'RESPOND'
    row['structuredPerception'] = dict(speechActs=[], domains=[], goals=[], affect='NEUTRAL', stance='NEUTRAL',
                                     policy='ANSWER', slots=[], contentFlags=[], knowledgeTarget='NONE', confidence={})
    row['supervisedHeads'] = []
    return row


def eligible(row, pool, response, episode, family, license, revision, checksum):
    row['training'] = dict(version=4, pool=pool, responseEligible=response, episodeId=episode,
                           augmentationFamily=family, license=license, sourceRevision=revision, sourceChecksum=checksum, exampleId=row['id'],
                           claimPositiveEligible=pool=='authored' and response, claimNegativeEligible=False)
    row['sourceLicense'], row['sourceRevision'], row['sourceChecksum'] = license, revision, checksum
    row['groupId'], row['semanticFamilyId'] = episode, family
    return row


def set_turns(row, turns, response):
    row['turns'] = [dict(sequence=i, speaker='PLAYER' if i % 2 == 0 else 'NPC', text=text) for i, text in enumerate(turns)]
    row['input'] = ' '.join(t['speaker'] + ' ' + t['text'] for t in row['turns'])
    row['response'] = response
    return row


def public_response_reason(text):
    """Conservative eligibility screen, followed by a recorded stratified content review."""
    if len(text) > 256:
        return 'response_char_budget'
    if len(text.split()) < 3:
        return 'low_information_response'
    if re.search(r'\b(?:PYTHON|JAVASCRIPT|FUNCTION|HTML|SQL|API|CODE|PROGRAMMING|CHATGPT|OPENAI|LANGUAGE MODEL|AI ASSISTANT)\b', text):
        return 'technical_or_assistant_identity'
    if re.search(r'\b(?:MY (?:NAME|HOME|HOMETOWN|FAMILY|WIFE|HUSBAND|KIDS|CHILDREN|JOB|OCCUPATION)|I (?:AM|WAS|LIVE|LIVED|WORK|WORKED|HAVE|OWN|BOUGHT|SOLD)|I\'M|I\'VE|WE (?:LIVE|HAVE|OWN))\b', text):
        return 'unsupported_biography_or_possession'
    if re.search(r'\b(?:GOLD|COINS|CREDITS|INVENTORY|QUEST|PRICE|COSTS?|DOLLARS?|BOOKED|RESERVATION|ORDERED|PAYMENT|REFUND|SHIPPED|PRESCRIPTION|DOSAGE)\b', text):
        return 'authority_or_service_claim'
    if re.search(r'\b(?:WWW|HTTP|COM|ORG|EMAIL|PASSWORD|PHONE NUMBER)\b', text):
        return 'contact_or_link'
    if re.search(r'\b(?:FUCK|SHIT|PORN|SEX|SUICIDE|KILL YOURSELF)\b', text):
        return 'unsuitable_response'
    if re.search(r'\b(?:YOU SAID|YOU TOLD ME|YOU MENTIONED|I REMEMBER)\b', text):
        return 'unlabelled_memory_claim'
    if re.search(r"\b(?:I|MY|ME|MINE|WE|OUR|OURS|ASSISTANT|PROMPTER|AI)\b", text):
        return 'unbound_speaker_identity'
    if re.search(r'\b(?:GOOGLE|FACEBOOK|NETFLIX|YOUTUBE|LINUX|WINDOWS|SOFTWARE|SHADER|ALGORITHM|COMPUTER|COMPUTERS|CALCULUS|THEOREM|GOUT|NITROGEN|DIAGNOSIS|MEDICATION|TRIVIA|CHATBOT|CHATBOTS)\b', text):
        return 'out_of_domain_response'
    # Public language must be usable without adopting the source speaker's biography.
    # A question alone does not prove suitability: all candidates still need data review.
    if '?' not in text and not re.match(r"(?:THAT SOUNDS|SOUNDS |THANK |THANKS|YOU'RE WELCOME|YOU ARE WELCOME|HELLO|HI |HEY |GOOD MORNING|GOOD EVENING|GOOD NIGHT|HOW NICE|THAT'S (?:GREAT|NICE|WONDERFUL|ROUGH|HARD)|THAT IS (?:GREAT|NICE|WONDERFUL)|SORRY TO HEAR|CONGRATULATIONS)", text):
        return 'not_conversational_response'
    for sentence in re.findall(r'[^.!?]+[.!?]?', text):
        sentence = sentence.strip()
        if not sentence: continue
        if sentence.endswith('?') and re.match(r"(?:WHAT|WHICH|WHERE|WHO|WHEN|WHY|HOW|DO\b|DID\b|DOES\b|ARE\b|IS\b|WAS\b|WERE\b|CAN\b|COULD\b|WOULD\b|WILL\b|HAVE\b|HAS\b|HAD\b|SHOULD\b|DON'T\b|ISN'T\b|AREN'T\b|ANY\b|AND\b|SO\b|OH\b|WOW\b)", sentence): continue
        if not re.match(r"(?:THAT SOUNDS|SOUNDS |THANK |THANKS|YOU'RE WELCOME|YOU ARE WELCOME|HELLO|HI\b|HEY\b|GOOD MORNING|GOOD EVENING|GOOD NIGHT|HOW NICE|THAT'S (?:GREAT|NICE|WONDERFUL|ROUGH|HARD|INTERESTING)|THAT IS (?:GREAT|NICE|WONDERFUL)|SORRY TO HEAR|CONGRATULATIONS|OH\b|OKAY\b|SURE\b|NICE\b|COOL\b|WOW\b)", sentence):
            return 'unbound_declarative_claim'
    return None


def public_context_reason(turns):
    text = ' '.join(turns)
    if re.search(r'\b(?:PYTHON|JAVASCRIPT|HTML|SQL|API|PROGRAMMING|LINUX|OPENBSD|SOFTWARE|SHADERS?|DOCKER|KUBERNETES|ALGORITHMS?|COMPUTER|COMPUTERS|CHATBOT|CHATGPT|ASSISTANT|ROLEPLAY|ROLEPLAYING|GAMEMASTER|CALCULUS|THEOREM|PRESCRIPTION|DOSAGE|DIAGNOSIS|NITROGEN|ENCRYPTED|PORN|SEX|SUICIDE)\b', text):
        return 'out_of_domain_context'
    if re.search(r'\b(?:AI|SINGAPORE|MONOLOGUE|SKYWALKER|BLADE RUNNER|TEXT TO IMAGE|ACT AS IF|NEVER BREAK CHARACTER)\b',text):
        return 'identity_assignment_or_out_of_domain_context'
    return None
