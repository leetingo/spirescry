namespace Spirescry;

// Stable machine-readable vocabulary returned in the bridge's `err` field.
// Dispatch and transport paths refer to these names so a typo cannot create
// an undocumented one-off code that breaks the agent retry grammar.
public static class RejectionCodes
{
    public const string BadRequest = "bad_request";
    public const string BadPhase = "bad_phase";
    public const string BadIndex = "bad_index";
    public const string BadTarget = "bad_target";
    public const string NotReady = "not_ready";
    public const string NotPlayable = "not_playable";
    public const string NotEnoughGold = "not_enough_gold";
    public const string NotEnoughEnergy = "not_enough_energy";
    public const string NotEnoughStars = "not_enough_stars";
    public const string RunExists = "run_exists";
    public const string StaleState = "stale_state";
    public const string ExternalChange = "external_change";
    public const string ResolutionPartial = "resolution_partial";
    public const string ResolutionFailed = "resolution_failed";
    public const string NotFound = "not_found";
    public const string Internal = "internal";

    public static readonly string[] All =
    {
        BadRequest, BadPhase, BadIndex, BadTarget, NotReady, NotPlayable,
        NotEnoughGold, NotEnoughEnergy, NotEnoughStars, RunExists,
        StaleState, ExternalChange, ResolutionPartial, ResolutionFailed,
        NotFound, Internal,
    };
}
