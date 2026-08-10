using System.Security.Cryptography;
using System.Text;

namespace Fishbrain;

internal enum StructuredTrainingMode
{
    All,
    Discourse,
    Ranking
}

internal sealed class TrainingCurriculumSampler
{
    private readonly int _seed;
    private readonly TrainingExample[][] _allFamilies;
    private readonly TrainingExample[][] _operationalFamilies;
    private readonly TrainingExample[][] _naturalOperationalFamilies;
    private readonly IReadOnlyDictionary<string, TrainingExample[][]> _rareOperationalFamilies;
    private readonly TrainingExample[][] _toolNullContrastFamilies;
    private readonly TrainingExample[][] _factFamilies;
    private readonly TrainingExample[][] _positiveFactFamilies;
    private readonly TrainingExample[][] _nullFactFamilies;
    private readonly TrainingExample[][] _referenceFamilies;
    private readonly TrainingExample[][] _positiveReferenceFamilies;
    private readonly TrainingExample[][] _nullReferenceFamilies;
    private readonly TrainingExample[][] _banterFamilies;
    private readonly TrainingExample[][] _negativeFamilies;

    public TrainingCurriculumSampler(
        IReadOnlyList<TrainingExample> examples,
        int seed,
        bool requireAllBands = true)
    {
        _seed = seed;
        _allFamilies = Families(examples);
        _operationalFamilies = _allFamilies.Where(family => !IsDiscourse(family[0])).ToArray();
        _naturalOperationalFamilies = _operationalFamilies
            .SelectMany(family => Enumerable.Repeat(family, family.Length))
            .ToArray();
        _rareOperationalFamilies = new[]
        {
            "AFFECT", "CONTENT", "DOMAIN", "GOAL", "KNOWLEDGE", "POLICY", "SLOT", "SPEECH", "STANCE", "TOOL"
        }.ToDictionary(
            head => head,
            head => RareOperationalFamilies(_operationalFamilies, head),
            StringComparer.Ordinal);
        _toolNullContrastFamilies = _operationalFamilies.Where(family => family.Any(IsToolNullContrast)).ToArray();
        _factFamilies = Band("PROJECT_DISCOURSE_FACTS");
        _referenceFamilies = Band("PROJECT_DISCOURSE_REFERENCES");
        _banterFamilies = Band("PROJECT_CONVERSATION");
        _negativeFamilies = Band("PROJECT_DISCOURSE_NEGATIVES");
        _positiveFactFamilies = PointerFamilies(_factFamilies, positive: true);
        _nullFactFamilies = PointerFamilies(_factFamilies, positive: false);
        _positiveReferenceFamilies = PointerFamilies(_referenceFamilies, positive: true);
        _nullReferenceFamilies = PointerFamilies(_referenceFamilies, positive: false);
        if (_operationalFamilies.Length == 0)
        {
            throw new InvalidDataException("The curriculum requires operational families.");
        }

        if (requireAllBands && (_factFamilies.Length == 0 ||
            _referenceFamilies.Length == 0 || _banterFamilies.Length == 0 ||
            _negativeFamilies.Length == 0))
        {
            throw new InvalidDataException("The curriculum requires operational and all four discourse bands.");
        }

        _factFamilies = MissingFallback(_factFamilies);
        _referenceFamilies = MissingFallback(_referenceFamilies);
        _banterFamilies = MissingFallback(_banterFamilies);
        _negativeFamilies = MissingFallback(_negativeFamilies);

        TrainingExample[][] Band(string source) =>
            _allFamilies.Where(family => family[0].Source == source).ToArray();
        TrainingExample[][] MissingFallback(TrainingExample[][] families) =>
            families.Length == 0 ? _operationalFamilies : families;
        static TrainingExample[][] PointerFamilies(TrainingExample[][] families, bool positive) => families
            .Where(family => (family[0].Discourse.AntecedentUtterance is not null) == positive)
            .ToArray();
    }

    public TrainingExample[] BaseStructuredBatch(long batchIndex, int size = 32)
    {
        var result = new TrainingExample[size];
        var firstSelection = checked(batchIndex * size);
        for (var offset = 0; offset < size; offset++)
        {
            var selection = firstSelection + offset;
            result[offset] = selection % 4 == 3
                ? SelectDiscourse(selection / 4)
                : SelectOperational(selection - (selection + 1) / 4, allowRareFocus: true);
        }

        return result;
    }

    public TrainingExample[] DiscourseBatch(long batchIndex, int size = 32)
    {
        var result = new TrainingExample[size];
        var firstSelection = checked(batchIndex * size);
        for (var offset = 0; offset < size; offset++)
        {
            result[offset] = SelectDiscourse(firstSelection + offset);
        }

        return result;
    }

    public TrainingExample[] CorrectiveOperationalBatch(long batchIndex, int size = 32)
    {
        var result = new TrainingExample[size];
        var firstSelection = checked(batchIndex * size);
        for (var offset = 0; offset < size; offset++)
        {
            result[offset] = SelectOperational(firstSelection + offset, allowRareFocus: true);
        }

        return result;
    }

    public TrainingExample[] RankingBatch(long batchIndex, int size = 32)
    {
        var result = new TrainingExample[size];
        for (var offset = 0; offset < size; offset++)
        {
            var selection = checked(batchIndex + (long)offset * 104_729);
            result[offset] = Select(_allFamilies, selection, 7919);
        }

        return result;
    }

    internal static bool IsDiscourse(TrainingExample example) =>
        example.Source.StartsWith("PROJECT_DISCOURSE", StringComparison.Ordinal) ||
        example.Source == "PROJECT_CONVERSATION";

    internal static string DiscourseQuotaBand(long selection) => (selection % 10) switch
    {
        <= 3 => "FACTS",
        <= 6 => "REFERENCES",
        <= 8 => "BANTER",
        _ => "HARD_NEGATIVES"
    };

    internal static string RareOperationalFocus(long rareSelection)
    {
        var slot = (int)(rareSelection % 90);
        return slot switch
        {
            0 => "AFFECT",
            1 or 2 => "CONTENT",
            3 => "DOMAIN",
            4 => "GOAL",
            5 => "KNOWLEDGE",
            >= 6 and < 68 => "POLICY",
            68 => "SLOT",
            69 or 70 => "SPEECH",
            71 => "STANCE",
            _ => "TOOL"
        };
    }

    private TrainingExample SelectDiscourse(long selection)
    {
        var band = DiscourseQuotaBand(selection);
        var quotaPosition = (int)(selection % 10);
        var local = selection / 10 * (band switch
        {
            "FACTS" => 4,
            "REFERENCES" => 3,
            "BANTER" => 2,
            _ => 1
        }) + (band switch
        {
            "FACTS" => selection % 10,
            "REFERENCES" => selection % 10 - 4,
            "BANTER" => selection % 10 - 7,
            _ => 0
        });
        return band switch
        {
            "FACTS" => SelectPointerBalanced(
                quotaPosition <= 1 ? _positiveFactFamilies : _nullFactFamilies,
                _factFamilies,
                local,
                quotaPosition <= 1 ? 1009 : 1013),
            "REFERENCES" => SelectPointerBalanced(
                quotaPosition <= 5 ? _positiveReferenceFamilies : _nullReferenceFamilies,
                _referenceFamilies,
                local,
                quotaPosition <= 5 ? 1877 : 1889),
            "BANTER" => Select(_banterFamilies, local, 2027),
            _ => Select(_negativeFamilies, local, 3253)
        };
    }

    private TrainingExample SelectPointerBalanced(
        TrainingExample[][] preferred,
        TrainingExample[][] fallback,
        long selection,
        int salt) => Select(preferred.Length == 0 ? fallback : preferred, selection, salt);

    private TrainingExample SelectOperational(long selection, bool allowRareFocus)
    {
        var rare = allowRareFocus && selection % 5 == 4;
        var local = rare ? selection / 5 : selection - selection / 5;
        if (!rare)
        {
            return Select(_naturalOperationalFamilies, local, 4201);
        }

        var focus = RareOperationalFocus(local);
        var preferred = _rareOperationalFamilies[focus];
        var focusSlot = (int)(local % 90);
        if (focus == "TOOL" && _toolNullContrastFamilies.Length > 0 && focusSlot % 2 == 1)
        {
            return Select(_toolNullContrastFamilies, local / 90, 4253 + focusSlot * 2);
        }

        var families = preferred.Length == 0 ? _operationalFamilies : preferred;
        return Select(families, local / 90, 4253 + focusSlot * 2);
    }

    internal static bool IsToolNullContrast(TrainingExample example)
    {
        if (example.ToolSchema != "NONE" || !example.SupervisedHeads.Contains("tool"))
        {
            return false;
        }

        var input = example.Input.AsSpan().TrimStart();
        return input.StartsWith("BUY ", StringComparison.OrdinalIgnoreCase) ||
               input.StartsWith("SELL ", StringComparison.OrdinalIgnoreCase);
    }

    private TrainingExample Select(TrainingExample[][] families, long selection, int salt)
    {
        var epoch = selection / families.Length;
        var position = (int)(selection % families.Length);
        var familyIndex = PermutationIndex(families.Length, epoch, position, _seed ^ salt);
        var family = families[familyIndex];
        return family[(int)(epoch % family.Length)];
    }

    private static TrainingExample[][] Families(IReadOnlyList<TrainingExample> examples) => examples
        .GroupBy(example => example.SemanticFamilyId, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => group.OrderBy(example => example.Input, StringComparer.Ordinal).ToArray())
        .ToArray();

    private static TrainingExample[][] RareOperationalFamilies(
        TrainingExample[][] families,
        string head)
    {
        var labelled = families
            .Select(family => (Family: family, Labels: OperationalLabels(family[0]).ToArray()))
            .ToArray();
        var support = labelled.SelectMany(item => item.Labels)
            .GroupBy(label => label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var prefix = head + ":";
        var headSupport = support.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (headSupport.Length == 0) return [];
        var maximum = headSupport.Max(item => item.Value);
        var rareLabels = headSupport.Where(item => item.Value * 2 <= maximum)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        return labelled
            .Where(item => item.Labels.Any(rareLabels.Contains))
            .Select(item => item.Family)
            .ToArray();

        static IEnumerable<string> OperationalLabels(TrainingExample example)
        {
            foreach (var value in example.SpeechActs) yield return $"SPEECH:{value}";
            foreach (var value in example.Domains) yield return $"DOMAIN:{value}";
            foreach (var value in example.Goals) yield return $"GOAL:{value}";
            yield return $"AFFECT:{example.Affect}";
            yield return $"STANCE:{example.Stance}";
            yield return $"POLICY:{example.Policy}";
            foreach (var value in example.ContentFlags) yield return $"CONTENT:{value}";
            foreach (var slot in example.Slots) yield return $"SLOT:{slot.Type}";
            yield return $"TOOL:{example.ToolSchema}";
            yield return $"KNOWLEDGE:{example.KnowledgeTarget}";
        }
    }

    private static int PermutationIndex(int count, long epoch, int position, int seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{epoch}"));
        var multiplier = (int)(BitConverter.ToUInt32(hash, 0) % (uint)count) | 1;
        while (GreatestCommonDivisor(multiplier, count) != 1)
        {
            multiplier += 2;
            if (multiplier >= count)
            {
                multiplier = 1;
            }
        }

        var offset = (int)(BitConverter.ToUInt32(hash, 4) % (uint)count);
        return (int)(((long)multiplier * position + offset) % count);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }
}
