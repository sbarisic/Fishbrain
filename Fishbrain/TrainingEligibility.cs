namespace Fishbrain;

/// <summary>Corpus provenance and loss eligibility, never host runtime state.</summary>
internal sealed record TrainingEligibility(int Version, string Pool, bool ResponseEligible, string EpisodeId,
    string AugmentationFamily, string License, string SourceRevision, string SourceChecksum, string ExampleId,
    bool ClaimPositiveEligible = false, bool ClaimNegativeEligible = false)
{
    internal void Validate(string? response, string? rejectedResponse = null)
    {
        if (Version != 4 || Pool is not ("public" or "authored" or "focused" or "classification") ||
            new[] { EpisodeId, AugmentationFamily, License, SourceRevision, SourceChecksum, ExampleId }.Any(string.IsNullOrWhiteSpace) ||
            SourceChecksum.Length != 64 || SourceChecksum.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid conversation training provenance.");
        if (ResponseEligible && (Pool is not ("public" or "authored") || string.IsNullOrWhiteSpace(response)))
            throw new InvalidDataException("Response eligibility requires an approved public or authored response.");
        if (ResponseEligible && License is not ("PROJECT-OWNED" or "APACHE-2.0" or "CC-BY-4.0"))
            throw new InvalidDataException("Response source license is not approved.");
        if (ClaimPositiveEligible && (Pool != "authored" || !ResponseEligible) ||
            ClaimNegativeEligible && (Pool is not ("authored" or "focused") || string.IsNullOrWhiteSpace(rejectedResponse)))
            throw new InvalidDataException("Claim supervision requires an explicit authored target; public responses are response-only.");
    }
}
