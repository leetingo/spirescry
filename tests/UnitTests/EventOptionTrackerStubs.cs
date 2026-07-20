namespace MegaCrit.Sts2.Core.Multiplayer.Game
{
    internal sealed class EventSynchronizer;
}

namespace Spirescry.State
{
    using MegaCrit.Sts2.Core.Multiplayer.Game;

    internal static class EventSync
    {
        public static bool SharedVotePending(EventSynchronizer? synchronizer) => false;
    }
}
