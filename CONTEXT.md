# Spirescry Play Control

Terms for assigning control of a Slay the Spire 2 multiplayer participant to
either its human player or an agent through Spirescry.

## Language

**Local seat**:
The multiplayer participant owned by the player running this mod. It is the
only seat that this Spirescry instance may observe and control.
_Avoid_: Host seat, remote seat, arbitrary player

**Agent takeover**:
Persistent delegation of the local seat's decisions and actions from its human
player to an agent until the local player reclaims control; every other seat
remains controlled by its own player.
_Avoid_: Remote takeover, host control

**Control owner**:
The human player or agent with exclusive authority to act for the local seat.
The human may reclaim ownership at any time, including during agent takeover.
_Avoid_: Concurrent controller, shared control

**Decision surface**:
The boot-selected adapter through which Spirescry enumerates the local seat's
current options, acts on one, and reaches that choice's completion. The GUI
adapter uses the real screens; the headless adapter uses the existing
stand-ins and parking hooks.
_Avoid_: Per-call boot fork, headless screen emulation
