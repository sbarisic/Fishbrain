namespace Fishbrain;

internal static class ConversationalResponder
{
    public static bool TryRespond(
        ReplyRequest request,
        DiscourseFrame frame,
        out string text,
        out DiscourseResponseAction action,
        out string? fallbackReason)
    {
        text = string.Empty;
        action = DiscourseResponseAction.None;
        fallbackReason = null;

        if (frame.Act == DiscourseAct.AskExplanation)
            return Explain(request, frame, out text, out action, out fallbackReason);
        if (frame.Act == DiscourseAct.ReferBack && frame.Subject == DialogueParticipant.Player &&
            frame.FactKind is { } recalledKind)
        {
            var remembered = request.PlayerProfile.Facts.Concat(request.State.SessionFacts).LastOrDefault(fact =>
                fact.Subject == DialogueParticipant.Player &&
                fact.Kind == recalledKind &&
                !fact.Negated);
            if (remembered is null)
            {
                text = MissingFact(recalledKind);
                action = DiscourseResponseAction.ClarifyReference;
            }
            else
            {
                text = RecallFact(remembered);
                action = DiscourseResponseAction.AcknowledgeFact;
            }
            return true;
        }
        if (frame.Act == DiscourseAct.ReferBack && frame.Subject == DialogueParticipant.Npc &&
            frame.FactKind is DialogueFactKind.Role or DialogueFactKind.Occupation &&
            frame.FactValueSpan is { } npcFact)
        {
            text = $"YES. I AM {WithArticle(npcFact.NormalizedValue)}.";
            action = DiscourseResponseAction.AcknowledgeFact;
            return true;
        }
        if (frame.FactKind is null || frame.FactValueSpan is null || frame.Subject != DialogueParticipant.Player)
            return false;

        var factValue = frame.FactValueSpan.NormalizedValue;

        if (frame.Act is DiscourseAct.Correct or DiscourseAct.RejectAssumption)
        {
            action = DiscourseResponseAction.AcknowledgeCorrection;
            text = Correction(frame, factValue);
            return true;
        }

        action = DiscourseResponseAction.AcknowledgeFact;
        text = frame.FactKind switch
        {
            DialogueFactKind.Name => $"GOOD TO MEET YOU, {factValue}.",
            DialogueFactKind.Occupation when factValue == "SCIENTIST" =>
                "A SCIENTIST? WHAT DO YOU STUDY?",
            DialogueFactKind.Occupation => $"A {factValue}? WHAT KIND OF WORK DO YOU DO?",
            DialogueFactKind.Origin => $"YOU ARE FROM {factValue}? WHAT IS IT LIKE THERE?",
            DialogueFactKind.Home => $"I WILL REMEMBER THAT YOU LIVE IN {factValue}.",
            DialogueFactKind.Role => $"I WILL REMEMBER THAT YOUR ROLE IS {factValue}.",
            DialogueFactKind.Family => $"I WILL REMEMBER WHAT YOU SAID ABOUT {factValue}.",
            DialogueFactKind.Activity => $"WHAT INTERESTS YOU MOST ABOUT {factValue}?",
            DialogueFactKind.Preference => $"I WILL REMEMBER THAT YOU PREFER {factValue}.",
            DialogueFactKind.Dislike => $"I WILL REMEMBER THAT YOU DISLIKE {factValue}.",
            DialogueFactKind.Opinion => "THAT IS AN INTERESTING WAY TO SEE IT. WHAT LED YOU THERE?",
            DialogueFactKind.Experience => "THAT SOUNDS LIKE A STORY WORTH HEARING.",
            _ => "I WILL REMEMBER THAT."
        };
        return true;
    }

    private static string MissingFact(DialogueFactKind kind) => kind switch
    {
        DialogueFactKind.Name => "YOU HAVE NOT TOLD ME YOUR NAME.",
        DialogueFactKind.Role => "YOU HAVE NOT TOLD ME YOUR ROLE.",
        DialogueFactKind.Occupation => "YOU HAVE NOT TOLD ME WHAT WORK YOU DO.",
        DialogueFactKind.Origin => "YOU HAVE NOT TOLD ME WHERE YOU ARE FROM.",
        DialogueFactKind.Home => "YOU HAVE NOT TOLD ME WHERE YOU LIVE.",
        DialogueFactKind.Family => "YOU HAVE NOT TOLD ME ABOUT YOUR FAMILY.",
        DialogueFactKind.Activity => "YOU HAVE NOT TOLD ME WHAT YOU ARE STUDYING OR WORKING ON.",
        DialogueFactKind.Preference => "YOU HAVE NOT TOLD ME WHAT YOU PREFER.",
        DialogueFactKind.Dislike => "YOU HAVE NOT TOLD ME WHAT YOU DISLIKE.",
        DialogueFactKind.Opinion => "YOU HAVE NOT TOLD ME YOUR OPINION.",
        DialogueFactKind.Experience => "YOU HAVE NOT TOLD ME ABOUT THAT EXPERIENCE.",
        _ => "YOU HAVE NOT TOLD ME THAT."
    };

    private static string RecallFact(DialogueFact fact) => fact.Kind switch
    {
        DialogueFactKind.Name => $"I REMEMBER THAT YOUR NAME IS {fact.Value}.",
        DialogueFactKind.Role => $"I REMEMBER THAT YOUR ROLE IS {fact.Value}.",
        DialogueFactKind.Occupation => $"I REMEMBER THAT YOU ARE {WithArticle(fact.Value)}.",
        DialogueFactKind.Origin => $"I REMEMBER THAT YOU ARE FROM {fact.Value}.",
        DialogueFactKind.Home => $"I REMEMBER THAT YOU LIVE IN {fact.Value}.",
        DialogueFactKind.Family => $"I REMEMBER WHAT YOU SAID ABOUT {fact.Value}.",
        DialogueFactKind.Activity => $"I REMEMBER THAT YOU ARE FOCUSED ON {fact.Value}.",
        DialogueFactKind.Preference => $"I REMEMBER THAT YOU PREFER {fact.Value}.",
        DialogueFactKind.Dislike => $"I REMEMBER THAT YOU DISLIKE {fact.Value}.",
        DialogueFactKind.Opinion => $"I REMEMBER THAT YOU THINK {fact.Value}.",
        DialogueFactKind.Experience => $"I REMEMBER THAT YOU EXPERIENCED {fact.Value}.",
        _ => "I REMEMBER THAT."
    };

    private static string WithArticle(string value)
    {
        var article = value.Length > 0 && "AEIOU".Contains(value[0]) ? "AN" : "A";
        return $"{article} {value}";
    }

    private static string Correction(DiscourseFrame frame, string factValue)
    {
        if (!frame.Negated)
        {
            return $"UNDERSTOOD. I WILL REMEMBER THE CORRECTION ABOUT {factValue}.";
        }

        return frame.FactKind switch
        {
            DialogueFactKind.Name => $"UNDERSTOOD. YOUR NAME IS NOT {factValue}.",
            DialogueFactKind.Role => $"UNDERSTOOD. YOUR ROLE IS NOT {factValue}.",
            DialogueFactKind.Occupation => "UNDERSTOOD. I WAS DESCRIBING MYSELF, NOT YOU.",
            DialogueFactKind.Origin => $"UNDERSTOOD. YOU ARE NOT FROM {factValue}.",
            DialogueFactKind.Home => $"UNDERSTOOD. YOU DO NOT LIVE IN {factValue}.",
            DialogueFactKind.Preference => $"UNDERSTOOD. YOU DO NOT PREFER {factValue}.",
            DialogueFactKind.Dislike => $"UNDERSTOOD. YOU DO NOT DISLIKE {factValue}.",
            _ => $"UNDERSTOOD. I WILL REMEMBER THE CORRECTION ABOUT {factValue}."
        };
    }

    private static bool Explain(
        ReplyRequest request,
        DiscourseFrame frame,
        out string text,
        out DiscourseResponseAction action,
        out string? fallbackReason)
    {
        action = DiscourseResponseAction.ExplainPreviousResponse;
        fallbackReason = null;
        var antecedent = frame.AntecedentUtterance is { } sequence
            ? request.Utterances.FirstOrDefault(value => value.Sequence == sequence && value.Speaker == DialogueRole.Npc)
            : null;
        if (antecedent is null)
        {
            text = "WHICH OF MY EARLIER REMARKS DO YOU MEAN?";
            action = DiscourseResponseAction.ClarifyReference;
            fallbackReason = "UNRESOLVED_UTTERANCE_REFERENCE";
            return true;
        }

        var trace = request.State.LastResponseTrace is { } candidateTrace &&
                    candidateTrace.UtteranceSequence == antecedent.Sequence
            ? candidateTrace
            : null;
        if (trace?.FallbackReason == "NO_ELIGIBLE_RESPONSE_PLAN")
        {
            text = "I WAS ASKING WHAT YOU WANTED TO DISCUSS. I SHOULD HAVE BEEN CLEARER.";
            action = DiscourseResponseAction.RepairMisunderstanding;
            fallbackReason = "REPAIRED_GENERIC_FALLBACK";
            return true;
        }

        if (trace is not null)
        {
            text = ExplainTrace(trace);
            return true;
        }

        var normalized = DialogueText.Normalize(antecedent.Text);
        text = normalized.EndsWith("?", StringComparison.Ordinal)
            ? "I WAS ASKING FOR MORE DETAIL ABOUT THAT EARLIER TOPIC."
            : "I WAS REFERRING TO THAT EARLIER TOPIC, BUT I NO LONGER HAVE ITS SEMANTIC TRACE.";
        return true;
    }

    private static string ExplainTrace(ResponseSemanticTrace trace)
    {
        var topic = trace.Topic == "GENERAL CONVERSATION" ? "OUR CONVERSATION" : trace.Topic;
        if (trace.Action == DiscourseResponseAction.AcknowledgeFact &&
            trace.ReferencedFacts.Contains(DialogueFactKind.Activity) &&
            FactTopicValue(trace.Topic, DialogueFactKind.Activity) is { } activity)
        {
            return $"I WAS ACKNOWLEDGING THAT YOU STUDY OR WORK ON {activity} AND INVITING YOU TO SAY MORE.";
        }

        return trace.Action switch
        {
            DiscourseResponseAction.AcknowledgeFact =>
                $"I WAS ACKNOWLEDGING WHAT YOU SAID ABOUT {topic}.",
            DiscourseResponseAction.AcknowledgeCorrection =>
                $"I WAS ACCEPTING YOUR CORRECTION ABOUT {topic}.",
            DiscourseResponseAction.ClarifyReference =>
                "I WAS ASKING YOU TO IDENTIFY THE EARLIER REMARK YOU MEANT.",
            DiscourseResponseAction.RepairMisunderstanding =>
                $"I WAS TRYING TO REPAIR A MISUNDERSTANDING ABOUT {topic}.",
            _ when trace.CandidateId?.StartsWith("PERSONA_", StringComparison.Ordinal) == true =>
                $"I WAS ANSWERING YOUR QUESTION ABOUT {topic}.",
            _ when trace.CandidateId?.EndsWith("_ACKNOWLEDGE", StringComparison.Ordinal) == true =>
                $"I RECOGNIZED YOUR MESSAGE AS ABOUT {topic}, BUT MY REPLY WAS TOO GENERIC.",
            _ => $"I WAS RESPONDING TO THE PART OF OUR CONVERSATION ABOUT {topic}."
        };
    }

    private static string? FactTopicValue(string topic, DialogueFactKind kind)
    {
        var prefix = kind.ToString().ToUpperInvariant() + ": ";
        return topic.StartsWith(prefix, StringComparison.Ordinal) && topic.Length > prefix.Length
            ? topic[prefix.Length..]
            : null;
    }
}
