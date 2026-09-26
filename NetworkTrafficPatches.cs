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
// 2. Entity.PredictedGolfCart (type Mirror.PredictedGolfCart) - NOT Entity.NetworkRigidbody as
//    previously assumed. With full source now available, GolfCartInfo.cs settles this directly:
//    GolfCartInfo.SetMovementSyncDirectionInternal, called every time the driver seat changes,
//    sets syncDirection on AsEntity.PredictedGolfCart and Movement together and nothing else -
//    there is no reference to Entity.NetworkRigidbody anywhere in GolfCartInfo or
//    GolfCartMovement. Entity.IsSimulatingRigidbody() confirms the two are mutually exclusive by
//    design: it checks PredictedGolfCart first and returns before ever consulting
//    NetworkRigidbody. The NetworkRigidbodyUnreliable type Entity fetches generically is, per
//    PlayerInfo.cs, actually the *player's* rigidbody sync component, not the cart's - so the
//    previous version of this patch (gated on Entity.NetworkRigidbody) was very likely a no-op
//    for every golf cart, since GetComponent<NetworkRigidbodyUnreliable>() on a cart has nothing
//    to find.
//
//    PredictedGolfCart is also the actual worst offender of the two, not a marginal add-on: its
//    OnValidate() hard-codes `syncInterval = 0f`, and OnSerialize writes position, rotation,
//    linear velocity, angular velocity, and all four wheel speeds - every server tick, uncapped,
//    per cart. That baked-in 0 means it can't be scaled the same way as Movement/Info
//    (0 * scale is still 0); Apply() below special-cases a zero baseline by seeding it from
//    NetworkServer.sendInterval (1 / NetworkServer.tickRate) - the actual cadence "uncapped"
//    already resolves to - rather than inventing a constant, so the "change nothing at vanilla
//    player count" guarantee still holds.
//
// PhysicsManager remains untouched - genuinely unseen source, no safe lever available.
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

    // Entity.PredictedGolfCart is declared as Mirror.PredictedGolfCart, which lives in a separate
    // assembly (Mirror.Components) that this project doesn't reference - confirmed by a real
    // build failure (CS0012) the first time an equivalent access (then targeting
    // NetworkRigidbodyUnreliable) was written as a direct typed reference. Fetched by reflection
    // instead: PropertyInfo.GetValue returns plain object, so the compiler never needs to resolve
    // PredictedGolfCart at all, only NetworkBehaviour (Mirror.dll, already referenced throughout
    // this project) once the result is cast below. This is a strictly better fix than adding a
    // <Reference> for a specific DLL path that may not match every install - it removes the hard
    // dependency rather than relocating it.
    private static readonly PropertyInfo EntityPredictedGolfCartProperty =
        AccessTools.Property(typeof(Entity), "PredictedGolfCart");

    private static bool _hasLoggedUnavailable;

    internal static bool IsAvailable
    {
        get
        {
            if (SyncIntervalField != null && EntityPredictedGolfCartProperty != null)
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
                    : "Entity.PredictedGolfCart";
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
        // multiplying preserves their original tuning intent (Movement, Info, and PredictedGolfCart
        // are free to have been given different baseline intervals) instead of overwriting it with
        // one guessed number.
        //
        // PredictedGolfCart specifically ships with syncInterval hard-coded to 0f (see
        // PredictedGolfCart.OnValidate in the decompiled source) - "send every tick, uncapped" -
        // so 0f * scale would still be 0f and silently skip the one component that needs this
        // most. Seed the baseline from NetworkServer.sendInterval (1 / tickRate) in that case:
        // that's the real cadence 0f already resolves to, so scaling from it is consistent with
        // "change nothing at vanilla player count" rather than inventing a number.
        float currentInterval = (float)SyncIntervalField.GetValue(behaviour);
        if (currentInterval <= 0f)
        {
            currentInterval = NetworkServer.sendInterval;
        }

        SyncIntervalField.SetValue(behaviour, currentInterval * scale);
    }

    // Returns null if entity has no predicted-cart sync component, exactly like the property
    // itself would - callers already null-check the typed property elsewhere in this file, so
    // this preserves that contract rather than silently changing behaviour at the reflection
    // boundary.
    internal static NetworkBehaviour GetEntityPredictedGolfCart(Entity entity)
    {
        return EntityPredictedGolfCartProperty.GetValue(entity) as NetworkBehaviour;
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

        // Fetched by reflection rather than __instance.PredictedGolfCart directly - see the
        // comment on EntityPredictedGolfCartProperty above for why. Guarded rather than assumed
        // non-null, consistent with every other lookup in this file, though in practice every
        // golf cart is expected to have one (GolfCartInfo.SetMovementSyncDirectionInternal
        // dereferences AsEntity.PredictedGolfCart unconditionally whenever the driver seat
        // changes, with no null check of its own).
        NetworkBehaviour predictedGolfCart = NetworkSyncIntervalScaling.GetEntityPredictedGolfCart(__instance);
        if (predictedGolfCart == null)
        {
            return;
        }

        NetworkSyncIntervalScaling.Apply(predictedGolfCart);
    }
}
