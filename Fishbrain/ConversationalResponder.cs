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
        if (frame.Act == DiscourseAct.ReferBack && frame.FactKind == DialogueFactKind.Occupation)
        {
            var remembered = request.PlayerProfile.Facts.Concat(request.State.SessionFacts).LastOrDefault(fact =>
                fact.Subject == DialogueParticipant.Player &&
                fact.Kind == DialogueFactKind.Occupation &&
                !fact.Negated);
            if (remembered is null)
            {
                text = "YOU HAVE NOT TOLD ME WHAT WORK YOU DO.";
                action = DiscourseResponseAction.ClarifyReference;
            }
            else
            {
                text = $"I REMEMBER THAT YOU ARE A {remembered.Value}.";
                action = DiscourseResponseAction.AcknowledgeFact;
            }
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
            DialogueFactKind.Activity => $"HOW IS {factValue} GOING?",
            DialogueFactKind.Preference => $"I WILL REMEMBER THAT YOU PREFER {factValue}.",
            DialogueFactKind.Dislike => $"I WILL REMEMBER THAT YOU DISLIKE {factValue}.",
            DialogueFactKind.Opinion => "THAT IS AN INTERESTING WAY TO SEE IT. WHAT LED YOU THERE?",
            DialogueFactKind.Experience => "THAT SOUNDS LIKE A STORY WORTH HEARING.",
            _ => "I WILL REMEMBER THAT."
        };
        return true;
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
            _ => $"I WAS RESPONDING TO THE PART OF OUR CONVERSATION ABOUT {topic}."
        };
    }
}
