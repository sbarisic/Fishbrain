using Fishbrain;

namespace Fishbrain.DataGenerator;

internal static partial class CorpusCompiler
{
    // Existing project-authored single-act rows already declare their action and tool targets.
    // Carry those annotations into the new contract; external rows keep absent supervision masked.
    private static CorpusRow AddAuthoredPlanTargets(CorpusRow row)
    {
        if (row.Contextual is not null || !row.Source.StartsWith("PROJECT_", StringComparison.Ordinal)) return row;
        var current = row.Turns?.LastOrDefault()?.Text ?? row.Input;
        var offset = row.Input.LastIndexOf(current, StringComparison.Ordinal);
        var perception = row.StructuredPerception;
        if (perception.SpeechActs.Count != 1) return row; // Compound boundaries require explicit annotation.
        var tool = row.ToolTarget ?? perception.ToolSchema;
        if (tool == "NONE") tool = null;
        var status = perception.SpeechActs[0] == SpeechAct.Ask ? ActionStatus.Question : ActionStatus.Affirmative;
        if (tool is not null && ActionLanguage.ExecutionVeto(current) is not null) return row;
        var slots = perception.Slots.Where(s => s.Start >= offset).Select(s => s with { Start = s.Start - Math.Max(0, offset) }).ToArray();
        var frame = new SemanticFrame(0, current.Length, perception.SpeechActs[0], DialogueParticipant.Player, DialogueParticipant.Npc,
            tool, slots, perception.Discourse?.AntecedentUtterance, status, 1);
        var act = tool is not null ? DialogueResponseAct.ExecuteTool : perception.Policy switch
        {
            ResponsePolicy.Clarify => DialogueResponseAct.Clarify,
            ResponsePolicy.Refuse => DialogueResponseAct.Refuse,
            ResponsePolicy.Acknowledge => DialogueResponseAct.Acknowledge,
            _ => DialogueResponseAct.Answer
        };
        var target = new ContextualSupervision([frame], [new(act, 0)]);
        target.Validate(current);
        return row with { Contextual = target };
    }

    internal static IEnumerable<CorpusRow> ContextualRows(string source, int count, int seed)
    {
        if (count % 4 != 0) throw new ArgumentException("Contextual rows require complete four-member families.");
        for (var index = 0; index < count; index++)
        {
            var family = index / 4;
            var member = index % 4;
            var person = FactNames[family % FactNames.Length] + " " + FamilyNames[family / FactNames.Length % FamilyNames.Length];
            var place = FactPlaces[family / (FactNames.Length * FamilyNames.Length) % FactPlaces.Length] + " NEAR " +
                FactPlaces[family / (FactNames.Length * FamilyNames.Length * FactPlaces.Length) % FactPlaces.Length];
            var topic = ConversationTopics[(family + seed) % ConversationTopics.Length];
            var item = new[] { "ROPE", "HEALTH POTION", "IRON SWORD" }[family % 3];
            var quantity = family % 5 + 1;
            var context = $"I SPOKE WITH {person} AT {place} ABOUT {topic}.";
            var input = "";
            var response = "";
            var rejected = "I GAVE YOU TEN GOLD.";
            var frames = new List<SemanticFrame>();
            var acts = new List<PlannedResponseAct>();
            DialogueFact[] facts = [];
            DialogueFact[] relevant = [];
            DialogueAgendaEntry[] agenda = [];
            var discourse = DiscourseFrame.Empty;
            if (source == "PROJECT_CONTEXTUAL_ACTIONS")
            {
                var command = $"BUY {quantity} {item}";
                input = member switch { 0 => command + ".", 1 => "DO NOT " + command + ".", 2 => "IF I " + command + ", WHAT WOULD IT COST?", _ => $"{person} SAID \"{command}\"." };
                var status = member switch { 0 => ActionStatus.Affirmative, 1 => ActionStatus.Negated, 2 => ActionStatus.Hypothetical, _ => ActionStatus.Quoted };
                frames.Add(Frame(input, 0, input.Length, status, "BUY", [Slot(input, item, SlotType.Item), Slot(input, quantity.ToString(), SlotType.Quantity)]));
                acts.Add(new(member == 0 ? DialogueResponseAct.ExecuteTool : member == 2 ? DialogueResponseAct.Clarify : DialogueResponseAct.Acknowledge, 0));
                response = member == 0 ? "LET ME CHECK THAT TRANSACTION." : member == 2 ? "ARE YOU ASKING ABOUT THE PRICE?" : "I HAVE NOT MADE THAT PURCHASE.";
            }
            else if (source == "PROJECT_CONTEXTUAL_MEMORY")
            {
                var owner = member < 2 ? DialogueParticipant.Player : DialogueParticipant.Npc;
                var home = member % 2 == 0 ? place : FactPlaces[(family / (FactNames.Length * FamilyNames.Length) + 1) % FactPlaces.Length];
                facts = [new(owner, DialogueFactKind.Home, home, false, 0, 1, DialogueFactProvenance.SessionReported),
                    new(owner == DialogueParticipant.Player ? DialogueParticipant.Npc : DialogueParticipant.Player, DialogueFactKind.Home, "SOUTH TOWER", false, 1, 1, DialogueFactProvenance.SessionReported)];
                relevant = [facts[0]];
                context = $"{context} {(owner == DialogueParticipant.Player ? "MY" : "YOUR")} HOME IS {home}.";
                input = owner == DialogueParticipant.Player ? "DO YOU REMEMBER WHERE I LIVE?" : "DO YOU REMEMBER YOUR HOME?";
                discourse = new(DiscourseAct.ReferBack, DialogueParticipant.Player, owner, DialogueFactKind.Home, null, false, 0, 1, "ANNOTATED_MEMORY");
                frames.Add(Frame(input, 0, input.Length, ActionStatus.Question, null, []));
                acts.Add(new(DialogueResponseAct.Answer, 0));
                response = owner == DialogueParticipant.Player ? $"YOU TOLD ME YOUR HOME IS {home}." : $"WE DISCUSSED MY HOME AT {home}.";
                rejected = $"YOU LIVE AT {home}.";
            }
            else if (source == "PROJECT_CONTEXTUAL_COMPOUND")
            {
                var first = member % 2 == 0 ? $"DO NOT BUY {quantity} {item}." : $"BUY {quantity} {item}.";
                var secondItem = item == "ROPE" ? "HEALTH POTION" : "ROPE";
                var second = member < 2 ? $"SELL ONE {secondItem}." : $"WHAT IS THE PRICE OF {secondItem}?";
                input = first + " " + second;
                frames.Add(Frame(input, 0, first.Length, member % 2 == 0 ? ActionStatus.Negated : ActionStatus.Affirmative,
                    "BUY", [Slot(input, item, SlotType.Item), Slot(input, quantity.ToString(), SlotType.Quantity)]));
                frames.Add(Frame(input, first.Length + 1, second.Length, member < 2 ? ActionStatus.Affirmative : ActionStatus.Question,
                    member < 2 ? "SELL" : "LOOKUP_PRICE", member < 2
                        ? [Slot(input, secondItem, SlotType.Item, first.Length), Slot(input, "ONE", SlotType.Quantity)]
                        : [Slot(input, secondItem, SlotType.Item, first.Length)]));
                acts.Add(new(DialogueResponseAct.ExecuteTool, member % 2 == 0 ? 1 : 0));
                if (member % 2 != 0) acts.Add(new(DialogueResponseAct.ExecuteTool, 1));
                else acts.Insert(0, new(DialogueResponseAct.Acknowledge, 0));
                response = "I WILL HANDLE ONE ACTION AT A TIME.";
            }
            else if (source == "PROJECT_CONTEXTUAL_AGENDA")
            {
                var kind = member < 2 ? DialogueFactKind.Preference : DialogueFactKind.Activity;
                input = member switch { 0 => $"I PREFER {topic}.", 1 => $"ACTUALLY I DISLIKE {topic}.", 2 => $"I AM STUDYING {topic}.", _ => $"I FINISHED STUDYING {topic}." };
                var span = new DialogueTextSpan(topic, input.IndexOf(topic, StringComparison.Ordinal), topic.Length);
                discourse = new(member == 1 ? DiscourseAct.Correct : DiscourseAct.Inform, DialogueParticipant.Player, DialogueParticipant.Npc,
                    kind, span, member == 1, null, 1, "ANNOTATED_AGENDA");
                agenda = [new(AgendaKind.UnansweredQuestion, kind.ToString().ToUpperInvariant(), 1, member == 3 ? AgendaStatus.Completed : AgendaStatus.Active)];
                frames.Add(Frame(input, 0, input.Length, member == 1 ? ActionStatus.Negated : ActionStatus.Affirmative, null, []));
                acts.Add(new(member == 1 ? DialogueResponseAct.Correct : DialogueResponseAct.Acknowledge, 0));
                if (member != 3) acts.Add(new(DialogueResponseAct.AskFollowUp, 0, kind.ToString().ToUpperInvariant()));
                response = member == 3 ? "YOU HAVE FINISHED THAT STUDY." : $"YOU MENTIONED {topic}. WHAT DREW YOUR ATTENTION TO IT?";
            }
            else throw new ArgumentException("Unknown contextual corpus group.", nameof(source));

            var turns = new[] { new DialogueUtterance(0, DialogueRole.Player, context), new DialogueUtterance(1, DialogueRole.Npc, $"WE WERE DISCUSSING {topic}."), new DialogueUtterance(2, DialogueRole.Player, input) };
            var row = BuildDiscourseRow("PROJECT_DISCOURSE_FACTS", index, "CONTEXTUAL", $"{source}:{family:D5}", input, turns,
                discourse, response, discourse.Act == DiscourseAct.Correct ? DiscourseResponseAction.AcknowledgeCorrection :
                    discourse.Act == DiscourseAct.Inform ? DiscourseResponseAction.AcknowledgeFact : DiscourseResponseAction.None,
                ["PRESERVE FACT OWNERSHIP", "EXECUTE ONLY AFFIRMATIVE ACTIONS"], rejected);
            var selectedFrame = frames.FirstOrDefault(f => f.ToolName is not null && (f.Status == ActionStatus.Affirmative || f.Status == ActionStatus.Question && f.ToolName == "LOOKUP_PRICE"));
            var operational = row.StructuredPerception with
            {
                SpeechActs = [frames[0].SpeechAct],
                Slots = frames.SelectMany(f => f.Arguments)
                    .Select(s => s with { Start = s.Start + row.Input.LastIndexOf(input, StringComparison.Ordinal) }).ToArray(),
                Domains = selectedFrame is null ? [DialogueDomain.Social] : [DialogueDomain.TradeEconomy],
                Policy = selectedFrame is null ? ResponsePolicy.Answer : ResponsePolicy.ExecuteTool,
                ToolSchema = selectedFrame?.ToolName
            };
            var supervision = new ContextualSupervision(frames.ToArray(), acts.ToArray(), relevant, agenda);
            supervision.Validate(input);
            yield return row with
            {
                Source = source,
                SourceChecksum = ProjectChecksum(source),
                GroupId = $"{source}:{family:D5}",
                StructuredPerception = operational,
                InitialDialogueState = NpcDialogueState.Initial with
                {
                    SessionFacts = facts,
                    Agenda = agenda.Select(a => a with { Status = AgendaStatus.Active }).ToArray()
                },
                Contextual = supervision,
                ToolTarget = selectedFrame?.ToolName,
                ToolArguments = selectedFrame is null ? [] : selectedFrame.Arguments.ToDictionary(s => s.Type == SlotType.Item ? "ITEM" : "QUANTITY", s => s.Value),
                FactDelta = DialogueStateReducer.ReduceFacts(facts, discourse, 2).ToArray(),
                ResponsePlanId = "ACKNOWLEDGE",
                SupervisedHeads = FactDiscourseHeads
            };
        }

        static DialogueSlot Slot(string text, string value, SlotType type, int from = 0) => new(type, BioTag.B, value, text.IndexOf(value, from, StringComparison.Ordinal), value.Length, 1);
        static SemanticFrame Frame(string input, int start, int length, ActionStatus status, string? tool, DialogueSlot[] slots) =>
            new(start, length, status == ActionStatus.Question ? SpeechAct.Ask : tool is null ? SpeechAct.Inform : SpeechAct.Request,
                DialogueParticipant.Player, DialogueParticipant.Npc, tool, slots, null, status, 1);
    }
}
