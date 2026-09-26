using System.Reflection;
using HarmonyLib;

namespace MoreGolfers;

// ---------------------------------------------------------------------------------------------
// WHAT THIS DOES
//
// Cross-referencing the crash.dmp/Player.log addresses (via the supplied lib_burst_generated.dll
// decompilation) against BoundsManager.cs settles this: the fault is inside the Burst-compiled
// Bezier winding-angle job (GetBezierWindingAnglesForSinglePointJob) that BoundsManager schedules
// to answer "is this point inside the level/green bounds".
//
// BoundsManager.cs has three near-identical "is point in X" methods. One of them -
// GetNearestPointOnReturnSplinesInternal - guards correctly:
//
//     if (!returnSplinesWorldCurves.IsCreated) { ...; return default; }
//
// The other two - IsPointInLevelBoundsImmediateInternal and IsPointInGreenBoundsImmediateInternal
// - only check that levelBounds/greenBounds (the SplineContainer reference) is non-null, and then
// read levelBoundsWorldCurves.Length / greenBoundsWorldCurves.Length and hand that NativeArray
// straight to the winding-angle job, with no equivalent .IsCreated check.
//
// InitializeLevelBounds()/InitializeGreenBounds() Dispose() the old NativeArray<BezierCurve> and
// reassign it later in the same method - so there's a window (mid hole-transition, while the new
// hole's SplineContainer reference is already assigned but its curves haven't been rebuilt yet)
// where the array is disposed/stale. In a release Burst build the NativeContainer safety-handle
// checks are compiled out, so reading .Length and scheduling a job against a disposed array
// doesn't throw - it hands the job a stale length and a freed pointer, and the job faults reading
// freed native memory from inside Burst code. No catchable exception, purely native - which
// matches the crash signature exactly (the whole stack resolves into UnityPlayer.dll and
// lib_burst_generated.dll, no managed frame anywhere).
//
// This runs on every ball/player/tick, so the odds of landing inside that init window scale with
// concurrent entity count - same shape as every other "fine at 16, breaks past it" bug in this
// mod, just surfacing as a hard crash instead of a catchable one because these two methods are
// missing the guard their own sibling already has.
//
// Fix: give both methods the same .IsCreated guard GetNearestPointOnReturnSplinesInternal already
// has, via a Harmony Prefix. levelBoundsWorldCurves/greenBoundsWorldCurves are private fields of
// type NativeArray<BezierCurve> (Unity.Collections + the Splines package's BezierCurve) - resolved
// by reflection rather than referenced directly, the same reasoning as every cross-assembly access
// in this mod: it avoids needing a compile-time reference to whichever package assembly
// BezierCurve lives in, and reading FieldInfo.FieldType at runtime means the exact closed generic
// type never has to be named in this project's source at all.
// ---------------------------------------------------------------------------------------------

internal static class BoundsCurvesGuard
{
    private static readonly FieldInfo LevelBoundsWorldCurvesField =
        AccessTools.Field(typeof(BoundsManager), "levelBoundsWorldCurves");

    private static readonly FieldInfo GreenBoundsWorldCurvesField =
        AccessTools.Field(typeof(BoundsManager), "greenBoundsWorldCurves");

    private static readonly PropertyInfo LevelBoundsIsCreatedProperty =
        LevelBoundsWorldCurvesField != null
            ? AccessTools.Property(LevelBoundsWorldCurvesField.FieldType, "IsCreated")
            : null;

    private static readonly PropertyInfo GreenBoundsIsCreatedProperty =
        GreenBoundsWorldCurvesField != null
            ? AccessTools.Property(GreenBoundsWorldCurvesField.FieldType, "IsCreated")
            : null;

    private static bool _hasLoggedLevelUnavailable;
    private static bool _hasLoggedGreenUnavailable;

    internal static bool LevelBoundsGuardAvailable
    {
        get
        {
            if (LevelBoundsWorldCurvesField != null && LevelBoundsIsCreatedProperty != null)
            {
                return true;
            }

            if (!_hasLoggedLevelUnavailable)
            {
                _hasLoggedLevelUnavailable = true;
                MoreGolfersPlugin.Logger?.LogWarning(
                    "Skipping IsPointInLevelBoundsImmediate crash guard: levelBoundsWorldCurves or " +
                    "its IsCreated property could not be resolved. This build remains exposed to " +
                    "the disposed-NativeArray crash during hole transitions.");
            }

            return false;
        }
    }

    internal static bool GreenBoundsGuardAvailable
    {
        get
        {
            if (GreenBoundsWorldCurvesField != null && GreenBoundsIsCreatedProperty != null)
            {
                return true;
            }

            if (!_hasLoggedGreenUnavailable)
            {
                _hasLoggedGreenUnavailable = true;
                MoreGolfersPlugin.Logger?.LogWarning(
                    "Skipping IsPointInGreenBoundsImmediate crash guard: greenBoundsWorldCurves or " +
                    "its IsCreated property could not be resolved. This build remains exposed to " +
                    "the disposed-NativeArray crash during hole transitions.");
            }

            return false;
        }
    }

    // Mirrors "if (!levelBoundsWorldCurves.IsCreated) return false/default;" - boxes the struct via
    // FieldInfo.GetValue so the IsCreated getter can be invoked by reflection without this project
    // ever naming NativeArray<BezierCurve> as a type.
    internal static bool IsLevelCurvesCreated(BoundsManager instance)
    {
        object curves = LevelBoundsWorldCurvesField.GetValue(instance);
        return (bool)LevelBoundsIsCreatedProperty.GetValue(curves);
    }

    internal static bool IsGreenCurvesCreated(BoundsManager instance)
    {
        object curves = GreenBoundsWorldCurvesField.GetValue(instance);
        return (bool)GreenBoundsIsCreatedProperty.GetValue(curves);
    }
}

[HarmonyPatch(typeof(BoundsManager), "IsPointInLevelBoundsImmediateInternal")]
static class PatchIsPointInLevelBoundsImmediateInternal
{
    static bool Prepare() => BoundsCurvesGuard.LevelBoundsGuardAvailable;

    private const string LogKey = "bounds-manager-level-curves-not-created";

    // Same answer the vanilla method already gives when levelBounds itself is null - "not in
    // bounds" - just extended to the case where the curves backing it aren't (yet, or any longer)
    // valid. Returning false here skips the original method entirely, so the Burst job is never
    // scheduled against the disposed/stale array.
    static bool Prefix(BoundsManager __instance, ref bool __result)
    {
        if (BoundsCurvesGuard.IsLevelCurvesCreated(__instance))
        {
            return true;
        }

        // Throttled rather than unconditional - if this is genuinely racing InitializeLevelBounds
        // during a hole transition, every player/ball's out-of-bounds check in that window would
        // hit it, which is exactly the kind of per-tick spam ThrottledLog exists to stop. One
        // message per throttle window is enough to confirm which side raced without reproducing
        // the crash's own symptom.
        ThrottledLog.Warn(LogKey,
            "Skipped IsPointInLevelBoundsImmediate: levelBoundsWorldCurves is not created " +
            "(disposed or not yet rebuilt). Returning false instead of scheduling the winding-angle " +
            "job against a stale/disposed NativeArray.");

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(BoundsManager), "IsPointInGreenBoundsImmediateInternal")]
static class PatchIsPointInGreenBoundsImmediateInternal
{
    static bool Prepare() => BoundsCurvesGuard.GreenBoundsGuardAvailable;

    private const string LogKey = "bounds-manager-green-curves-not-created";

    static bool Prefix(BoundsManager __instance, ref bool __result)
    {
        if (BoundsCurvesGuard.IsGreenCurvesCreated(__instance))
        {
            return true;
        }

        ThrottledLog.Warn(LogKey,
            "Skipped IsPointInGreenBoundsImmediate: greenBoundsWorldCurves is not created " +
            "(disposed or not yet rebuilt). Returning false instead of scheduling the winding-angle " +
            "job against a stale/disposed NativeArray.");

        __result = false;
        return false;
    }
}
