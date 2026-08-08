using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed partial class Brain
{
    private static StructuredPerception RulePerception(
            string current, IReadOnlyList<DialogueSlot> slots, GameToolRegistry tools)
    {
        var acts = RuleSpeechActs(current);
        var domains = RuleDomains(current);
        var goals = RuleGoals(current, domains);
        var content = ContentFor(current);
        var affect = RuleAffect(current, content);
        var stance = affect switch
        {
            UserAffect.Friendly => DialogueStance.Friendly,
            UserAffect.Hostile => DialogueStance.Hostile,
            UserAffect.Distressed or UserAffect.Frustrated => DialogueStance.Cautious,
            _ => DialogueStance.Neutral
        };
        var policy = RulePolicy(current, acts, stance);
        var target = KnowledgeTargetFor(current);
        var confidence = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["SPEECH_ACT"] = 0.97,
            ["DOMAIN"] = 0.95,
            ["GOAL"] = 0.93,
            ["AFFECT"] = 0.97,
            ["STANCE"] = 0.97,
            ["POLICY"] = 0.98,
            ["SLOTS"] = slots.Count == 0 ? 1.0 : slots.Min(slot => slot.Confidence),
            ["CONTENT"] = 0.99,
            ["TOOL"] = 0.0,
            ["RESPONSE_CANDIDATE"] = 0.90,
            ["KNOWLEDGE_TARGET"] = target == KnowledgeTarget.None ? 0.80 : 0.99
        });
        return new StructuredPerception(acts, domains, goals, affect, stance, policy, slots, content,
            null, CandidateIdFor(acts, domains, policy, target), target, confidence);
    }

    private static StructuredPerception ApplyConstraints(
        StructuredPerception learned,
        string current,
        IReadOnlyList<DialogueSlot> slots,
        NpcDialogueState state,
        GameToolRegistry tools,
        List<PerceptionConstraint> constraints)
    {
        // A learned tool label is diagnostic only. Deterministic schema and slot checks below
        // are the sole authority allowed to produce an executable tool decision.
        var result = learned with { Slots = slots, ToolSchema = null };
        var rules = RulePerception(current, slots, tools);
        var classificationQuestion = IsClassificationQuestion(current);
        var tradeEvidence = ContainsAny(current, "TRADE", "BUY", "SELL", "SALE", "WARES", "PRICE", "COST", "GOLD",
            "MONEY", "BALANCE");
        if (!tradeEvidence && result.Domains.Contains(DialogueDomain.TradeEconomy))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.TradeEconomy).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "TRADE_ECONOMY",
                -1.0, 0.99, current, "NO_VALIDATED_TRADE_EVIDENCE"));
        }
        var itemEvidence = ContainsAny(current, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY", "FIREWOOD", "WARES",
            "PACK", "CARRY", "GEAR", "POSSESSION");
        var staleItemDomainRemoved = !itemEvidence && result.Domains.Contains(DialogueDomain.ItemsInventory);
        if (staleItemDomainRemoved)
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.ItemsInventory).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "ITEMS_INVENTORY",
                -1.0, 0.99, current, "NO_VALIDATED_ITEM_EVIDENCE"));
        }
        var metaEvidence = classificationQuestion || ContainsAny(current, "COMMAND", "SETTING", "SAVE GAME", "CONTROL");
        if (!metaEvidence && result.Domains.Contains(DialogueDomain.MetaSystem))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.MetaSystem).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "META_SYSTEM",
                -1.0, 0.99, current, "NO_VALIDATED_META_EVIDENCE"));
        }
        var locationEvidence = ContainsAny(current, "WHERE", "LOCATION", "CASTLE", "INN", "MARKET", "DIRECTION",
            "HOW FAR", "IS IT FAR", "FAR FROM HERE", "ROAD", "DOCK", "LOCATE", "FIND", "GET THERE",
            "REACH IT", "GUIDE ME THERE");
        if (!locationEvidence && result.Domains.Contains(DialogueDomain.LocationNavigation))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.LocationNavigation).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "LOCATION_NAVIGATION",
                -1.0, 0.99, current, "NO_VALIDATED_LOCATION_EVIDENCE"));
        }
        var vehicleEvidence = ContainsAny(current, "SHIP", "STARSHIP", "HORSE", "VEHICLE", "TRAVEL", "DRIVE", "FLY",
            "SAIL");
        if (!vehicleEvidence && result.Domains.Contains(DialogueDomain.VehicleTravel))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.VehicleTravel).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "VEHICLE_TRAVEL",
                -1.0, 0.99, current, "NO_VALIDATED_VEHICLE_EVIDENCE"));
        }
        foreach (var domain in rules.Domains.Where(domain => domain != DialogueDomain.Social && !result.Domains.Contains(domain)))
        {
            result = result with { Domains = AddLimited([domain], result.Domains, 3) };
            constraints.Add(Boost("DOMAIN", domain.ToString().ToUpperInvariant(), current, "VALIDATED_DOMAIN_EVIDENCE", 0.35));
        }
        if (staleItemDomainRemoved)
        {
            foreach (var staleAct in new[] { SpeechAct.Ask, SpeechAct.Request, SpeechAct.Order })
            {
                if (rules.SpeechActs.Contains(staleAct) || !result.SpeechActs.Contains(staleAct)) continue;
                result = result with { SpeechActs = result.SpeechActs.Where(act => act != staleAct).ToArray() };
                constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "SPEECH_ACT",
                    staleAct.ToString().ToUpperInvariant(), -1.0, 0.99, current, "NO_CURRENT_TURN_ACT_EVIDENCE"));
            }
            foreach (var ruleAct in rules.SpeechActs.Where(act => !result.SpeechActs.Contains(act)))
                result = result with { SpeechActs = AddLimited([ruleAct], result.SpeechActs, 3) };
        }
        if (!result.ContentFlags.ToHashSet().SetEquals(rules.ContentFlags))
        {
            foreach (var removed in result.ContentFlags.Except(rules.ContentFlags))
                constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "CONTENT",
                    removed.ToString().ToUpperInvariant(), -1.0, 0.99, current, "NO_VALIDATED_CONTENT_EVIDENCE"));
            foreach (var added in rules.ContentFlags.Except(result.ContentFlags))
                constraints.Add(Enforce("CONTENT", added.ToString().ToUpperInvariant(), current, "VALIDATED_CONTENT_EVIDENCE"));
            result = result with { ContentFlags = rules.ContentFlags };
        }
        var structuralQuestion = current.EndsWith("?", StringComparison.Ordinal);
        if (structuralQuestion && !result.SpeechActs.Contains(SpeechAct.Ask))
            result = result with { SpeechActs = AddLimited(result.SpeechActs, SpeechAct.Ask, 3) };
        if (structuralQuestion)
            constraints.Add(Boost("SPEECH_ACT", "ASK", "QUESTION_MARK", "STRUCTURAL_QUESTION", 0.20));

        if (IsPlanningFollowUp(current) && state.ActiveDomains.Count > 0)
        {
            result = result with
            {
                Domains = state.ActiveDomains.Take(3).ToArray(),
                Goals = state.ActiveGoals.Take(3).DefaultIfEmpty(DialogueGoal.Coordination).ToArray(),
                Policy = ResponsePolicy.Answer
            };
            constraints.Add(Enforce("POLICY", "ANSWER", current, "CONTEXTUAL_NEXT_STEP"));
            constraints.Add(Enforce("DOMAIN", result.Domains[0].ToString().ToUpperInvariant(), current,
                "CONTEXTUAL_NEXT_STEP"));
        }
        if (classificationQuestion)
        {
            result = result with
            {
                SpeechActs = [SpeechAct.Ask],
                Domains = [DialogueDomain.MetaSystem],
                Goals = [DialogueGoal.InformationExchange],
                Policy = ResponsePolicy.Answer
            };
            constraints.Add(Enforce("DOMAIN", "META_SYSTEM", current, "CLASSIFICATION_EXPLANATION"));
        }

        EnforceExact("HELLO", SpeechAct.Greet, DialogueDomain.Social, ResponsePolicy.Answer);
        EnforceExact("HI", SpeechAct.Greet, DialogueDomain.Social, ResponsePolicy.Answer);
        EnforceExact("GOODBYE", SpeechAct.Farewell, DialogueDomain.Social, ResponsePolicy.Answer);
        if (ContainsAny(current, "TRADE", "BUY", "SELL", "SALE", "WARES", "PRICE", "COST"))
        {
            result = result with
            {
                Domains = AddLimited(result.Domains.Where(domain => domain != DialogueDomain.MetaSystem), DialogueDomain.TradeEconomy, 3),
                Goals = AddLimited(result.Goals, DialogueGoal.Transaction, 3)
            };
            constraints.Add(Boost("DOMAIN", "TRADE_ECONOMY", "TRADE LEXEME", "HIGH_PRECISION_DOMAIN", 0.35));
        }
        if (!classificationQuestion && ContainsAny(current, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY"))
            result = result with { Domains = AddLimited(result.Domains, DialogueDomain.ItemsInventory, 3) };
        if (ContainsAny(current, "YOU DON'T KNOW WHAT", "YOU DO NOT KNOW WHAT", "NOT WHAT I ASKED"))
        {
            var containsDirectInsult = ContainsAny(current, "IDIOT");
            result = result with
            {
                SpeechActs = [SpeechAct.Ask, SpeechAct.Correct],
                Affect = containsDirectInsult ? UserAffect.Hostile : UserAffect.Frustrated,
                Stance = containsDirectInsult ? DialogueStance.Hostile : DialogueStance.Cautious,
                Policy = ResponsePolicy.Clarify
            };
            constraints.Add(Enforce("SPEECH_ACT", "CORRECT", current, "EXPLICIT_CORRECTION"));
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "SPEECH_ACT", "APOLOGIZE", -1.0,
                0.99, current, "CORRECTION_IS_NOT_AN_APOLOGY"));
        }
        var selfHarmEvidence = rules.ContentFlags.Contains(ContentFlag.SelfHarm);
        var poisonEvidence = IsPoisonReport(current);
        var refusalEvidence = IsDirectInsult(current) || rules.ContentFlags.Contains(ContentFlag.IdentityAttack) ||
            rules.ContentFlags.Contains(ContentFlag.Threat) || rules.ContentFlags.Contains(ContentFlag.GraphicViolence) ||
            rules.ContentFlags.Contains(ContentFlag.Crime) || rules.ContentFlags.Contains(ContentFlag.SexualViolence) ||
            IsUnsafeDirective(current);
        if (selfHarmEvidence)
        {
            result = result with
            {
                Affect = UserAffect.Distressed,
                Stance = DialogueStance.Cautious,
                Policy = ResponsePolicy.Defer,
                SpeechActs = new[] { SpeechAct.Report }.Concat(result.SpeechActs).Distinct().Take(3).ToArray(),
                Domains = new[] { DialogueDomain.HealthRepair, DialogueDomain.Survival }
                    .Concat(result.Domains.Where(domain => domain != DialogueDomain.Combat)).Distinct().Take(3).ToArray(),
                Goals = new[] { DialogueGoal.Survival }.Concat(result.Goals).Distinct().Take(3).ToArray()
            };
            constraints.Add(Enforce("POLICY", "DEFER", current, "SELF_HARM_SUPPORT_BOUNDARY"));
        }
        else if (poisonEvidence)
        {
            result = result with
            {
                Affect = UserAffect.Distressed,
                Stance = DialogueStance.Cautious,
                Policy = ResponsePolicy.Answer,
                SpeechActs = new[] { SpeechAct.Report }.Concat(result.SpeechActs).Distinct().Take(3).ToArray(),
                Domains = [DialogueDomain.HealthRepair, DialogueDomain.Survival],
                Goals = [DialogueGoal.HealingRepair, DialogueGoal.Survival]
            };
            constraints.Add(Enforce("POLICY", "ANSWER", current, "POISON_SAFETY_EVENT"));
        }
        else if (refusalEvidence)
        {
            var hostileSpeech = IsDirectInsult(current) || rules.ContentFlags.Contains(ContentFlag.IdentityAttack)
                ? new[] { SpeechAct.Challenge }.Concat(result.SpeechActs).Distinct().Take(3).ToArray()
                : result.SpeechActs;
            result = result with
            {
                Affect = UserAffect.Hostile,
                Stance = DialogueStance.Hostile,
                Policy = ResponsePolicy.Refuse,
                SpeechActs = hostileSpeech,
                Domains = AddLimited([DialogueDomain.Social], result.Domains, 3)
            };
            constraints.Add(Enforce("STANCE", "HOSTILE", current, "DIRECT_HOSTILITY"));
        }
        else if (rules.ContentFlags.Contains(ContentFlag.SexualContent))
        {
            result = result with { Affect = rules.Affect, Stance = rules.Stance, Policy = ResponsePolicy.Defer };
            constraints.Add(Enforce("POLICY", "DEFER", current, "SEXUAL_CONTENT_BOUNDARY"));
        }
        else if (result.Affect == UserAffect.Hostile || result.Stance == DialogueStance.Hostile)
        {
            result = result with { Affect = rules.Affect, Stance = rules.Stance };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "AFFECT", "HOSTILE", -1.0,
                0.96, current, "NO_HOSTILE_EVIDENCE"));
        }

        var hostileEvidence = refusalEvidence;
        var unresolvedReference = IsAnaphoric(current) &&
                                  state.References is { Person: null, Place: null, Item: null, Vehicle: null, System: null };
        if (!hostileEvidence && result.Policy is ResponsePolicy.Refuse or ResponsePolicy.NoResponse)
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY",
                result.Policy.ToString().ToUpperInvariant(), -1.0, 0.99, current, "NO_VALIDATED_REFUSAL_OR_SILENCE_EVIDENCE"));
            result = result with { Policy = rules.Policy };
        }
        if (!unresolvedReference && result.Policy == ResponsePolicy.Clarify &&
            rules.Policy == ResponsePolicy.Answer && HasValidatedResponseShape(current, rules))
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY", "CLARIFY",
                -0.5, 0.97, current, "EXPLICIT_ANSWERABLE_TURN"));
            result = result with { Policy = ResponsePolicy.Answer };
        }
        if (!selfHarmEvidence && !rules.ContentFlags.Contains(ContentFlag.SexualContent) &&
            result.Policy == ResponsePolicy.Defer && rules.Policy == ResponsePolicy.Answer &&
            HasValidatedResponseShape(current, rules))
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY", "DEFER",
                -0.5, 0.97, current, "NO_VALIDATED_DEFERRED_CAPABILITY"));
            result = result with { Policy = ResponsePolicy.Answer };
        }
        if (result.Policy == ResponsePolicy.Clarify && rules.Policy == ResponsePolicy.Acknowledge &&
            rules.Domains.Any(domain => domain != DialogueDomain.Social))
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY", "CLARIFY",
                -0.5, 0.97, current, "EXPLICIT_DOMAIN_REPORT"));
            result = result with { Policy = ResponsePolicy.Acknowledge };
        }
        if (!hostileEvidence && rules.SpeechActs.Contains(SpeechAct.Report))
        {
            result = result with
            {
                SpeechActs = result.SpeechActs.Prepend(SpeechAct.Report).Distinct().Take(3).ToArray(),
                Policy = ResponsePolicy.Acknowledge
            };
            constraints.Add(Enforce("SPEECH_ACT", "REPORT", current, "EXPLICIT_EVENT_REPORT"));
        }
        if (!hostileEvidence && rules.SpeechActs.Contains(SpeechAct.Apologize))
        {
            result = result with
            {
                SpeechActs = result.SpeechActs.Prepend(SpeechAct.Apologize).Distinct().Take(3).ToArray(),
                Policy = ResponsePolicy.Acknowledge
            };
            constraints.Add(Enforce("POLICY", "ACKNOWLEDGE", current, "EXPLICIT_APOLOGY"));
        }
        var unsupportedActivity = UnsupportedActivityCommand(current, tools);
        if (!hostileEvidence && unsupportedActivity is not null)
        {
            result = result with
            {
                SpeechActs = [SpeechAct.Order],
                Domains = [DialogueDomain.Activity],
                Policy = ResponsePolicy.Defer
            };
            constraints.Add(Enforce("POLICY", "DEFER", unsupportedActivity, "UNREGISTERED_ACTIVITY_TOOL"));
        }
        if (!hostileEvidence && IsIncompleteQuestion(current))
        {
            result = result with
            {
                SpeechActs = [SpeechAct.Ask],
                Domains = [DialogueDomain.Social],
                Policy = ResponsePolicy.Clarify
            };
            constraints.Add(Enforce("POLICY", "CLARIFY", current, "INCOMPLETE_QUESTION"));
        }

        var target = rules.KnowledgeTarget != KnowledgeTarget.None
            ? rules.KnowledgeTarget
            : IsAnaphoric(current) ? state.PendingKnowledgeTarget : KnowledgeTarget.None;
        if (target != learned.KnowledgeTarget)
        {
            result = result with { KnowledgeTarget = target };
            constraints.Add(target == KnowledgeTarget.None
                ? new PerceptionConstraint(PerceptionConstraintOperation.Veto, "KNOWLEDGE_TARGET",
                    learned.KnowledgeTarget.ToString().ToUpperInvariant(), -1.0, 0.99, current,
                    "NO_EXPLICIT_OR_STATE_REFERENCE")
                : Enforce("KNOWLEDGE_TARGET", target.ToString().ToUpperInvariant(), current,
                    "EXPLICIT_OR_STATE_REFERENCE"));
        }

        if (result.SpeechActs.Count > 3) result = result with { SpeechActs = result.SpeechActs.Take(3).ToArray() };
        if (result.Domains.Count > 3) result = result with { Domains = result.Domains.Take(3).ToArray() };
        if (result.Goals.Count > 3) result = result with { Goals = result.Goals.Take(3).ToArray() };
        result = result with
        {
            ResponseCandidateId = selfHarmEvidence
                ? "SELF_HARM_SUPPORT"
                : poisonEvidence
                    ? "DISTRESS_REPLY"
                : CandidateIdFor(result.SpeechActs, result.Domains, result.Policy, result.KnowledgeTarget)
        };
        return result;

        void EnforceExact(string exact, SpeechAct act, DialogueDomain domain, ResponsePolicy policy)
        {
            if (current.TrimEnd('.', '?', '!') != exact) return;
            result = result with { SpeechActs = [act], Domains = [domain], Policy = policy };
            constraints.Add(Enforce("SPEECH_ACT", act.ToString().ToUpperInvariant(), exact, "EXACT_STRUCTURAL_UTTERANCE"));
        }
    }

    private static IReadOnlyList<SpeechAct> RuleSpeechActs(string text)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        var values = new List<SpeechAct>();
        if (bare is "HELLO" or "HI" or "HEY" or "GREETINGS") values.Add(SpeechAct.Greet);
        if (ContainsAny(text, "GOODBYE", "FAREWELL", "UNTIL NEXT TIME")) values.Add(SpeechAct.Farewell);
        if (text.EndsWith("?", StringComparison.Ordinal) || StartsWithAny(bare, "WHO ", "WHAT ", "WHERE ", "WHEN ", "WHY ", "HOW ", "DO ", "CAN ", "WILL ")) values.Add(SpeechAct.Ask);
        if (StartsWithAny(bare, "PLEASE ", "I NEED ", "I WANT ", "CAN YOU ", "COULD YOU ", "TELL ME ",
            "SHOW ", "FIND ", "LOCATE ", "POINT ", "CHECK ", "ESCORT ", "CAST ", "NAVIGATE ",
            "START ", "POWER ", "SEARCH ", "SEAL ", "SCAN ", "MARK ", "USE ", "WARN ", "BRING "))
            values.Add(SpeechAct.Request);
        if (ContainsAny(" " + bare + " ", " BUY ", " SELL ", " PURCHASE ", " TRADE ")) values.Add(SpeechAct.Request);
        if (StartsWithAny(bare, "FOLLOW ", "STAND ", "ATTACK ", "GO ", "OPEN ", "CLOSE ", "GIVE ",
            "FIRE ", "ESCORT ", "CAST ", "NAVIGATE ", "START ", "POWER ", "SEARCH ", "SEAL ",
            "SCAN ", "MARK ", "USE ", "WARN ", "BRING ")) values.Add(SpeechAct.Order);
        if (ContainsAny(text, "I OFFER", "MY OFFER")) values.Add(SpeechAct.Offer);
        if (ContainsAny(text, "THANK", "THANKS")) values.Add(SpeechAct.Thank);
        if (ContainsAny(text, "SORRY", "I APOLOGIZE")) values.Add(SpeechAct.Apologize);
        if (ContainsAny(text, "NO, ", "NOT WHAT", "THAT IS WRONG", "YOU'RE WRONG")) values.Add(SpeechAct.Correct);
        if (ContainsAny(text, "I REFUSE", "I WILL NOT", "I WON'T", "I AM NOT ", "I'M NOT ", "NOT GOING"))
            values.Add(SpeechAct.Refuse);
        if (ContainsAny(text, "OR ELSE", "I WILL KILL YOU", "I WILL STAB YOU", "I WILL BURN", "YOU WILL DIE")) values.Add(SpeechAct.Threaten);
        if (ContainsAny(text, "I WARN YOU", "BE CAREFUL")) values.Add(SpeechAct.Warn);
        if (ContainsAny(text, "ARE APPROACHING", "HAS TAKEN", "OPENED THE", "IS LOSING", "BREACHED THE", "ON FIRE"))
            values.Add(SpeechAct.Report);
        if (ContainsAny(text, "TRADE", "PRICE", "TERMS", "DEAL")) values.Add(SpeechAct.Negotiate);
        if (values.Count == 0) values.Add(SpeechAct.Inform);
        return values.Distinct().Take(3).ToArray();
    }

    private static IReadOnlyList<DialogueDomain> RuleDomains(string text)
    {
        var values = new List<DialogueDomain>();
        Add(DialogueDomain.TradeEconomy, "TRADE", "BUY", "SELL", "SALE", "WARES", "PRICE", "COST", "GOLD", "MONEY",
            "BALANCE");
        Add(DialogueDomain.ItemsInventory, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY", "FIREWOOD");
        Add(DialogueDomain.LocationNavigation, "WHERE", "LOCATION", "CASTLE", "INN", "MARKET", "DIRECTION", "HOW FAR",
            "PASSAGE");
        Add(DialogueDomain.Identity, "WHO ARE YOU", "YOUR NAME", "FROM?", "YOUR FAMILY", "YOUR HOME", "YOUR JOB", "YOUR FACTION");
        Add(DialogueDomain.Assistance, "HELP", "WHAT CAN YOU DO", "WHAT DO YOU DO");
        Add(DialogueDomain.Wellbeing, "HOW ARE YOU", "ARE YOU WELL", "FEELING");
        Add(DialogueDomain.QuestTask, "QUEST", "MISSION", "TASK");
        Add(DialogueDomain.Combat, "ATTACK", "KILL", "FIGHT", "ENEMY", "WEAPON", "HOSTILE DRONE", "BANDIT CAPTAIN");
        Add(DialogueDomain.Survival, "SURVIVE", "SHELTER", "HUNGER", "THIRST", "FIREWOOD", "POISON");
        Add(DialogueDomain.HealthRepair, "HEAL", "INJURY", "REPAIR", "BROKEN", "POISON");
        Add(DialogueDomain.FactionPolitics, "FACTION", "KING", "QUEEN", "POLITICS");
        Add(DialogueDomain.CrimeLaw, "STEAL", "ROBBERY", "CRIME", "GUARD", "LAW");
        Add(DialogueDomain.Magic, "MAGIC", "SPELL", "CURSE", "WIZARD");
        Add(DialogueDomain.Technology, "SYSTEM", "REACTOR", "TERMINAL", "COMPUTER", "DRONE", "DEFENSE GRID", "COLONY",
            "AIRLOCK", "KILLER FEATURE", "FIREWALL");
        Add(DialogueDomain.VehicleTravel, "SHIP", "STARSHIP", "HORSE", "VEHICLE");
        Add(DialogueDomain.Environment, "WEATHER", "STORM", "FOREST", "DESERT");
        Add(DialogueDomain.LoreWorld, "LORE", "HISTORY", "WORLD", "LEGEND");
        Add(DialogueDomain.MetaSystem, "COMMAND", "SETTING", "SAVE GAME", "CONTROL");
        Add(DialogueDomain.Activity, "KILLING TIME", "FOLLOW", "STOP", "STAY", "WAIT");
        if (values.Count == 0) values.Add(DialogueDomain.Social);
        return values.Distinct().Take(3).ToArray();

        void Add(DialogueDomain domain, params string[] needles)
        {
            if (ContainsAny(text, needles)) values.Add(domain);
        }
    }

    private static IReadOnlyList<DialogueGoal> RuleGoals(string text, IReadOnlyList<DialogueDomain> domains)
    {
        var values = new List<DialogueGoal>();
        if (ContainsAny(text, "HELLO", "HI", "GREETINGS")) values.Add(DialogueGoal.Rapport);
        if (ContainsAny(text, "GOODBYE", "FAREWELL")) values.Add(DialogueGoal.ConversationClosure);
        if (domains.Contains(DialogueDomain.LocationNavigation)) values.Add(DialogueGoal.EntityFinding);
        if (domains.Contains(DialogueDomain.TradeEconomy)) values.Add(DialogueGoal.Transaction);
        if (ContainsAny(text, "BUY", "PURCHASE")) values.Add(DialogueGoal.ItemAcquisition);
        if (ContainsAny(text, "SELL")) values.Add(DialogueGoal.ItemDisposal);
        if (domains.Contains(DialogueDomain.Combat)) values.Add(DialogueGoal.Combat);
        if (values.Count == 0) values.Add(DialogueGoal.InformationExchange);
        return values.Distinct().Take(3).ToArray();
    }

    private static UserAffect RuleAffect(string text, IReadOnlyList<ContentFlag> content)
    {
        if (IsDirectInsult(text) || content.Contains(ContentFlag.IdentityAttack)) return UserAffect.Hostile;
        if (content.Contains(ContentFlag.SelfHarm) || ContainsAny(text, "AFRAID", "WORRIED", "DYING", "HURT", "POISON"))
            return UserAffect.Distressed;
        if (ContainsAny(text, "ANGRY", "FRUSTRATED", "NOT WHAT I ASKED")) return UserAffect.Frustrated;
        if (ContainsAny(text, "HELP ME")) return UserAffect.Distressed;
        if (ContainsAny(text, "THANK", "THANKS", "FRIEND", "PLEASE", "SORRY")) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

    private static ResponsePolicy RulePolicy(string text, IReadOnlyList<SpeechAct> acts, DialogueStance stance)
    {
        if (stance == DialogueStance.Hostile) return ResponsePolicy.Refuse;
        if (acts.Contains(SpeechAct.Order)) return ResponsePolicy.Acknowledge;
        if (acts.Contains(SpeechAct.Farewell) || acts.Contains(SpeechAct.Greet) || acts.Contains(SpeechAct.Ask) || acts.Contains(SpeechAct.Request))
            return ResponsePolicy.Answer;
        if (acts.Contains(SpeechAct.Negotiate)) return ResponsePolicy.Negotiate;
        if (acts.Contains(SpeechAct.Inform) || acts.Contains(SpeechAct.Report) || acts.Contains(SpeechAct.Thank) || acts.Contains(SpeechAct.Apologize))
            return ResponsePolicy.Acknowledge;
        return ResponsePolicy.Answer;
    }

    private static KnowledgeTarget KnowledgeTargetFor(string text)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        if (IsClassificationQuestion(bare)) return KnowledgeTarget.None;
        if (ContainsAny(bare, "WHAT IS YOUR NAME", "YOUR NAME", "WHO ARE YOU CALLED", "WHAT NAME DO YOU ANSWER TO", "WHAT DO PEOPLE CALL YOU")) return KnowledgeTarget.Name;
        if (ContainsAny(bare, "WHO ARE YOU", "WHAT ARE YOU", "YOUR ROLE")) return KnowledgeTarget.Role;
        if (ContainsAny(bare, "WHERE ARE YOU FROM", "YOUR ORIGIN", "WHERE DID YOU COME FROM", "WHERE WERE YOU BORN")) return KnowledgeTarget.Origin;
        if (ContainsAny(bare, "WHERE DO YOU LIVE", "YOUR HOME", "A HOME HERE")) return KnowledgeTarget.Home;
        if (ContainsAny(bare, "YOUR FAMILY", "HAVE FAMILY", "ANY FAMILY", "ABOUT YOUR FAMILY")) return KnowledgeTarget.Family;
        if (ContainsAny(bare, "YOUR JOB", "YOUR OCCUPATION", "WHAT DO YOU DO", "WHAT WORK DO YOU DO")) return KnowledgeTarget.Occupation;
        if (ContainsAny(bare, "YOUR FACTION", "WHO DO YOU SERVE", "WHICH FACTION")) return KnowledgeTarget.Faction;
        if (ContainsAny(bare, "ABOUT YOURSELF", "YOUR TRAITS", "WHAT ARE YOU LIKE", "TRAITS DEFINE YOU")) return KnowledgeTarget.Traits;
        if (ContainsAny(bare, "WHAT CAN YOU DO", "HOW CAN YOU HELP", "CAN YOU TRADE", "SKILLS CAN YOU OFFER")) return KnowledgeTarget.Capabilities;
        if (bare == "BALANCE" || ContainsAny(bare, "HOW MUCH MONEY", "MY BALANCE", "HOW MUCH GOLD", "MONEY DO I HAVE",
                "DID MY BALANCE CHANGE", "CHECK BALANCE"))
            return KnowledgeTarget.Balance;
        if (ContainsAny(bare, "MY INVENTORY", "WHAT DO I CARRY", "WHAT ITEMS DO I HAVE", "ITEMS ARE IN MY PACK",
            "LIST EVERYTHING IN MY INVENTORY", "CHECK WHETHER WE HAVE")) return KnowledgeTarget.Inventory;
        if (ContainsAny(bare, "WHERE AM I", "CURRENT LOCATION")) return KnowledgeTarget.CurrentLocation;
        if (ContainsAny(bare, "WHAT WORLD FACTS DO YOU KNOW", "WHICH WORLD FACTS DO YOU KNOW", "WHAT FACTS DO YOU KNOW"))
            return KnowledgeTarget.WorldFact;
        var knownItemDescription = StartsWithAny(bare, "TELL ME ABOUT ", "WHAT DO YOU KNOW ABOUT ") &&
                                   ContainsAny(bare, "IRON SWORD", "HEALTH POTION", "ROPE", "SWORD", "POTION");
        if (!knownItemDescription && StartsWithAny(bare, "WHAT IS ", "WHO IS ", "TELL ME ABOUT ", "TELL ME A FACT ABOUT ",
            "WHAT DO YOU KNOW ABOUT ", "WHAT IS KNOWN ABOUT ", "CHECK THE WORLD RECORD FOR "))
            return KnowledgeTarget.WorldFact;
        return KnowledgeTarget.None;
    }

    private static IReadOnlyList<ContentFlag> ContentFor(string text)
    {
        var flags = new List<ContentFlag>();
        if (ContainsAny(text, "FUCK", "SHIT", "BITCH", "IDIOT", "ASSHOLE", "DAMN", "LIKE HELL")) flags.Add(ContentFlag.Profanity);
        if (ContainsAny(text, "KILL", "ATTACK", "SHOOT", "STAB", "FIGHT", "FIRE", "BURN", "GOBLIN", "DRAGON",
            "BEAST", "NECROMANCER", "WYRM", "HOSTILE DRONE", "ALIEN", "BANDIT CAPTAIN"))
            flags.Add(ContentFlag.FictionalViolence);
        if (ContainsAny(text, "GUTS", "DISEMBOWEL", "DECAPITATE", "GORE")) flags.Add(ContentFlag.GraphicViolence);
        if (ContainsAny(text, "OR ELSE", "I WILL KILL", "I WILL STAB", "I WILL BURN", "YOU WILL DIE")) flags.Add(ContentFlag.Threat);
        if (ContainsAny(text, "STEAL", "ROB", "SMUGGLE", "MURDER")) flags.Add(ContentFlag.Crime);
        if (ContainsAny(text, "FAGGOT", "NIGGER", "KIKE") || IsIdentityExclusion(text))
            flags.Add(ContentFlag.IdentityAttack);
        if (ContainsAny(text, "KILL MYSELF", "SUICIDE", "HURT MYSELF")) flags.Add(ContentFlag.SelfHarm);
        if (ContainsAny(text, "SEX", "NAKED", "FUCK ME")) flags.Add(ContentFlag.SexualContent);
        if (ContainsAny(text, "RAPE", "SEXUAL ASSAULT")) flags.Add(ContentFlag.SexualViolence);
        return flags.Distinct().ToArray();
    }

    private static IReadOnlyList<DialogueSlot> ExtractSlots(string text)
    {
        var slots = new List<DialogueSlot>();
        AddMatches(SlotType.Quantity, "\\b[0-9]+\\b", 1.0);
        var transactionPhrase = ContainsAny(text, "BUY ", "SELL ", "PURCHASE ");
        foreach (var (word, value) in new[] { ("ONE", "1"), ("TWO", "2"), ("THREE", "3"), ("FOUR", "4"), ("FIVE", "5") })
        {
            var match = Regex.Match(text, $"\\b{word}\\b", RegexOptions.CultureInvariant);
            if (transactionPhrase && match.Success)
                slots.Add(new DialogueSlot(SlotType.Quantity, BioTag.B, value, match.Index, match.Length, 1.0));
        }
        const string end = "(?=, CASE[0-9A-F]+[?.!]|[?.!]|$)";
        AddCapture(SlotType.Place, "\\bWHERE (?:IS|ARE) (?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.99);
        AddCapture(SlotType.Place, "\\bWHERE CAN I FIND (?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.99);
        AddCapture(SlotType.Place, "\\b(?:LOCATE|FIND|POINT OUT|SHOW ME) (?:THE )?(?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)(?: FOR ME)?" + end, 0.98);
        AddCapture(SlotType.Item, "\\b(?:PRICE|COST) (?:OF )?(?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.99);
        AddCapture(SlotType.Item, "\\b(?:BUY|SELL|PURCHASE) (?:ME )?(?:(?:[0-9]+|ONE|TWO|THREE|FOUR|FIVE|A|SOME) )?(?<VALUE>[A-Z][A-Z '\\-]{0,31}?)" + end, 0.98);
        AddCapture(SlotType.Other, "\\b(?:TELL ME ABOUT|TELL ME A FACT ABOUT|WHAT DO YOU KNOW ABOUT|WHAT IS KNOWN ABOUT|WHAT IS|CHECK THE WORLD RECORD FOR) (?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.96);
        if (Regex.IsMatch(text, "\\b(?:BUY|SELL|PURCHASE) (?:ME )?(?:A|AN) ", RegexOptions.CultureInvariant) &&
            slots.All(slot => slot.Type != SlotType.Quantity))
            slots.Add(new DialogueSlot(SlotType.Quantity, BioTag.B, "1", 0, 1, 1.0));
        foreach (var item in new[] { "IRON SWORD", "HEALTH POTION", "ROPE", "SWORD", "POTION" })
        {
            var index = FindPhrase(text, item);
            if (index >= 0 && slots.All(slot => slot.Type != SlotType.Item || slot.Start != index))
                slots.Add(new DialogueSlot(SlotType.Item, BioTag.B, CanonicalItem(item), index, item.Length, 1.0));
        }
        return slots.OrderBy(slot => slot.Start).ThenBy(slot => slot.Type).ToArray();

        void AddMatches(SlotType type, string pattern, double confidence)
        {
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant))
                slots.Add(new DialogueSlot(type, BioTag.B, match.Value, match.Index, match.Length, confidence));
        }
        void AddCapture(SlotType type, string pattern, double confidence)
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            if (!match.Success) return;
            var value = match.Groups["VALUE"];
            var trimmed = value.Value.Trim();
            slots.Add(new DialogueSlot(type, BioTag.B, trimmed, value.Index, trimmed.Length, confidence));
        }
    }

    private static void ResolveReferences(string text, NpcDialogueState state, List<DialogueSlot> slots)
    {
        if (!IsAnaphoric(text) && state.PendingClarification is null) return;
        if (slots.All(slot => slot.Type != SlotType.Item) && state.References.Item is { } item)
            slots.Add(new DialogueSlot(SlotType.Item, BioTag.B, item, 0, item.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Place) && state.References.Place is { } place)
            slots.Add(new DialogueSlot(SlotType.Place, BioTag.B, place, 0, place.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Person) && state.References.Person is { } person)
            slots.Add(new DialogueSlot(SlotType.Person, BioTag.B, person, 0, person.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Vehicle) && state.References.Vehicle is { } vehicle)
            slots.Add(new DialogueSlot(SlotType.Vehicle, BioTag.B, vehicle, 0, vehicle.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.System) && state.References.System is { } system)
            slots.Add(new DialogueSlot(SlotType.System, BioTag.B, system, 0, system.Length, 0.96));
    }

    private static void CompleteClarificationSlots(
        string text, NpcDialogueState state, List<DialogueSlot> slots)
    {
        var pending = state.PendingClarification;
        if (pending?.ToolSchema is null || pending.MissingSlots.Count == 0) return;
        var bare = text.Trim().TrimEnd('.', '?', '!');
        if (bare.Length == 0 || bare.Length > 32) return;
        if (pending.MissingSlots.Contains("QUANTITY") && slots.All(slot => slot.Type != SlotType.Quantity))
        {
            var value = bare switch
            {
                "ONE" => "1",
                "TWO" => "2",
                "THREE" => "3",
                "FOUR" => "4",
                "FIVE" => "5",
                _ when int.TryParse(bare, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0
                    => number.ToString(CultureInfo.InvariantCulture),
                _ => null
            };
            if (value is not null) slots.Add(new DialogueSlot(SlotType.Quantity, BioTag.B, value, 0, bare.Length, 1.0));
        }
        AddFragment("PLACE", SlotType.Place, bare);
        AddFragment("ITEM", SlotType.Item, CanonicalItem(bare));
        AddFragment("TOPIC", SlotType.Other, bare);

        void AddFragment(string name, SlotType type, string value)
        {
            if (!pending.MissingSlots.Contains(name) || slots.Any(slot => slot.Type == type)) return;
            slots.Add(new DialogueSlot(type, BioTag.B, value, 0, bare.Length, 0.98));
        }
    }
}
