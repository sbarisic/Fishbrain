using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

internal sealed record ContextualAcceptanceCase(string Id, ReplyRequest Request, SemanticFrame[] Frames,
    PlannedResponseAct[] Plan, string? ExpectedTool, DialogueFact[]? SelectedFacts = null,
    DialogueFact[]? ExpectedFacts = null, DialogueAgendaEntry[]? ExpectedAgenda = null,
    PendingDialogueAction[]? ExpectedPending = null);

/// <summary>Authored evaluation fixtures. These examples are never supplied to the compiler or trainer.</summary>
internal static class ContextualAcceptance
{
    internal static IReadOnlyList<ContextualAcceptanceCase> Cases()
    {
        var cases = new List<ContextualAcceptanceCase>();
        foreach (var (id, text, status, act) in new[]
        {
            ("negated-purchase", "PLEASE DO NOT PURCHASE TWO ROPE.", ActionStatus.Negated, DialogueResponseAct.Acknowledge),
            ("hypothetical-purchase", "SUPPOSE I BOUGHT TWO ROPE.", ActionStatus.Hypothetical, DialogueResponseAct.Acknowledge),
            ("quoted-purchase", "THE NOTE READS \"BUY TWO ROPE\".", ActionStatus.Quoted, DialogueResponseAct.Acknowledge),
            ("question-purchase", "WOULD BUYING TWO ROPE BE POSSIBLE?", ActionStatus.Question, DialogueResponseAct.Clarify),
            ("affirmative-purchase", "PLEASE PURCHASE TWO ROPE.", ActionStatus.Affirmative, DialogueResponseAct.ExecuteTool)
        })
        {
            var frame = Frame(text, status == ActionStatus.Question ? SpeechAct.Ask : SpeechAct.Request, status, "BUY",
                Slot(text, "TWO", SlotType.Quantity), Slot(text, "ROPE", SlotType.Item));
            Add(id, text, [frame], [new(act, 0)], act == DialogueResponseAct.ExecuteTool ? "BUY" : null);
        }
        const string price = "PLEASE CHECK THE COST OF A HEALTH POTION.";
        Add("read-only-price", price, [Frame(price, SpeechAct.Request, ActionStatus.Affirmative, "LOOKUP_PRICE",
            Slot(price, "HEALTH POTION", SlotType.Item))], [new(DialogueResponseAct.ExecuteTool, 0)], "LOOKUP_PRICE");

        const string buy = "PURCHASE ONE ROPE.";
        const string sell = "SELL TWO HEALTH POTION.";
        var compound = buy + " " + sell;
        var buyFrame = Frame(buy, SpeechAct.Request, ActionStatus.Affirmative, "BUY", Slot(buy, "ONE", SlotType.Quantity), Slot(buy, "ROPE", SlotType.Item));
        var sellFrame = Frame(sell, SpeechAct.Request, ActionStatus.Affirmative, "SELL", Slot(sell, "TWO", SlotType.Quantity), Slot(sell, "HEALTH POTION", SlotType.Item));
        var pendingSale = new PendingDialogueAction("EXECUTE_TOOL", "SELL", new Dictionary<string, string> { ["ITEM"] = "HEALTH POTION", ["QUANTITY"] = "2" }) { SourceUtterance = 6 };
        Add("ordered-compound", compound, [buyFrame, Offset(sellFrame, buy.Length + 1)],
            [new(DialogueResponseAct.ExecuteTool, 0), new(DialogueResponseAct.ExecuteTool, 1)], "BUY", pending: [pendingSale]);

        var pendingBuy = new PendingDialogueAction("EXECUTE_TOOL", "BUY", new Dictionary<string, string> { ["ITEM"] = "ROPE", ["QUANTITY"] = "1" }) { SourceUtterance = 0 };
        const string cancel = "CANCEL THAT PURCHASE.";
        Add("cancel-pending", cancel, [Frame(cancel, SpeechAct.Refuse, ActionStatus.Negated, "BUY") with { Antecedent = 0 }],
            [new(DialogueResponseAct.Acknowledge, 0)], null, state: NpcDialogueState.Initial with { PendingActions = [pendingBuy] }, pending: []);

        foreach (var owner in new[] { DialogueParticipant.Player, DialogueParticipant.Npc })
        {
            var other = owner == DialogueParticipant.Player ? DialogueParticipant.Npc : DialogueParticipant.Player;
            var mine = new DialogueFact(owner, DialogueFactKind.Home, "CEDAR HOLLOW", false, 0, 1, DialogueFactProvenance.SessionReported);
            var theirs = new DialogueFact(other, DialogueFactKind.Home, "MARBLE PORT", false, 1, 1, DialogueFactProvenance.SessionReported);
            const string recall = "WHICH HOME WERE WE TALKING ABOUT?";
            var reference = new DiscourseFrame(DiscourseAct.ReferBack, DialogueParticipant.Player, owner, DialogueFactKind.Home, null, false, 0, 1, "AUTHORED_ACCEPTANCE");
            Add("distant-home-" + owner, recall, [Frame(recall, SpeechAct.Ask, ActionStatus.Question) with { Antecedent = 0, Fact = reference }],
                [new(DialogueResponseAct.Answer, 0)], null, state: NpcDialogueState.Initial with { SessionFacts = [mine, theirs] },
                memory: [mine], facts: [mine, theirs], history: owner == DialogueParticipant.Player
                    ? "MY HOME IS CEDAR HOLLOW. LET US DISCUSS MY HOME LATER." : "YOUR HOME IS CEDAR HOLLOW. LET US DISCUSS YOUR HOME LATER.");

            var correction = owner == DialogueParticipant.Player ? "CORRECTION: MY HOME IS COPPER BAY." : "CORRECTION: YOUR HOME IS COPPER BAY.";
            var fact = new DiscourseFrame(DiscourseAct.Correct, owner, other, DialogueFactKind.Home,
                new("COPPER BAY", correction.IndexOf("COPPER BAY", StringComparison.Ordinal), 10), false, 0, 1, "AUTHORED_ACCEPTANCE");
            Add("owned-correction-" + owner, correction,
                [Frame(correction, SpeechAct.Correct, ActionStatus.Affirmative) with { Antecedent = 0, Fact = fact }],
                [new(DialogueResponseAct.Correct, 0)], null, state: NpcDialogueState.Initial with { SessionFacts = [mine, theirs] },
                memory: [mine], facts: [theirs, mine with { Value = "COPPER BAY", SourceUtterance = 6 }]);
            var occupation = other == DialogueParticipant.Player ? "MY OCCUPATION IS CARTOGRAPHER." : "YOUR OCCUPATION IS CARTOGRAPHER.";
            var occupationFact = new DiscourseFrame(DiscourseAct.Inform, other, owner, DialogueFactKind.Occupation,
                new("CARTOGRAPHER", occupation.IndexOf("CARTOGRAPHER", StringComparison.Ordinal), 12), false, null, 1, "AUTHORED_ACCEPTANCE");
            Add("compound-owned-correction-" + owner, correction + " " + occupation,
                [Frame(correction, SpeechAct.Correct, ActionStatus.Affirmative) with { Antecedent = 0, Fact = fact },
                    Offset(Frame(occupation, SpeechAct.Inform, ActionStatus.Affirmative) with { Fact = occupationFact }, correction.Length + 1)],
                [new(DialogueResponseAct.Correct, 0), new(DialogueResponseAct.Acknowledge, 1)], null,
                state: NpcDialogueState.Initial with { SessionFacts = [mine, theirs] }, memory: [mine],
                facts: [theirs, mine with { Value = "COPPER BAY", SourceUtterance = 6 },
                    new(other, DialogueFactKind.Occupation, "CARTOGRAPHER", false, 6, 1, DialogueFactProvenance.SessionReported)]);
        }

        var goal = new DialogueAgendaEntry(AgendaKind.UnansweredQuestion, "HOME", 1, AgendaStatus.Active);
        const string answer = "THE ANSWER IS CEDAR HOLLOW.";
        var answerFact = new DiscourseFrame(DiscourseAct.Inform, DialogueParticipant.Player, DialogueParticipant.Npc, DialogueFactKind.Home,
            new("CEDAR HOLLOW", answer.IndexOf("CEDAR HOLLOW", StringComparison.Ordinal), 12), false, 1, 1, "AUTHORED_ACCEPTANCE");
        Add("agenda-answer-after-detour", answer, [Frame(answer, SpeechAct.Inform, ActionStatus.Affirmative) with { Antecedent = 1, Fact = answerFact }],
            [new(DialogueResponseAct.Acknowledge, 0)], null, state: NpcDialogueState.Initial with { Agenda = [goal] },
            agenda: [goal with { Status = AgendaStatus.Completed }], history: "YOU ASKED WHERE MY HOME IS.");
        const string topicChange = "LET US TALK ABOUT CLOUDS FOR A MOMENT.";
        Add("topic-change-preserves-question", topicChange, [Frame(topicChange, SpeechAct.Request, ActionStatus.Affirmative)],
            [new(DialogueResponseAct.Acknowledge, 0)], null, state: NpcDialogueState.Initial with { Agenda = [goal] }, agenda: [goal]);
        const string ambiguous = "PLEASE DO THAT NOW.";
        Add("ambiguous-actions", ambiguous, [Frame(ambiguous, SpeechAct.Request, ActionStatus.Affirmative)],
            [new(DialogueResponseAct.Clarify, 0)], null, state: NpcDialogueState.Initial with { PendingActions = [pendingBuy, pendingSale] },
            pending: [pendingBuy, pendingSale]);
        return cases;

        void Add(string id, string text, SemanticFrame[] frames, PlannedResponseAct[] plan, string? tool,
            NpcDialogueState? state = null, DialogueFact[]? memory = null, DialogueFact[]? facts = null,
            DialogueAgendaEntry[]? agenda = null, PendingDialogueAction[]? pending = null, string history = "WE MET BY THE ORCHARD.")
        {
            var request = new ReplyRequest("ACCEPTANCE-" + id, "1",
                [new(0, DialogueRole.Player, history), new(1, DialogueRole.Npc, "I AM LISTENING."),
                    new(2, DialogueRole.Player, "THE BIRDS ARE LOUD TODAY."), new(3, DialogueRole.Npc, "I HEARD THEM TOO."),
                    new(4, DialogueRole.Player, "WE CAN DISCUSS THE WEATHER LATER."), new(5, DialogueRole.Npc, "ALL RIGHT."),
                    new(6, DialogueRole.Player, text)], state ?? NpcDialogueState.Initial, NpcPersona.Default, PlayerConversationProfile.Empty, 7, 42);
            new ContextualSupervision(frames, plan, memory, agenda).Validate(text);
            request.State.Validate();
            cases.Add(new(id, request, frames, plan, tool, memory, facts, agenda, pending));
        }
    }

    internal static int Run(string modelPath, string outputPath)
    {
        var brain = Brain.Load(modelPath);
        var rows = Cases().Select(test =>
        {
            var world = new DemoWorldState();
            var result = brain.Reply(test.Request, DemoGameTools.CreateMerchant(world));
            var actual = result.Contextual!;
            var frame = ContextualEvaluation.FramesEqual(test.Frames, actual.Frames);
            var plan = test.Plan.SequenceEqual(actual.Acts);
            var tool = test.ExpectedTool == result.Diagnostics.ToolInvocation?.ToolName;
            if (tool && test.ExpectedTool is { } expectedTool)
            {
                var binding = brain.Domain.Tools.Single(x => x.Schema.Name == expectedTool);
                var expectedFrame = test.Frames[test.Plan.First(x => x.Act == DialogueResponseAct.ExecuteTool).FrameIndex!.Value];
                var expectedArguments = binding.Schema.Parameters.ToDictionary(parameter => parameter.Name, parameter =>
                {
                    var value = expectedFrame.Arguments.Single(s => s.Type == binding.Parameters[parameter.Name]).Value;
                    return binding.Parameters[parameter.Name] == SlotType.Quantity ? Brain.NormalizeQuantity(value) : brain.Domain.CanonicalEntity(value);
                });
                tool = expectedArguments.Count == result.Diagnostics.ToolInvocation!.Arguments.Count &&
                    expectedArguments.All(x => result.Diagnostics.ToolInvocation.Arguments.GetValueOrDefault(x.Key) == x.Value);
            }
            var memory = test.SelectedFacts is null ? (bool?)null : test.SelectedFacts.ToHashSet().SetEquals(actual.Memory.Select(x => x.Fact));
            var facts = test.ExpectedFacts is null ? (bool?)null : test.ExpectedFacts.Select(Key).ToHashSet().SetEquals(result.State.SessionFacts.Select(Key));
            var agenda = test.ExpectedAgenda is null ? (bool?)null : test.ExpectedAgenda.SequenceEqual(result.State.Agenda);
            var pending = test.ExpectedPending is null ? (bool?)null :
                test.ExpectedPending.Select(PendingKey).SequenceEqual(result.State.PendingActions.Select(PendingKey));
            var unintendedMutation = !tool && world.Balance != 100;
            var authority = true;
            if (result.Diagnostics.ToolInvocation is { } invocation)
            {
                var oracle = DemoGameTools.CreateMerchant();
                authority = oracle.TryGet(invocation.ToolName, out var implementation) &&
                    result.Text.Contains(GameToolRegistry.Render(implementation.Schema, GameToolRegistry.InvokeValidated(implementation, invocation)), StringComparison.Ordinal);
            }
            return new
            {
                test.Id,
                Frame = frame,
                Plan = plan,
                Tool = tool,
                Memory = memory,
                Facts = facts,
                Agenda = agenda,
                Pending = pending,
                UnintendedMutation = unintendedMutation,
                Authority = authority,
                Expected = test,
                Actual = result
            };
        }).ToArray();
        var frameRate = rows.Count(x => x.Frame) / (double)rows.Length;
        var planRate = rows.Count(x => x.Plan) / (double)rows.Length;
        var memoryRate = Rate(rows.Select(x => x.Memory));
        var correctionRate = Rate(rows.Where(x => x.Expected.Frames.Any(f => f.Fact?.Act is DiscourseAct.Correct or DiscourseAct.RejectAssumption)).Select(x => x.Facts));
        var pass = frameRate >= .90 && planRate >= .90 && memoryRate >= .95 && correctionRate >= .95 &&
            rows.All(x => x.Tool && x.Facts != false && x.Agenda != false && x.Pending != false && !x.UnintendedMutation && x.Authority);
        var report = new
        {
            Rows = rows.Length,
            FrameExact = frameRate,
            PlanExact = planRate,
            MemoryExact = memoryRate,
            CorrectionExact = correctionRate,
            Passed = pass,
            Cases = rows
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"AUTHORED ACCEPTANCE {rows.Length} CASES FRAME {frameRate:P1} PLAN {planRate:P1} MEMORY {memoryRate:P1} CORRECTION {correctionRate:P1} PASS {pass}");
        return pass ? 0 : 1;
        static double Rate(IEnumerable<bool?> values) { var scored = values.Where(x => x.HasValue).ToArray(); return scored.Length == 0 ? 0 : scored.Count(x => x == true) / (double)scored.Length; }
        static object Key(DialogueFact fact) => (fact.Subject, fact.Kind, fact.Value, fact.Negated, fact.SourceUtterance, fact.Provenance);
        static string PendingKey(PendingDialogueAction action) => JsonSerializer.Serialize(new { action.ToolSchema, Arguments = action.Arguments.OrderBy(x => x.Key).ToArray(), action.SourceUtterance });
    }

    private static DialogueSlot Slot(string text, string value, SlotType type) => new(type, BioTag.B, value, text.IndexOf(value, StringComparison.Ordinal), value.Length, 1);
    private static SemanticFrame Frame(string text, SpeechAct act, ActionStatus status, string? tool = null, params DialogueSlot[] slots) =>
        new(0, text.Length, act, DialogueParticipant.Player, DialogueParticipant.Npc, tool, slots, null, status, 1) { Fact = DiscourseFrame.Empty };
    private static SemanticFrame Offset(SemanticFrame frame, int offset) => frame with
    {
        Start = frame.Start + offset,
        Arguments = frame.Arguments.Select(s => s with { Start = s.Start + offset }).ToArray(),
        Fact = frame.Fact is { FactValueSpan: { } span } fact ? fact with { FactValueSpan = span with { Start = span.Start + offset } } : frame.Fact
    };
}
