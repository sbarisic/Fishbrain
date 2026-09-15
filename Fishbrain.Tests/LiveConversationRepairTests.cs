using Fishbrain;

namespace Fishbrain.Tests;

internal static partial class RuntimeTestSuite
{
    private static LegacyBrain ConversationTestBrain() => LegacyBrain.CreateForTesting(new BrainConfig
    {
        EmbeddingSize = 8,
        HeadCount = 2,
        MlpSize = 12,
        ContextLength = 256,
        AttentionWindow = 256,
        PositionPeriod = 256,
        MaximumOutputLength = 8,
        Seed = 17
    });

    private static void LiveConversationRepairs() => VerifyLiveConversationRepairs(ConversationTestBrain().Reply);

    private static void VerifyLiveConversationRepairs(Func<ReplyRequest, GameToolRegistry, ReplyResult> reply)
    {
        var world = new DemoWorldState();
        var tools = DemoGameTools.CreateMerchant(world);
        var state = NpcDialogueState.Initial;
        var utterances = new List<DialogueUtterance>();
        long sequence = 0;
        var turn = 0;

        ReplyResult Say(string text)
        {
            utterances.Add(new DialogueUtterance(++sequence, DialogueRole.Player, text));
            var result = reply(new ReplyRequest("LIVE-REPAIRS", (++turn).ToString(), utterances,
                state, NpcPersona.Default, PlayerConversationProfile.Empty, sequence + 1, 900 + turn), tools);
            state = result.State;
            if (result.Text.Length > 0)
            {
                utterances.Add(new DialogueUtterance(++sequence, DialogueRole.Npc, result.Text));
            }

            return result;
        }

        _ = Say("hello");
        _ = Say("who are you?");
        var fellow = Say("also a fellow traveler?");
        Assert(fellow.Text == "YES. I AM A TRAVELER." &&
               fellow.Plan.DiscourseAction == DiscourseResponseAction.AcknowledgeFact,
            "an elliptical identity callback resolves to the NPC's preceding role");

        var correction = Say("i am not a traveler");
        Assert(correction.Text.Contains("NOT YOU", StringComparison.Ordinal),
            "speaker-relative correction does not contradict the NPC's role");
        _ = Say("i am a scientist.");
        var study = Say("i study computer science");
        Assert(study.Text.Contains("COMPUTER SCIENCE", StringComparison.Ordinal) &&
               state.SessionFacts.Any(fact => fact.Subject == DialogueParticipant.Player &&
                   fact.Kind == DialogueFactKind.Activity && fact.Value == "COMPUTER SCIENCE" && !fact.Negated),
            "a field of study is acknowledged and retained as a player activity");

        var explanation = Say("what do you mean?");
        Assert(explanation.Text.Contains("COMPUTER SCIENCE", StringComparison.Ordinal) &&
               explanation.Plan.DiscourseAction == DiscourseResponseAction.ExplainPreviousResponse,
            $"an explanation describes the prior activity response rather than only naming a domain: {explanation.Text}");
        var occupation = Say("what is my occupation?");
        Assert(occupation.Text.Contains("SCIENTIST", StringComparison.Ordinal) &&
               occupation.Diagnostics.ToolInvocation is null &&
               occupation.Perception.Discourse is
               {
                   Act: DiscourseAct.ReferBack,
                   Subject: DialogueParticipant.Player,
                   FactKind: DialogueFactKind.Occupation
               },
            "my occupation recalls the player's fact without routing to persona or world knowledge");

        var sharedLocation = Say("where are we?");
        Assert(sharedLocation.Diagnostics.ToolInvocation?.ToolName == "GET_CURRENT_LOCATION" &&
               sharedLocation.Text == "WE ARE AT VILLAGE MARKET." &&
               sharedLocation.Diagnostics.Slots.All(slot => slot.Type != SlotType.Place || slot.Value != "WE"),
            "where are we uses current location without treating WE as a place");
        var npcLocation = Say("were are you?");
        Assert(npcLocation.Diagnostics.ToolInvocation?.ToolName == "GET_CURRENT_LOCATION" &&
               npcLocation.Text == "I AM AT VILLAGE MARKET.",
            "the common were/where typo preserves NPC-relative location perspective");
        var playerLocation = Say("where am i?");
        Assert(playerLocation.Diagnostics.ToolInvocation?.ToolName == "GET_CURRENT_LOCATION" &&
               playerLocation.Text == "YOU ARE AT VILLAGE MARKET.",
            "where am I preserves player-relative location perspective");

        var availability = Say("do you sell anything?");
        Assert(availability.Diagnostics.ToolInvocation?.ToolName == "LIST_WARES" &&
               availability.Text.Contains("IN STOCK", StringComparison.Ordinal) &&
               availability.Diagnostics.Slots.All(slot => slot.Value != "ANYTHING"),
            "sale availability lists clearly labelled stock without starting a SELL transaction");
        var malformedAvailability = Say("do you sell two anything?");
        Assert(malformedAvailability.Diagnostics.ToolInvocation?.ToolName == "LIST_WARES",
            "a malformed sale-availability question cannot become a mutating transaction");
        var ownership = Say("what do i own?");
        Assert(ownership.Diagnostics.ToolInvocation?.ToolName == "LIST_INVENTORY" &&
               ownership.Text.Contains("YOU CARRY", StringComparison.Ordinal),
            "ownership uses authoritative inventory instead of conversational claims");

        var balanceBeforeGift = world.Balance;
        var inventoryBeforeGift = world.Inventory;
        var gift = Say("give me one iron sword, please.");
        Assert(gift.Perception.Policy == ResponsePolicy.Clarify &&
               gift.Diagnostics.ToolInvocation is null &&
               gift.Text == "DO YOU WANT TO BUY THAT ITEM?" &&
               state.PendingActions.Any(action => action.ToolSchema == "BUY") &&
               world.Balance == balanceBeforeGift && world.Inventory.SequenceEqual(inventoryBeforeGift),
            "gift wording requests purchase confirmation without mutating authoritative state");
        var confirmedPurchase = Say("yes");
        Assert(confirmedPurchase.Diagnostics.ToolInvocation?.ToolName == "BUY" &&
               world.Balance == 75 && world.Inventory.GetValueOrDefault("IRON SWORD") == 1 &&
               state.PendingActions.Count == 0,
            "explicit confirmation executes the pending purchase exactly once");
    }
}
