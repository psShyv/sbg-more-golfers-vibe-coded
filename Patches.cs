using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace MoreGolfers;

// REMOVED: TeeingSpotRepositioner + PatchTeeingSpotReserveFor + PatchTeeingSpotClaimFor.
//
// Those patches re-placed each teeing spot at the moment it was first assigned, using the
// platform's *live* headcount instead of its configured capacity, to avoid squeezing early
// joiners into worst-case spacing. The intent was good but the arithmetic couldn't work.
//
// The spot was placed at `distance * liveIndex - firstTeeOffset`, where firstTeeOffset was
// itself recomputed from the live headcount as `liveIndex * distance / 2`. Those cancel down
// to `liveIndex * distance / 2` - i.e. the k'th player to join a platform landed only *half* a
// spacing right of the (k-1)'th, starting from dead centre for the very first player. So the
// row began in the middle of the platform and crept rightwards, and since a platform is only
// about 12 units across, the 5th player onto a platform was already past its right edge -
// their tee left floating in space, and untargetable.
//
// Worse, above the old 8-players-per-platform threshold the live spacing became 12/liveIndex,
// and `liveIndex * (12 / liveIndex) / 2` is the constant 6 - so every player from the 9th
// onwards was placed at the exact same point, right on the edge, on top of each other.
//
// The underlying problem isn't a bug in the formula so much as a contradiction in the goal:
// spacing that adapts to live occupancy only produces a coherent row if *every* tee moves when
// the headcount changes, and moving players who are already standing at their tee is precisely
// what this was trying to avoid. Any fixed-once, live-derived placement drifts.
//
// GolfTeeingPlatform.Awake() already lays spots out left-to-right via GetTeeWorldPosition(i),
// and TryGetAvailableTeeingSpot() always hands out the lowest free index, so players naturally
// fill from index 0 upwards. With MoreGolfersPlugin.GetFirstTeeOffset() now pinned to the left
// end of the platform, that baked layout does exactly what's wanted - starts on the left, fills
// right, always inside the platform - with no live repositioning needed. Leaving the baked
// positions alone also fixes a second latent bug: after a player left mid-round and freed a
// low-index spot, the live-index maths would have dropped the next joiner on top of somebody.
//
// The trade-off, stated plainly: spacing is now sized for the configured cap, so a half-empty
// platform leaves unused room on its right-hand side. That's cosmetic, and stable.

[HarmonyPatch]
public static class SliderLogicPatch
{
    // MatchSetupMenu.LoadValues() defines its slider-configuration logic as an anonymous lambda,
    // which the compiler names <LoadValues>b__N_M. That N is a per-class counter that shifts
    // whenever ANY lambda is added or removed anywhere else in MatchSetupMenu, even in code
    // that has nothing to do with sliders - which is exactly what just happened on a game update
    // (b__84_0 -> b__129_0) with no actual change to the slider logic itself. Rather than
    // hardcode a specific generated name that's liable to shift again on the next update, find
    // the right lambda at runtime by its IL content: the one method among MatchSetupMenu's
    // compiler-generated <LoadValues>b__* methods whose body contains (at least) the two
    // Ldc_I4_S 16 loads the transpiler below patches.
    static MethodBase TargetMethod()
    {
        var candidates = AccessTools.GetDeclaredMethods(typeof(MatchSetupMenu))
            .Where(m => m.Name.StartsWith("<LoadValues>b__", StringComparison.Ordinal));

        foreach (MethodInfo candidate in candidates)
        {
            int count;
            try
            {
                count = PatchProcessor.GetCurrentInstructions(candidate)
                    .Count(instr => instr.opcode == OpCodes.Ldc_I4_S && instr.OperandIs(16));
            }
            catch
            {
                continue; // a handful of candidates may not be readable this way - skip, don't abort the whole search over one.
            }

            if (count >= 2)
            {
                return candidate;
            }
        }

        MoreGolfersPlugin.Logger.LogWarning(
            "Could not locate the MatchSetupMenu slider lambda by IL pattern - the match setup " +
            "slider may show the vanilla max instead of the configured one, but nothing else " +
            "should be affected. This patch will simply be skipped.");
        return null;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        int finds = 0;
        int newValue = (int)MoreGolfersPlugin.GetCustomMaxPlayers();

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_S && codes[i].OperandIs(16))
            {
                codes[i] = new CodeInstruction(OpCodes.Ldc_I4, newValue);
                finds++;

                if (finds >= 2)
                {
                    //MoreGolfersPlugin.Logger.LogInfo("Patched MatchSetupMenu delegate");
                    break;
                }
            }
        }

        // Warn on both a total miss and a partial match - a partial match is the more
        // dangerous case since the patch appears to succeed while only doing half the job.
        if (finds == 0)
            MoreGolfersPlugin.Logger.LogWarning("instruction Ldc_I4_S was not found with value 16.");
        else if (finds < 2)
            MoreGolfersPlugin.Logger.LogWarning($"Only found {finds} of the expected 2 occurrences of Ldc_I4_S with value 16 - patch may be incomplete.");

        return codes.AsEnumerable();
    }
}

[HarmonyPatch(typeof(MatchSetupMenu), "OnStartClient")]
static class PatchMatchSetupMenu
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        bool found = false;
        foreach (var instruction in instructions)
        {
            if (!found && instruction.opcode == OpCodes.Ldc_R4 && (float)instruction.operand == 16f)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(MoreGolfersPlugin), nameof(MoreGolfersPlugin.GetCustomMaxPlayers));
                found = true;
                //MoreGolfersPlugin.Logger.LogInfo("Patched MatchSetupMenu.onStartClient");
            }

            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(TeeingPlatformSettings), "MaxTeeCount", MethodType.Getter)]
static class PatchMaxTeeCount
{
    static bool Prefix(ref int __result)
    {
        __result = MoreGolfersPlugin.GetCurrentPlayersPerPlatform();
        return false;
    }
}

[HarmonyPatch(typeof(TeeingPlatformSettings), "DistanceBetweenTees", MethodType.Getter)]
static class PatchDistanceBetweenTees
{
    static bool Prefix(ref float __result)
    {
        __result = MoreGolfersPlugin.GetDistanceBetweenTees();
        return false;
    }
}

[HarmonyPatch(typeof(TeeingPlatformSettings), "FirstTeeOffset", MethodType.Getter)]
static class PatchFirstTeeOffset
{
    static bool Prefix(ref float __result)
    {
        __result = MoreGolfersPlugin.GetFirstTeeOffset();
        return false;
    }
}

// Now backed by the decompiled TextChatMessageUi.UpdateAlpha() - the stack trace's
// Component.get_gameObject() is `base.gameObject` inside the "message has timed out, hide it"
// branch, meaning `this` component itself is the destroyed/invalid object at that point.
//
// The source also shows a real (pre-existing, not mod-related) asymmetry: the `timeout <= 0f`
// branch properly calls BUpdate.DeregisterCallback when it's done, but the "timed out and faded
// out" branch just below it - the one that throws - never does. A message that plays out its
// full lifecycle stays subscribed to the global per-frame BUpdate loop indefinitely (until
// pooling reuses it), which is wasted ticking and one more thing that can end up racing against
// destruction once chat volume is higher than vanilla ever saw.
//
// Three-part fix:
//  - Prefix: skip the method entirely if `this` is already Unity-destroyed (`== null` correctly
//    detects that, even though the C# reference isn't literally null). Cheaper than throwing and
//    catching - the original NullReferenceException never happens at all via this path.
//  - Postfix: mirror the missing deregistration for the timed-out/faded-out case, so objects that
//    finish their lifecycle stop ticking (and correctly re-register next time they're reused via
//    TextChatUi's pooling, since Initialize() only re-registers when callbackRegistered is false).
//  - Finalizer: kept as a narrow backstop for any other NullReferenceException inside this method
//    (e.g. a destroyed canvasGroup/messageText field while the root component is still alive) -
//    a case the Prefix's instance check wouldn't catch.
[HarmonyPatch(typeof(TextChatMessageUi), "UpdateAlpha")]
static class PatchTextChatMessageUiUpdateAlpha
{
    private static float _lastLoggedAt = float.NegativeInfinity;
    private const float LogThrottleSeconds = 1f;

    static bool Prefix(TextChatMessageUi __instance)
    {
        return __instance != null;
    }

    static void Postfix(TextChatMessageUi __instance, ref bool ___callbackRegistered)
    {
        if (__instance == null)
        {
            return;
        }
        if (!___callbackRegistered || __instance.timeout <= 0f)
        {
            return; // not registered, or the `timeout <= 0f` branch already handled deregistration itself.
        }
        if (__instance.gameObject.activeSelf)
        {
            return; // still sliding in / showing / fading out - not finished yet.
        }

        BUpdate.DeregisterCallback(__instance, BUpdateLoopType.All);
        ___callbackRegistered = false;
    }

    static Exception Finalizer(Exception __exception)
    {
        if (__exception is NullReferenceException)
        {
            if (Time.unscaledTime - _lastLoggedAt >= LogThrottleSeconds)
            {
                _lastLoggedAt = Time.unscaledTime;
                MoreGolfersPlugin.Logger.LogWarning(
                    "Suppressed a NullReferenceException in TextChatMessageUi.UpdateAlpha " +
                    "(a chat bubble reference went stale) to stop it from spamming every frame.");
            }
            return null; // null = suppress; the original exception no longer propagates.
        }

        return __exception; // anything else: rethrow unchanged, don't mask unrelated bugs.
    }
}

// NOTE: PatchBUpdateOnUpdateLoop used to live here. It now sits in BUpdateLoopPatches.cs
// alongside the same treatment for the other four BUpdate loops, all sharing one implementation.
// The late-update loop in particular had no isolation at all, which is the loop every logged
// NullReferenceException in this session came through.

// PlayerOcclusionManager was flagged here as needing a patch to resize its native collections for
// higher player counts, but the real source (now available) shows that was never actually needed.
// Awake() sizes transforms/visibilty/occlusionMasks with a "16"/"8" argument, but for both
// TransformAccessArray and NativeList<T> that argument is only an initial-capacity hint, not a
// cap - RegisterInstanceInternal calls plain .Add() on each of them, which both types grow to fit
// automatically, and every later read (transforms.length, visibilty.Length) reads the live count,
// never the original hint. There's no hardcoded bound anywhere in the class. No patch needed.
