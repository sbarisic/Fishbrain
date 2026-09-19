"""Authored after inspecting V2 smoke failures. Preparation only; never extends training."""
import json,shutil,subprocess
from context_data import ROOT,SOURCE,tool,text,lines,rows
from prepare import sha,write_json

def build():
    episodes=[]
    def memory(quote,id='NEW',required=()):
        return tool(quote,'MEMORY_UPSERT',required=required,ID=id,SUBJECT='PLAYER',PREDICATE='NAME',
            VALUE=quote.removeprefix('My name is '),POLARITY=True,SOURCE_TURN='CURRENT',QUOTE=quote)
    def mine(q):return tool(q,'MEMORY_SEARCH',SUBJECT='PLAYER',PREDICATE='NAME')
    def yours(q):return tool(q,'READ_PERSONA',FIELD='NAME')
    conversations=[
        [text('hello there','Hello.'),tool('Show me the wares.','LIST_WARES'),memory('My name is Steve'),
         mine('What is my name?'),yours('And your name?'),text('Now we have been introduced.','Good to meet you.')],
        [tool('Where are we standing?','GET_CURRENT_LOCATION'),text('I am new here.','What would you like to know?'),memory('My name is Lina'),
         yours('Who are you?'),mine('Who did I say I was?'),text('That is right.','Thanks for confirming.')],
        [tool('Buy 2 rope','BUY',ITEM='ROPE',QUANTITY=2),tool('What does a rope cost?','LOOKUP_PRICE',ITEM='ROPE'),memory('My name is Tomas'),
         mine('Remind me of my name.'),yours('Tell me your name too.'),tool('How much gold is left?','GET_BALANCE')],
        [text('I have a question.','Go ahead.'),yours('What should I call you?'),text('All right.','What else would you like to know?'),
         memory('My name is Maya'),mine('What name did I give you?'),yours('What name do I call you?')],
        [tool('What is in my bag?','LIST_INVENTORY'),tool('Where are we?','GET_CURRENT_LOCATION'),memory('My name is Leona'),
         text('Let us talk about travel.','What do you enjoy about it?'),mine('What is my name again?'),yours('And what is yours?')],
        [text('I like the quiet.','What do you enjoy about it?'),text('Time to think.','What has been on your mind?'),memory('My name is Bruno'),
         mine('Do you recall my name?'),tool('How much gold do I have?','GET_BALANCE'),yours('Who am I talking to?')],
        [tool('How much for an iron sword?','LOOKUP_PRICE',ITEM='IRON SWORD'),tool('Buy 1 iron sword','BUY',ITEM='IRON SWORD',QUANTITY=1),
         memory('My name is Petra'),yours('What is your name?'),mine('Tell me my name.'),tool('Check my balance.','GET_BALANCE')],
        [text('Can we start again?','Of course.'),memory('My name is Dario'),text('I enjoy reading.','What do you like to read?'),
         yours('What should I call you?'),mine('Can you tell me the name I gave?'),text('Yes, that is me.','Good to know.')],
        [memory('My name is Vera'),tool('Show what you sell.','LIST_WARES'),memory('My name is Mira','M1',required=(0,)),
         mine('What is my corrected name?'),yours('What is your name?'),text('I meant to correct mine.','Understood.')],
        [text('Good afternoon.','Hello.'),memory('My name is Oskar'),yours('What is your name?'),memory('My name is Oscar','M1',required=(1,)),
         mine('How did I spell my name now?'),yours('And yours stays the same?')],
        [tool('Where are we?','GET_CURRENT_LOCATION'),memory('My name is Nora'),text('I gave you the wrong name.','You can correct it.'),
         memory('My name is Nela','M1',required=(1,)),mine('Which name did I correct it to?'),yours('What is your own name?')],
        [memory('My name is Luka'),tool('How much gold do I have?','GET_BALANCE'),tool('Buy 1 rope','BUY',ITEM='ROPE',QUANTITY=1),
         memory('My name is Lukasz','M1',required=(0,)),mine('Tell me the corrected name.'),yours('Who are you again?')],
        [tool('What do you have for sale?','LIST_WARES'),tool('how much money for iron sword?','LOOKUP_PRICE',ITEM='IRON SWORD'),
         tool('How much do I have?','GET_BALANCE',required=(1,)),yours('Who are you?'),tool('And where are we?','GET_CURRENT_LOCATION'),
         text('That answers my questions.','Glad I could help.')],
        [tool('Show me your stock.','LIST_WARES'),tool('What is the price of rope?','LOOKUP_PRICE',ITEM='ROPE'),
         tool('And my money?','GET_BALANCE',required=(1,)),text('I am only comparing.','Take your time.'),
         tool('How much for a health potion?','LOOKUP_PRICE',ITEM='HEALTH POTION'),tool('What do I carry?','LIST_INVENTORY')],
        [tool('What can I buy here?','LIST_WARES'),text('I need to decide.','Take your time.'),tool('What is an iron sword worth?','LOOKUP_PRICE',ITEM='IRON SWORD'),
         tool('Show the contents of my pack.','LIST_INVENTORY'),tool('And the money in my purse?','GET_BALANCE'),yours('Before I go, what is your name?')],
        [tool('What does a health potion cost?','LOOKUP_PRICE',ITEM='HEALTH POTION'),tool('What do you have for sale?','LIST_WARES'),
         tool('How much money for rope?','LOOKUP_PRICE',ITEM='ROPE'),tool('How much gold do I have?','GET_BALANCE'),
         text('Enough questions for now.','All right.'),yours('One last thing: your name?')]
    ]
    for i,steps in enumerate(conversations):
        episodes.append(dict(id=f'context-followup-{i:03}',family=f'post-smoke-authored-{i}',split='train',pool='context',steps=steps))
    return episodes

def main():
    path=SOURCE/'followup-plans.jsonl'
    if path.exists():raise RuntimeError('Preserve authored follow-up evidence')
    plans=build();lines(path,plans)
    root=ROOT/'followup';p=root/'prepared';p.mkdir(parents=True)
    shutil.copy2(ROOT/'prepared/tokenizer.json',p/'tokenizer.json')
    subprocess.run(['dotnet',ROOT/'tools/Fishbrain.dll','compile-trajectories',path,p/'episodes.jsonl'],check=True)
    subprocess.run(['dotnet',ROOT/'tools/Fishbrain.dll','pack-corpus',root,p/'packed.jsonl'],check=True)
    write_json(SOURCE/'followup-audit.json',dict(status='PREPARED_NOT_TRAINED',episodes=len(plans),families=len(plans),
        rows=len(rows(p/'packed.jsonl')),packing=json.loads((p/'packed.jsonl.audit.json').read_text()),
        sourceSha256=sha(path),packedSha256=sha(p/'packed.jsonl'),
        note='Authored after the V2 smoke test. Does not alter the frozen corpus, candidate, or held-out evaluation. No further training authorized by this script.'))
if __name__=='__main__':main()
