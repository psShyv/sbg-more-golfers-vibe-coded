using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using BepInEx.Configuration;
using UnityEngine;

namespace MoreGolfers;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class MoreGolfersPlugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    public static ConfigEntry<int> MaxPlayersConfig;

    // Vanilla Super Battle Golf ships with a 16-player cap. Above PlatformSplitThreshold
    // players the second pair of teeing platforms comes online, so the cap is spread over
    // 4 platforms instead of 2 (see GetActivePlatformCount).
    private const int PlatformSplitThreshold = 8;

    // The vanilla cap itself - not to be confused with PlatformSplitThreshold above, which is a
    // different number (8) marking an unrelated vanilla behaviour change. This one is the scaling
    // anchor for anything sized against "how many players vanilla was ever tested with" - see
    // GetNetworkSyncIntervalScale.
    private const int VanillaMaxPlayerCount = 16;

    // Vanilla's spacing between adjacent tees, and the usable width of a teeing platform
    // along its transform.right axis.
    //
    // PlatformTeeSpan is THE number to tune if tees end up sitting short of, or just past,
    // the physical edge of the platform - every layout below is fitted into exactly this span
    // and anchored to its left end, which is what makes it impossible for a tee to be placed
    // off the platform. 12 is carried over from this mod's original spacing formula
    // (12f / (playersPerPlat - 1)), i.e. it's the width the mod was already assuming.
    //
    // Note it is deliberately NOT derived from "vanilla is 16 players over 2 platforms, so 8
    // tees per platform". A row of 8 tees at 3.25 spacing would be 22.75 wide, nearly double
    // this span - vanilla almost certainly uses 4 platforms of 4 tees, which is 9.75 wide and
    // fits. Guessing the span from the player cap is how tees end up in mid-air; measuring it
    // once and reusing it everywhere is not.
    private const float VanillaDistanceBetweenTees = 3.25f;
    private const float PlatformTeeSpan = 12f;

    private void Awake()
    {
        Logger = base.Logger;
        MaxPlayersConfig = Config.Bind("General", "MaxPlayers", 32,
            new ConfigDescription("Player limit", new AcceptableValueRange<int>(2, 64)));
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        PatchAllResilient(harmony);
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded! (see log above for any individual patches that failed to apply)");
    }

    // harmony.PatchAll() applies every [HarmonyPatch] class in one pass and, critically, aborts
    // that entire pass the moment any single class fails (e.g. a target method that no longer
    // exists after a game update) - meaning one broken patch silently takes every other patch in
    // the mod down with it. That's exactly what happened when SliderLogicPatch's target lambda
    // got renamed by a game update: nothing in this plugin was actually active afterward, not
    // just the slider fix. Patching each class individually, each in its own try/catch, means a
    // single incompatible patch is logged and skipped instead of disabling the whole mod.
    private static void PatchAllResilient(Harmony harmony)
    {
        foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(MoreGolfersPlugin).Assembly))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Failed to apply patch class '{type.Name}': {ex.Message} - this usually means " +
                    "the game updated and a patched method's name or signature changed. Skipping " +
                    "just this patch; the rest of the mod should still work.");
            }
        }
    }

    // Returned as float (rather than the underlying int config value) because
    // PatchMatchSetupMenu's transpiler substitutes this call directly in place of an
    // Ldc_R4 (float) literal in the game's IL — changing this to return int would
    // produce a type mismatch on the evaluation stack at that call site.
    public static float GetCustomMaxPlayers()
    {
        return (float)MaxPlayersConfig.Value;
    }

    // Live connection count - kept as a general utility, but deliberately NOT used by the
    // platform/tee sizing math below anymore (see GetActivePlatformCount).
    public static int GetCurrentPlayerCount()
    {
        var connectionIds = BNetworkManager.ServerConnectionIds;
        return (connectionIds != null) ? connectionIds.Count : 1;
    }

    // GolfTeeingPlatform allocates its `teeingSpots` array exactly once, in Awake(), sized to
    // whatever this returns at that single moment - and Awake() runs at scene load, typically
    // before most (or any) players have connected. Driving this off the live connection count
    // (as an earlier version did) meant every platform's tee array got permanently locked in at
    // whatever tiny headcount happened to exist at scene load - usually just 1 slot per platform
    // - regardless of how many players actually joined afterward. It also meant the getter could
    // return a different value between the array's allocation and CreateTeeingSpots()'s loop
    // condition re-reading it on every iteration, risking an IndexOutOfRangeException.
    //
    // Deriving this from the configured player cap instead (stable for the whole session, same
    // as vanilla's hardcoded 16 always was) fixes both: every read within Awake() agrees, and it
    // agrees with every later read too, so tee counts/positions stay consistent all match long.
    public static int GetActivePlatformCount()
    {
        return MaxPlayersConfig.Value > PlatformSplitThreshold ? 4 : 2;
    }

    public static int GetCurrentPlayersPerPlatform()
    {
        int total = MaxPlayersConfig.Value;
        int platforms = GetActivePlatformCount();
        return Mathf.Max(1, Mathf.CeilToInt((float)total / platforms));
    }

    public static float GetDistanceBetweenTees()
    {
        return ComputeDistanceBetweenTees(GetCurrentPlayersPerPlatform());
    }

    // GolfTeeingPlatform.GetTeeWorldPosition() places tee i at
    //     platform.position + (DistanceBetweenTees * i - FirstTeeOffset) * platform.right
    // so FirstTeeOffset is really just "how far left of the platform's centre tee 0 sits".
    //
    // Vanilla derives it as half the row's own width, which centres the row. That's fine when
    // the row is always exactly as long as it's assumed to be, but it means the anchor point
    // moves whenever the assumed length changes, and a row shorter than the platform starts in
    // the middle rather than at the edge.
    //
    // Returning a fixed half-span instead pins tee 0 to the left end of the platform for every
    // layout, so tees always start on the left and fill rightwards, and a full row's last tee
    // lands exactly on the right end. Since ComputeDistanceBetweenTees() fits every layout into
    // that same span, nothing can be placed past the edge no matter what the player cap is.
    public static float GetFirstTeeOffset()
    {
        return PlatformTeeSpan / 2f;
    }

    // Pool sizing for the vanilla object pools that were dimensioned against the 16-player cap.
    // Both pools destroy anything arriving once they're full and re-instantiate it later, so a
    // pool smaller than the lobby turns every hole transition (and every tee-off) into a burst of
    // destroy/allocate churn - and, in HoleProgressBarUi's case, exposed a lifecycle bug that
    // vanilla could never reach. See UiLifecyclePatches.cs.
    //
    // A little slack above the cap absorbs entries that are briefly in flight (a player being
    // re-registered as they finish a hole, say) without the pool tipping over into its full branch.
    private const int PoolSizeSlack = 4;

    public static int GetRequiredUiEntryPoolSize()
    {
        return MaxPlayersConfig.Value + PoolSizeSlack;
    }

    // Hit tees are transient - one per swing, returned once the post-hit animation ends - so the
    // worst realistic case is the whole lobby teeing off together. No slack needed beyond that.
    public static int GetRequiredHitTeePoolSize()
    {
        return MaxPlayersConfig.Value;
    }

    // NameTagManager backs a single pool (NameTagManager.nameTagPool) shared by every caller of
    // GetUnusedNameTag/ReturnNameTag. Only two of those callers scale with the player cap rather
    // than being a fixed handful: PlayerId hands out one name tag per connected player, and
    // GolfBall hands out one per ball that needs a name tag - so the worst realistic case is every
    // player's own name tag and every player's ball name tag alive at the same time, i.e. 2x the
    // player cap. LocalSpectatorCameraFollower also borrows one while spectating, which
    // PoolSizeSlack (already used above for the same purpose) covers along with anything briefly
    // in flight around a pool/return race.
    //
    // Unlike the progress-bar pool this one has no lifecycle bug - NameTagManager.ReturnNameTag
    // destroys the actual GameObject, correctly, once the pool is full - so an undersized pool here
    // isn't a crash risk, just destroy/reinstantiate churn on every hole transition once the lobby
    // outgrows vanilla's 16-player assumption.
    public static int GetRequiredNameTagPoolSize()
    {
        return MaxPlayersConfig.Value * 2 + PoolSizeSlack;
    }

    // WorldspaceIconManager backs a single pool (iconPool) shared by every icon type it serves:
    // objective, ball dispenser, hole, red/blue team and homing-warning icons are a fixed handful
    // tied to level geometry or in-flight projectiles, not the player cap, but two are not -
    // PlayerInfo.cs hands every OTHER connected player a teammateWorldSpaceIcon (so up to
    // MaxPlayers-1 concurrently on any one client), and GolfBall.cs hands one to every ball that
    // needs a worldspace icon (up to MaxPlayers, same one-ball-per-player assumption as the name
    // tag pool above). Sized the same way as the name tag pool for the same reason - worst case is
    // every other player's teammate icon plus every player's ball icon at once - with PoolSizeSlack
    // covering the fixed handful of level/projectile icons on top.
    //
    // Same as the name tag pool: no lifecycle bug, WorldspaceIconManager.ReturnIcon destroys the
    // GameObject correctly once over capacity, so this is churn (extra Instantiate/Destroy traffic
    // whenever visibility changes at higher player counts), not a crash risk.
    public static int GetRequiredWorldspaceIconPoolSize()
    {
        return MaxPlayersConfig.Value * 2 + PoolSizeSlack;
    }

    // TextPopupManager backs the floating stroke/penalty text popup shown over a player's head
    // (PlayerInfo.cs's only caller of GetUnusedPopup). Popups are transient like hit tees, but
    // unlike hit tees they can burst in the exact way the hole-progress-bar bug already showed is
    // dangerous: a hole ending is a moment where every player in the lobby can trigger a scoring
    // popup within the same frame. Sized at 1x the player cap plus slack for the same reason as the
    // hit-tee pool - the whole lobby popping a stroke result at once is the worst realistic case.
    //
    // No lifecycle bug here either - TextPopupManager.ReturnPopup destroys the GameObject correctly
    // once over capacity - so, again, this removes churn rather than fixing a crash.
    public static int GetRequiredTextPopupPoolSize()
    {
        return MaxPlayersConfig.Value + PoolSizeSlack;
    }

    // JumboBurgerGiantFormTimerManager backs the on-screen timer shown while a player is in giant
    // form (PlayerInfo.cs's only caller of GetUnusedTimer). One per player who has the effect
    // active - worst realistic case is every player in the lobby eating a jumbo burger around the
    // same time - so sized like the text-popup pool: 1x the player cap plus slack. Same static-cache
    // Awake pattern as the other pools above (staticallyCachedMaxPoolSize), same lack of a lifecycle
    // bug (ReturnTimer destroys the GameObject correctly when over capacity) - churn only.
    public static int GetRequiredJumboBurgerTimerPoolSize()
    {
        return MaxPlayersConfig.Value + PoolSizeSlack;
    }

    // LockOnTargetUiManager backs the local player's own homing-weapon lock-on reticle UI
    // (PlayerGolfer.cs/PlayerInventory.cs call AddTarget/RemoveTarget for every opponent that
    // becomes a valid lock-on target for whatever weapon is currently equipped). How many targets
    // can be simultaneously valid scales with how many opponents are in range at once, which scales
    // with the lobby size - so sized the same as the text-popup and jumbo-burger pools above: 1x the
    // player cap plus slack, covering every other player being a live target at once.
    //
    // Unlike the four pools above, this one does NOT cache maxPoolSize into a static field in
    // Awake() - ReturnTargetUi reads the instance field directly every time - so there's no
    // Prefix-before-the-copy timing requirement; see PatchLockOnTargetUiManagerAwake for why that
    // patch is a Postfix instead of a Prefix, unlike every other pool patch in this file.
    public static int GetRequiredLockOnTargetPoolSize()
    {
        return MaxPlayersConfig.Value + PoolSizeSlack;
    }

    // How much to slow down each golf cart's own Mirror SyncVar broadcast rate (see
    // NetworkTrafficPatches.cs) relative to whatever the designers already configured. 1x at
    // vanilla's own player count or below - this mod should change nothing about a vanilla-sized
    // game. Capped rather than left to grow unbounded with the player cap: past a point, slower
    // updates start costing more in perceived responsiveness (another cart's steering/braking
    // visibly lagging) than they save in bandwidth, and this is cosmetic client-side
    // interpolation, not something that needs to track the player count exactly.
    private const float MaxNetworkSyncIntervalScale = 3f;

    public static float GetNetworkSyncIntervalScale()
    {
        float raw = (float)MaxPlayersConfig.Value / VanillaMaxPlayerCount;
        return Mathf.Clamp(raw, 1f, MaxNetworkSyncIntervalScale);
    }

    private static float ComputeDistanceBetweenTees(int playersPerPlat)
    {
        // A single tee sits at the left end; there's no gap to size.
        if (playersPerPlat <= 1)
        {
            return VanillaDistanceBetweenTees;
        }

        // Share the platform span out among however many gaps this row needs, but never open
        // the tees up wider than vanilla ever did - so a platform holding only a few players
        // keeps normal-looking spacing along its left-hand side instead of scattering them to
        // the far corners, while a busy platform tightens up just enough to fit everyone on.
        float fitted = PlatformTeeSpan / (playersPerPlat - 1);
        return Mathf.Min(VanillaDistanceBetweenTees, fitted);
    }
}
