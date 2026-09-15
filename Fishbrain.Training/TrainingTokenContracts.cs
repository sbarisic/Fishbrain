using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal sealed record TeachingRecovery(
    string ProjectPath,
    string CorpusDirectory,
    string CheckpointPath,
    int PlannedSteps,
    int UntilStep)
{
    public string TeachCommand(int untilStep) =>
        $"dotnet run -c Release --project {Quote(ProjectPath)} -- teach {Quote(CorpusDirectory)} " +
        $"{Quote(CheckpointPath)} --planned {PlannedSteps} --until {untilStep}";

    public string EvaluateCommand() =>
        $"dotnet run -c Release --project {Quote(ProjectPath)} -- evaluate " +
        $"{Quote(Path.Combine(CorpusDirectory, "test.jsonl"))} {Quote(CheckpointPath)}";

    internal static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
internal enum TrainingTask { Language, Perception, Tool }

[Flags]
internal enum PerceptionFields { None = 0, Intent = 1, Affect = 2, Expected = 4, All = Intent | Affect | Expected }

internal sealed record TrainingSample(
    int[] Tokens,
    int PositionOffset,
    int FirstTargetIndex,
    TrainingTask Task = TrainingTask.Language,
    string Bucket = "",
    string Source = "synthetic",
    TurnPerception? PerceptionTarget = null,
    string Family = "",
    PerceptionFields TargetFields = PerceptionFields.All,
    int? UnlikelihoodTargetIndex = null,
    int? UnlikelihoodToken = null,
    double UnlikelihoodWeight = 0.0);


internal static class DialogueKeys
{
    public static string Catalog(DialogueIntent intent, ResponseTone tone) => $"{intent}|{tone}";

    public static string StateInput(string input, NpcState state) =>
        $"{state.Rapport}|{(int)state.Mood}|{(int)state.LastIntent}|{(int)state.LastAffect}|" +
        $"{(int)state.ActiveTopic}|{(int)state.ActiveGoal}|{input}";

    public static string Example(
        string input,
        NpcState state,
        TurnPerception perception,
        TurnDecision decision,
        ResponseTone tone) =>
        $"{StateInput(input, state)}|{(int)perception.Intent}|{(int)perception.Affect}|" +
        $"{perception.ResponseExpected}|{(int)decision.Action}|{(int)tone}";
}
