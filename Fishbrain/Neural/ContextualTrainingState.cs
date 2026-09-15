namespace Fishbrain.Neural;

internal enum ContextualPhase { MaskedLanguage, JointUnderstanding, JointRealization, DecoderPolish }

/// <summary>Checkpoint data only; no optimizer execution code belongs to inference.</summary>
internal sealed record ContextualTrainingState(ulong RandomState, Dictionary<string, float[]> FirstMoments,
    Dictionary<string, float[]> SecondMoments, Dictionary<string, int> ParameterUpdates);

internal interface IContextualTrainingState
{
    Dictionary<string, Tensor> Parameters { get; }
    Dictionary<string, float[]> FirstMoments { get; }
    Dictionary<string, float[]> SecondMoments { get; }
    Dictionary<string, int> ParameterUpdates { get; }
    DeterministicRandom Random { get; }
}

internal static class ContextualSchedule
{
    public static ContextualPhase Phase(int step) => step < 40_000 ? ContextualPhase.MaskedLanguage
        : step >= 220_000 ? ContextualPhase.DecoderPolish
        : (step - 40_000) % 10 < 7 ? ContextualPhase.JointUnderstanding : ContextualPhase.JointRealization;
}
