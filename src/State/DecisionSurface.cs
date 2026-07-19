using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace Spirescry.State;

// One boot-selected boundary for the local seat's current decision. The
// bundle offer is the pilot: later migrations add other surfaces without
// adding more boot checks at their call sites.
internal interface IDecisionSurface
{
    bool BundleActive { get; }
    BundleDecision? Bundle { get; }

    DecisionSurfaceResult PickBundle(int idx);
    DecisionSurfaceResult ConfirmBundle();
    DecisionSurfaceResult SkipBundle();

    // The headless Harmony hook asks the selected adapter whether it owns
    // this completion. GUI returns false so the engine's real screen keeps
    // ownership; headless parks the offer on its stand-in and returns true.
    bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion);
}

internal sealed record BundleDecision(
    IReadOnlyList<IReadOnlyList<CardModel>> Bundles,
    bool Confirmable,
    bool Cancelable);

internal enum DecisionSurfaceError
{
    BadIndex,
    BadState,
    NotReady,
}

internal readonly record struct DecisionSurfaceResult(
    DecisionSurfaceError? Error,
    string? Message)
{
    public bool Ok => Error is null;

    public static DecisionSurfaceResult Success() => new(null, null);
    public static DecisionSurfaceResult Reject(DecisionSurfaceError error, string message) =>
        new(error, message);
}

internal static class DecisionSurface
{
    private static IDecisionSurface? _current;

    public static IDecisionSurface Current => _current
        ?? throw new InvalidOperationException("decision surface not selected at boot");

    public static void UseGui() => Select(new GuiDecisionSurface());

    public static void UseHeadless() => Select(new HeadlessDecisionSurface());

    private static void Select(IDecisionSurface adapter)
    {
        if (Interlocked.CompareExchange(ref _current, adapter, null) is not null)
            throw new InvalidOperationException("decision surface already selected");
    }
}

internal sealed class GuiDecisionSurface : IDecisionSurface
{
    private static NChooseABundleSelectionScreen? BundleScreen =>
        Screens.Top<NChooseABundleSelectionScreen>();

    public bool BundleActive => BundleScreen is not null;

    public BundleDecision? Bundle
    {
        get
        {
            if (BundleScreen is not { } screen) return null;
            return new BundleDecision(
                Screens.Bundles(screen) ?? [],
                Reflect.Field<NClickableControl>(screen, "_confirmButton")
                    is { IsEnabled: true },
                Reflect.Field<NClickableControl>(screen, "_skipButton")
                    is { IsEnabled: true });
        }
    }

    public DecisionSurfaceResult PickBundle(int idx)
    {
        if (BundleScreen is not { } screen)
            return NotReady("bundle screen not mounted");
        var nodes = Screens.BundleNodes(screen);
        if (nodes is null || nodes.Count == 0)
            return NotReady("bundle row not wired yet — retry");
        if (idx < 0 || idx >= nodes.Count)
            return BadIndex($"bundle idx {idx} out of range [0,{nodes.Count - 1}]");
        Reflect.Invoke(screen, "OnBundleClicked", nodes[idx]);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ConfirmBundle()
    {
        if (BundleScreen is not { } screen)
            return NotReady("bundle screen not mounted");
        Reflect.Invoke(screen, "ConfirmSelection", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult SkipBundle()
    {
        if (BundleScreen is not { } screen)
            return NotReady("bundle screen not mounted");
        Reflect.Invoke(screen, "CancelSelection", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion)
    {
        completion = null!;
        return false;
    }

    private static DecisionSurfaceResult BadIndex(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadIndex, message);

    private static DecisionSurfaceResult NotReady(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.NotReady, message);
}

internal sealed class HeadlessDecisionSurface : IDecisionSurface
{
    public bool BundleActive => HeadlessBundle.IsActive;

    public BundleDecision? Bundle => !HeadlessBundle.IsActive
        ? null
        : new BundleDecision(
            HeadlessBundle.Bundles,
            Confirmable: false,
            Cancelable: true);

    public DecisionSurfaceResult PickBundle(int idx) =>
        HeadlessBundle.Pick(idx) is { } message
            ? DecisionSurfaceResult.Reject(DecisionSurfaceError.BadIndex, message)
            : DecisionSurfaceResult.Success();

    public DecisionSurfaceResult ConfirmBundle() =>
        DecisionSurfaceResult.Reject(
            DecisionSurfaceError.BadState,
            "host bundle picks resolve on pick-card");

    public DecisionSurfaceResult SkipBundle()
    {
        HeadlessBundle.CancelIfActive();
        return DecisionSurfaceResult.Success();
    }

    public bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion)
    {
        completion = HeadlessBundle.Park(bundles);
        return true;
    }
}
