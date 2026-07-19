namespace Spirescry;

// Compatibility facade for the stable names returned in the bridge's `err`
// field. ProtocolVocabulary owns their values and order; existing dispatch
// and transport callers keep these names during the expand step.
public static class RejectionCodes
{
    public const string BadRequest = ProtocolVocabulary.Rejections.BadRequest;
    public const string BadPhase = ProtocolVocabulary.Rejections.BadPhase;
    public const string BadIndex = ProtocolVocabulary.Rejections.BadIndex;
    public const string BadTarget = ProtocolVocabulary.Rejections.BadTarget;
    public const string BadState = ProtocolVocabulary.Rejections.BadState;
    public const string NotReady = ProtocolVocabulary.Rejections.NotReady;
    public const string NotPlayable = ProtocolVocabulary.Rejections.NotPlayable;
    public const string NotEnoughGold = ProtocolVocabulary.Rejections.NotEnoughGold;
    public const string NotEnoughEnergy = ProtocolVocabulary.Rejections.NotEnoughEnergy;
    public const string NotEnoughStars = ProtocolVocabulary.Rejections.NotEnoughStars;
    public const string RunExists = ProtocolVocabulary.Rejections.RunExists;
    public const string StaleState = ProtocolVocabulary.Rejections.StaleState;
    public const string ExternalChange = ProtocolVocabulary.Rejections.ExternalChange;
    public const string ResolutionPartial = ProtocolVocabulary.Rejections.ResolutionPartial;
    public const string ResolutionFailed = ProtocolVocabulary.Rejections.ResolutionFailed;
    public const string NotFound = ProtocolVocabulary.Rejections.NotFound;
    public const string Internal = ProtocolVocabulary.Rejections.Internal;

    public static readonly string[] All = ProtocolVocabulary.Rejections.All.ToArray();
}
