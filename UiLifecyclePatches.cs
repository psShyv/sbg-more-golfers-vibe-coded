using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace MoreGolfers;

// ---------------------------------------------------------------------------------------------
// THE BUG THESE PATCHES FIX
//
// Observed symptom: repeating NullReferenceException at
//     RectTransform.get_anchorMin
//     HoleProgressBarPlayerEntryUi.<OnLateUpdate>g__SetNormalizedProgress|15_2
//     HoleProgressBarPlayerEntryUi.OnLateUpdate
//     HoleProgressBarUi.OnLateBUpdate
//     BUpdate.OnLateUpdateLoop
//
// It is three separate vanilla defects that only compound into a visible failure once the
// player count exceeds what the pool was sized for - i.e. exactly what this mod does.
//
// (1) HoleProgressBarUi.RemovePlayerEntry calls Object.Destroy(entry) on the *component*
//     (not entry.gameObject) and then immediately hands that same entry to
//     ReturnPlayerEntryToPool. Unity defers the destroy to end of frame, so the entry is not
//     yet fake-null and sails past that method's null check, gets deactivated, reparented to
//     the DontDestroyOnLoad pool root, and pushed onto playerEntryPool. One frame later the
//     component is dead but its GameObject is still alive and still parented under the pool
//     root, permanently unreachable. Every player removal therefore leaks one GameObject and
//     burns one pool slot on a corpse. GetUnusedPlayerEntry's `while (entry == null)` loop
//     discards corpses on the way *out* of the pool, which is why this never crashed on its
//     own - it just quietly fills the pool.
//
// (2) ReturnPlayerEntryToPool checks `entry == null` *after* it has already dereferenced
//     `entry.gameObject` in the pool-full branch. The guard cannot do its job.
//
// (3) HoleProgressBarUi.OnWillChangeScene returns every active entry to the pool but never
//     clears activePlayerEntries or sortedActivePlayerEntires. Once (1) has packed the pool
//     with corpses, `playerEntryPool.Count >= maxPlayerEntryPoolSize` is true, so
//     ReturnPlayerEntryToPool takes its pool-full branch and calls Object.Destroy on the
//     entry's *GameObject* - while that entry is still sitting in activePlayerEntries.
//     OnLateBUpdate keeps iterating that dictionary every frame until the old
//     HoleProgressBarUi is torn down, calling OnLateUpdate on entries whose RectTransform no
//     longer exists. That is the logged NullReferenceException, once per orphaned entry per
//     frame, for the whole hole transition.
//
// Note the tell in vanilla OnLateBUpdate: its *second* loop null-checks each entry before
// touching transform, but the first loop calls OnLateUpdate() unguarded. The destroyed-entry
// case was known; only half of it was handled.
//
// Why vanilla never sees it: with a 16-player cap the pool is never pressured, so branch (3)
// is unreachable. At 32-64 players a single hole transition returns more entries at once than
// the pool can hold even before the corpses from (1) are counted.
// ---------------------------------------------------------------------------------------------

// Fix for (3), and the direct cause of the logged spam. The entries have already been pooled
// (or destroyed) by the time the original returns, so the collections that still reference them
// are stale by definition - clearing them is what the original should have done. Both
// AddPlayerEntry and RemovePlayerEntry early-out while IsChangingSceneOrShuttingDown is set, and
// the next scene's HoleProgressBarUi repopulates from scratch in Start(), so nothing else reads
// these collections between here and teardown.
[HarmonyPatch(typeof(HoleProgressBarUi), "OnWillChangeScene")]
static class PatchHoleProgressBarUiOnWillChangeScene
{
    static void Postfix(
        Dictionary<PlayerInfo, HoleProgressBarPlayerEntryUi> ___activePlayerEntries,
        List<HoleProgressBarPlayerEntryUi> ___sortedActivePlayerEntires)
    {
        // NOTE: 'sortedActivePlayerEntires' is misspelled in the game assembly. Harmony matches
        // the real field name, so the typo has to be reproduced here verbatim.
        ___activePlayerEntries?.Clear();
        ___sortedActivePlayerEntires?.Clear();
    }
}

// Fix for (1). Surgically removes the stray Object.Destroy(entry) and nothing else: the call
// pops one argument and pushes nothing, so replacing it with Pop leaves the evaluation stack
// balanced and every other instruction in the method untouched. The entry then reaches
// ReturnPlayerEntryToPool alive and genuinely reusable, which is plainly what the surrounding
// pooling code intends.
[HarmonyPatch(typeof(HoleProgressBarUi), "RemovePlayerEntry")]
static class PatchHoleProgressBarUiRemovePlayerEntry
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo destroy = AccessTools.Method(
            typeof(UnityEngine.Object),
            nameof(UnityEngine.Object.Destroy),
            new[] { typeof(UnityEngine.Object) });

        List<CodeInstruction> codes = instructions.ToList();
        int replaced = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Call || (codes[i].operand as MethodInfo) != destroy)
            {
                continue;
            }

            // Copy-construct so any labels or exception-handling blocks attached to the original
            // instruction survive; dropping those would corrupt branches into this offset.
            codes[i] = new CodeInstruction(codes[i]) { opcode = OpCodes.Pop, operand = null };
            replaced++;
        }

        if (replaced != 1)
        {
            MoreGolfersPlugin.Logger.LogWarning(
                $"RemovePlayerEntry: expected exactly 1 Object.Destroy(Object) call to remove but found {replaced}. " +
                "The game likely changed this method. Progress-bar entries may leak GameObjects, but nothing " +
                "should break - the remaining lifecycle patches still apply.");
        }

        return codes;
    }
}

// Fix for (2). Restores the guard the original wrote but placed one branch too late, so a
// destroyed entry can no longer be dereferenced here. With the patches above this should be
// unreachable; it stays as the cheap backstop for any path that still hands over a dead entry.
[HarmonyPatch(typeof(HoleProgressBarUi), "ReturnPlayerEntryToPool")]
static class PatchHoleProgressBarUiReturnPlayerEntryToPool
{
    // __0 rather than the parameter name: positional injection keeps working if a future game
    // update renames the parameter.
    static bool Prefix(HoleProgressBarPlayerEntryUi __0)
    {
        return __0 != null;
    }
}

// The guard on the throwing method itself. Placed here rather than on HoleProgressBarUi's loop
// because every caller benefits, and because the actual throw site is a compiler-generated local
// function ('<OnLateUpdate>g__SetNormalizedProgress|15_2') whose name shifts whenever any lambda
// elsewhere in the class is added or removed - the same fragility that already broke
// SliderLogicPatch once. OnLateUpdate is a real, stable, public method.
//
// A destroyed GameObject takes its RectTransform with it, so both checks below describe the same
// failure from two directions; __instance is checked first because dereferencing a destroyed
// component's fields is what throws in the first place.
[HarmonyPatch(typeof(HoleProgressBarPlayerEntryUi), "OnLateUpdate")]
static class PatchHoleProgressBarPlayerEntryUiOnLateUpdate
{
    static bool Prefix(HoleProgressBarPlayerEntryUi __instance, RectTransform ___rectTransform)
    {
        if (__instance == null)
        {
            ThrottledLog.Warn(
                "progressbar-entry-destroyed",
                "Skipped OnLateUpdate on a destroyed hole-progress-bar entry that was still being " +
                "driven by the late update loop. Suppressing further identical warnings briefly.");
            return false;
        }

        if (___rectTransform == null)
        {
            ThrottledLog.Warn(
                "progressbar-entry-no-recttransform",
                "Hole-progress-bar entry has no RectTransform (its GameObject was destroyed, or the " +
                "prefab root is not a RectTransform). Skipping its update.");
            return false;
        }

        return true;
    }
}

// Pooled entries kept a live reference to the PlayerInfo they were last used for: OnReturnedToPool
// unsubscribes from that player's events but never drops the reference. That pins a PlayerInfo per
// pooled entry for as long as the pool lives (which is forever - the pool root is
// DontDestroyOnLoad), and means a pooled entry that still gets ticked would compute progress from
// a player it no longer represents. Clearing it makes UpdatePosition take its documented
// `player == null` branch instead.
//
// Safe because OnReturnedToPool has already unsubscribed every handler that reads this field, and
// OnDestroy's own `player != null` check simply skips the now-redundant second unsubscribe.
[HarmonyPatch(typeof(HoleProgressBarPlayerEntryUi), "OnReturnedToPool")]
static class PatchHoleProgressBarPlayerEntryUiOnReturnedToPool
{
    static void Postfix(ref PlayerInfo ___player)
    {
        ___player = null;
    }
}

// maxPlayerEntryPoolSize is a serialized field sized against vanilla's 16-player cap. Every entry
// beyond it gets destroyed instead of pooled on a hole transition and re-instantiated on the next
// hole - per player, per hole. Raising it to the configured cap means the pool can actually absorb
// a full lobby, which removes the destroy/reinstantiate churn and keeps the pool-full branch (the
// one implicated in the bug above) out of normal play entirely.
//
// Prefix rather than Postfix so the value is in place before anything can read it.
[HarmonyPatch(typeof(HoleProgressBarUi), "Awake")]
static class PatchHoleProgressBarUiAwake
{
    static void Prefix(ref int ___maxPlayerEntryPoolSize)
    {
        int required = MoreGolfersPlugin.GetRequiredUiEntryPoolSize();
        if (___maxPlayerEntryPoolSize >= required)
        {
            return;
        }

        ___maxPlayerEntryPoolSize = required;
    }
}

// Same class of problem, same fix, different pool: GolfTeeManager copies maxHitTeePoolSize into a
// static cache during Awake, and ReturnHitTee destroys any hit tee that arrives once the pool is
// full. At a vanilla-sized pool with 32-64 golfers teeing off together that is a burst of
// destroy-then-reinstantiate every single tee-off.
//
// This MUST be a Prefix: Awake's body is what copies the field into
// staticallyCachedMaxHitTeePoolSize, so a Postfix would set the field after the value that
// actually gets used had already been captured.
[HarmonyPatch(typeof(GolfTeeManager), "Awake")]
static class PatchGolfTeeManagerAwake
{
    static void Prefix(ref int ___maxHitTeePoolSize)
    {
        int required = MoreGolfersPlugin.GetRequiredHitTeePoolSize();
        if (___maxHitTeePoolSize >= required)
        {
            return;
        }

        ___maxHitTeePoolSize = required;
    }
}

// Third pool sized against vanilla's 16-player cap: NameTagManager.Awake copies its serialized
// maxPoolSize into a static cache (staticallyCachedMaxPoolSize) the same way GolfTeeManager does
// for staticallyCachedMaxHitTeePoolSize above, and NameTagManager.ReturnNameTag destroys anything
// that arrives once nameTagPool.Count reaches that cached size instead of pooling it. PlayerId and
// GolfBall are the two callers that scale with player count (see
// MoreGolfersPlugin.GetRequiredNameTagPoolSize for why 2x), so at 32-64 players a pool sized for 16
// would otherwise turn every hole transition into a burst of name-tag destroy/reinstantiate churn,
// on top of the progress-bar and hit-tee churn the other two patches already remove.
//
// No lifecycle bug here unlike PatchHoleProgressBarUiAwake above - ReturnNameTag destroys the
// GameObject itself, correctly, in the pool-full branch - so this is purely about removing the
// churn, not about correctness.
//
// This MUST be a Prefix, for the same reason as PatchGolfTeeManagerAwake: Awake's own body is what
// reads maxPoolSize into the static cache that GetUnusedNameTag/ReturnNameTag actually consult, so
// a Postfix would set the field after the value that matters had already been captured.
[HarmonyPatch(typeof(NameTagManager), "Awake")]
static class PatchNameTagManagerAwake
{
    static void Prefix(ref int ___maxPoolSize)
    {
        int required = MoreGolfersPlugin.GetRequiredNameTagPoolSize();
        if (___maxPoolSize >= required)
        {
            return;
        }

        ___maxPoolSize = required;
    }
}

// Fourth pool of this same shape: WorldspaceIconManager.Awake copies its serialized maxPoolSize
// into a static cache exactly like NameTagManager does, and WorldspaceIconManager.ReturnIcon
// destroys anything over that cached size instead of pooling it. See
// MoreGolfersPlugin.GetRequiredWorldspaceIconPoolSize for which callers scale with player count
// (teammate icons and ball icons) and why 2x. Must be a Prefix for the same reason as the other
// three Awake patches in this file: the static cache is populated from Awake's own body, so a
// Postfix would run too late to change what gets cached.
[HarmonyPatch(typeof(WorldspaceIconManager), "Awake")]
static class PatchWorldspaceIconManagerAwake
{
    static void Prefix(ref int ___maxPoolSize)
    {
        int required = MoreGolfersPlugin.GetRequiredWorldspaceIconPoolSize();
        if (___maxPoolSize >= required)
        {
            return;
        }

        ___maxPoolSize = required;
    }
}

// Fifth pool of this same shape: TextPopupManager.Awake copies its serialized maxPoolSize into a
// static cache exactly like the four patches above, and TextPopupManager.ReturnPopup destroys
// anything over that cached size instead of pooling it. See
// MoreGolfersPlugin.GetRequiredTextPopupPoolSize for why this is sized like the hit-tee pool
// rather than the 2x pools above. Must be a Prefix for the same reason as the others: Awake's own
// body populates the static cache this patch needs to change before it's read.
[HarmonyPatch(typeof(TextPopupManager), "Awake")]
static class PatchTextPopupManagerAwake
{
    static void Prefix(ref int ___maxPoolSize)
    {
        int required = MoreGolfersPlugin.GetRequiredTextPopupPoolSize();
        if (___maxPoolSize >= required)
        {
            return;
        }

        ___maxPoolSize = required;
    }
}

// Sixth pool of this same shape: JumboBurgerGiantFormTimerManager.Awake copies its serialized
// maxPoolSize into a static cache exactly like the pools above, and ReturnTimer destroys anything
// over that cached size instead of pooling it. See
// MoreGolfersPlugin.GetRequiredJumboBurgerTimerPoolSize. Prefix for the same reason as the other
// static-cache patches: Awake's own body populates the cache this patch needs to change first.
[HarmonyPatch(typeof(JumboBurgerGiantFormTimerManager), "Awake")]
static class PatchJumboBurgerGiantFormTimerManagerAwake
{
    static void Prefix(ref int ___maxPoolSize)
    {
        int required = MoreGolfersPlugin.GetRequiredJumboBurgerTimerPoolSize();
        if (___maxPoolSize >= required)
        {
            return;
        }

        ___maxPoolSize = required;
    }
}

// Seventh pool, same churn problem, different shape: LockOnTargetUiManager does NOT cache
// maxPoolSize into a static field the way the six pools above do - ReturnTargetUi
// (LockOnTargetUiManager.cs) reads the serialized instance field directly on every call. That
// means there's no "must run before Awake's body copies the value" constraint, so unlike every
// other patch in this file this one is a Postfix: it only needs to have changed the field by the
// time anything else can possibly call ReturnTargetUi, and nothing can do that before Awake
// returns. See MoreGolfersPlugin.GetRequiredLockOnTargetPoolSize for sizing.
[HarmonyPatch(typeof(LockOnTargetUiManager), "Awake")]
static class PatchLockOnTargetUiManagerAwake
{
    static void Postfix(ref int ___maxPoolSize)
    {
        int required = MoreGolfersPlugin.GetRequiredLockOnTargetPoolSize();
        if (___maxPoolSize >= required)
        {
            return;
        }

        ___maxPoolSize = required;
    }
}
