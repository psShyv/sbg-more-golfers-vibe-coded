using System.Reflection;
using HarmonyLib;
using Mirror;

namespace MoreGolfers;

// ---------------------------------------------------------------------------------------------
// WHAT THIS DOES
//
// GeneratedNetworkCode.cs (Mirror's own weaver-generated serialization glue) settles what
// EntityStateMessage actually is: it's Mirror.EntityStateMessage, a STOCK Mirror message, not
// anything specific to this game. Its wire format is just {netId, payload} - payload is opaque
// bytes - which is Mirror's generic per-NetworkIdentity state envelope: the combined OnSerialize
// output of every NetworkBehaviour on that object, sent as one message per synced GameObject.
// There are also EntityStateMessageUnreliableBaseline/-Delta variants carrying an extra
// baselineTick byte - the classic baseline-then-delta pattern for unreliable-channel state sync,
// which is what a component syncing continuously-changing data (rigidbody position/velocity,
// specifically) would use rather than the plain reliable variant.
//
// Two independent, fully-verified NetworkBehaviours contribute to this per-cart envelope:
//
// 1. GolfCartMovement and GolfCartInfo, each with their own [SyncVar] fields (confirmed directly
//    in their decompiled source: isAccelerating/isBraking/steering on Movement; driver seat
//    reservation and team on Info), synced through Mirror's ordinary ("reliable") SyncVar
//    mechanism and gated by their own inherited syncInterval.
//
// 2. Entity.NetworkRigidbody (type NetworkRigidbodyUnreliable, confirmed a NetworkBehaviour via
//    its use of isServer/isClient/isOwned/syncDirection in Entity.HasRigidbodyAuthority) - whose
//    very name and the Baseline/Delta message pattern above both point at it as the source of the
//    UNRELIABLE variant specifically. This is inferred rather than read directly (its own source
//    still hasn't been supplied), but the inference no longer requires guessing at anything
//    private: Entity.Awake() is fully verified, sets NetworkRigidbody directly in its own body,
//    and (via its last line, a call to a compiler-generated local function) sets PredictedGolfCart
//    by the time Awake returns - so a Postfix on Entity.Awake can gate on the public IsGolfCart
//    property and only touch NetworkRigidbody's syncInterval for entities that are actually golf
//    carts, using the same inherited Mirror field as the other two patches, on a class this file
//    already has full source for.
//
// Entity.PredictedGolfCart and PhysicsManager remain untouched, same as before - genuinely unseen
// source, no safe lever available.
// ---------------------------------------------------------------------------------------------

internal static class NetworkSyncIntervalScaling
{
    // NetworkBehaviour.syncInterval belongs to Mirror, not this game, so it's resolved by
    // reflection and gated by IsAvailable rather than referenced directly - the same reasoning as
    // every BUpdate patch this session: if this Mirror version doesn't expose it under this exact
    // name, both patches below should decline quietly rather than assume and risk throwing on
    // every single cart spawn.
    private static readonly FieldInfo SyncIntervalField =
        AccessTools.Field(typeof(NetworkBehaviour), "syncInterval");

    // Entity.NetworkRigidbody is declared as NetworkRigidbodyUnreliable, which turns out to live
    // in a separate assembly (Mirror.Components) that this project doesn't reference - confirmed
    // by a real build failure (CS0012) the first time this was written as a direct typed access.
    // Fetched by reflection instead: PropertyInfo.GetValue returns plain object, so the compiler
    // never needs to resolve NetworkRigidbodyUnreliable at all, only NetworkBehaviour (Mirror.dll,
    // already referenced throughout this project) once the result is cast below. This is a
    // strictly better fix than adding a <Reference> for a specific DLL path that may not match
    // every install - it removes the hard dependency rather than relocating it.
    private static readonly PropertyInfo EntityNetworkRigidbodyProperty =
        AccessTools.Property(typeof(Entity), "NetworkRigidbody");

    private static bool _hasLoggedUnavailable;

    internal static bool IsAvailable
    {
        get
        {
            if (SyncIntervalField != null && EntityNetworkRigidbodyProperty != null)
            {
                return true;
            }

            // Shared by all three Prepare() calls below; only warn once rather than three times
            // about the same missing member.
            if (!_hasLoggedUnavailable)
            {
                _hasLoggedUnavailable = true;
                string missing = SyncIntervalField == null
                    ? "NetworkBehaviour.syncInterval"
                    : "Entity.NetworkRigidbody";
                MoreGolfersPlugin.Logger?.LogWarning(
                    $"Skipping golf cart sync-interval scaling: {missing} could not be resolved. " +
                    "Golf carts will sync at their unmodified rate.");
            }

            return false;
        }
    }

    internal static void Apply(NetworkBehaviour behaviour)
    {
        float scale = MoreGolfersPlugin.GetNetworkSyncIntervalScale();
        if (scale <= 1f)
        {
            // Vanilla player count or below: change nothing, byte for byte, from what shipped.
            return;
        }

        // Scaled up from whatever the designers already set, rather than assumed outright -
        // multiplying preserves their original tuning intent (Movement and Info are free to have
        // been given different baseline intervals) instead of overwriting it with one guessed
        // number.
        float currentInterval = (float)SyncIntervalField.GetValue(behaviour);
        SyncIntervalField.SetValue(behaviour, currentInterval * scale);
    }

    // Returns null if entity has no rigidbody sync component, exactly like the property itself
    // would - callers already null-check the typed property elsewhere in this file, so this
    // preserves that contract rather than silently changing behaviour at the reflection boundary.
    internal static NetworkBehaviour GetEntityNetworkRigidbody(Entity entity)
    {
        return EntityNetworkRigidbodyProperty.GetValue(entity) as NetworkBehaviour;
    }
}

[HarmonyPatch(typeof(GolfCartMovement), "Awake")]
static class PatchGolfCartMovementAwake
{
    static bool Prepare() => NetworkSyncIntervalScaling.IsAvailable;

    static void Postfix(GolfCartMovement __instance) => NetworkSyncIntervalScaling.Apply(__instance);
}

[HarmonyPatch(typeof(GolfCartInfo), "Awake")]
static class PatchGolfCartInfoAwake
{
    static bool Prepare() => NetworkSyncIntervalScaling.IsAvailable;

    static void Postfix(GolfCartInfo __instance) => NetworkSyncIntervalScaling.Apply(__instance);
}

[HarmonyPatch(typeof(Entity), "Awake")]
static class PatchEntityAwake
{
    static bool Prepare() => NetworkSyncIntervalScaling.IsAvailable;

    static void Postfix(Entity __instance)
    {
        // Scoped to golf carts specifically: IsGolfCart is public and confirmed set by the time
        // Awake() returns (Awake's own last line calls the local function that sets it), and every
        // "EntityStateMessage ... without authority" warning in the supplied logs names
        // "Golf Cart(Clone)" specifically.
        if (!__instance.IsGolfCart)
        {
            return;
        }

        // Fetched by reflection rather than __instance.NetworkRigidbody directly - see the comment
        // on EntityNetworkRigidbodyProperty above for why. Can legitimately be null even for a golf
        // cart - Entity.IsSimulatingRigidbody has its own `NetworkRigidbody == null` branch - so
        // this guards rather than assumes.
        NetworkBehaviour networkRigidbody = NetworkSyncIntervalScaling.GetEntityNetworkRigidbody(__instance);
        if (networkRigidbody == null)
        {
            return;
        }

        NetworkSyncIntervalScaling.Apply(networkRigidbody);
    }
}
