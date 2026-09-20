using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoreGolfers;

// ---------------------------------------------------------------------------------------------
// WHY THESE PATCHES EXIST
//
// BUpdate installs itself directly into Unity's low-level PlayerLoop and dispatches its own
// callback lists, bypassing Unity's per-MonoBehaviour Update() dispatch - which normally catches
// an exception from one broken script, logs it, and carries on with the next. BUpdate has no such
// isolation. All five of its loops are the same shape:
//
//     for (int i = callbacks.Count - 1; i >= 0; i--)
//         callbacks[i].SomeCallback();
//
// so one throwing callback aborts the loop and skips every callback below it in the list - and not
// just for that frame. Nothing removes the offending entry, and the lists are static and only
// cleared on application quit, so the skip repeats every frame, forever, across scene loads. That
// is the mechanism behind "it froze but didn't crash": one stale reference can silently stop an
// unbounded number of unrelated systems from ever updating again, with no crash dialog and only a
// repeating stack trace to show for it.
//
// It matters more at raised player counts because both the number of registered callbacks and the
// amount of object churn scale with the lobby size. Every NullReferenceException in the attached
// logs came through OnLateUpdateLoop specifically.
//
// Each patch fully replaces its loop with a reimplementation taken verbatim from BUpdate's
// decompiled source, plus per-callback try/catch and the three hardening changes documented in
// BUpdateLoopRunner. All five loops are covered so the protection is uniform rather than depending
// on which loop a future bug happens to land in.
// ---------------------------------------------------------------------------------------------

internal static class BUpdateLoopRunner
{
    internal static void Run<T>(FieldInfo listField, Action<T> invoke, string loopName) where T : class
    {
        List<T> callbacks = listField.GetValue(null) as List<T>;
        if (callbacks == null)
        {
            return;
        }

        // Vanilla iterates backwards, which is what makes the common "callback deregisters itself
        // mid-tick" case safe - HitGolfTee does exactly that when it returns itself to the pool.
        // That ordering is preserved. The three differences from vanilla are all strictly additive:
        for (int i = callbacks.Count - 1; i >= 0; i--)
        {
            // (1) A callback that removes several entries below the cursor shrinks the list past i,
            //     and vanilla's unguarded callbacks[i] would then throw ArgumentOutOfRangeException
            //     from inside the very loop that is meant to be surviving exceptions.
            if (i >= callbacks.Count)
            {
                continue;
            }

            T callback = callbacks[i];
            if (callback == null)
            {
                continue;
            }

            // (2) These lists hold *interface* references, and == on an interface-typed variable is
            //     plain reference equality - Unity's overloaded operator does not apply. A destroyed
            //     MonoBehaviour that never deregistered still looks non-null to the check above.
            //     Casting to Object is what gets the real "has this been destroyed" answer.
            if (callback is UnityEngine.Object unityObject && unityObject == null)
            {
                ThrottledLog.Warn(
                    "bupdate-destroyed-" + loopName,
                    $"BUpdate.{loopName} is still holding a destroyed {callback.GetType().Name} that never " +
                    "deregistered itself. Skipping it instead of letting it throw.");
                continue;
            }

            // (3) The actual point of all this.
            try
            {
                invoke(callback);
            }
            catch (Exception ex)
            {
                ThrottledLog.Warn(
                    "bupdate-throw-" + loopName,
                    $"Caught {ex.GetType().Name} from {callback.GetType().Name} in BUpdate.{loopName}. " +
                    "Vanilla would have aborted every remaining callback this frame and every frame after. " +
                    $"Message: {ex.Message}");
            }
        }
    }
}

// A Prefix that returns false replaces the loop outright. If the fields it needs can't be resolved
// (a game update renaming them, say), installing it anyway would replace the loop with *nothing* and
// silently disable every BUpdate callback in the game - far worse than the problem being fixed. So
// every patch below gates itself on this and simply declines to apply when anything is missing.
internal static class BUpdateLoopPrerequisites
{
    internal static bool Check(string loopName, params MemberInfo[] required)
    {
        foreach (MemberInfo member in required)
        {
            if (member != null)
            {
                continue;
            }

            MoreGolfersPlugin.Logger?.LogWarning(
                $"Skipping exception isolation for BUpdate.{loopName}: one of BUpdate's internals could not " +
                "be resolved, so the loop is being left exactly as the game shipped it. This is safe, but a " +
                "callback that throws there will still abort the rest of that frame's callbacks.");
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(BUpdate), "OnUpdateLoop")]
static class PatchBUpdateOnUpdateLoop
{
    private static readonly FieldInfo CallbacksField = AccessTools.Field(typeof(BUpdate), "updateCallbacks");
    private static readonly FieldInfo InvokeRepeatingField = AccessTools.Field(typeof(BUpdate), "invokeRepeating");
    private static readonly Type InstanceType = ResolveInvokeRepeatingInstanceType();
    private static readonly FieldInfo IntervalField = InstanceType?.GetField("interval");
    private static readonly FieldInfo LastUpdateField = InstanceType?.GetField("lastUpdate");
    private static readonly FieldInfo CallbackField = InstanceType?.GetField("callback");

    // Cached so the delegate isn't reallocated every frame.
    private static readonly Action<IBUpdateCallback> Invoke = callback => callback.OnBUpdate();

    // BUpdate.InvokeRepeatingInstance is `internal`, so it can't be named at compile time even
    // against a publicized reference. Resolving it from the list's generic argument and handling
    // elements as `object` sidesteps that without needing the type name.
    private static Type ResolveInvokeRepeatingInstanceType()
    {
        Type listType = InvokeRepeatingField?.FieldType;
        if (listType == null || !listType.IsGenericType)
        {
            return null;
        }

        Type[] arguments = listType.GetGenericArguments();
        return arguments.Length == 1 ? arguments[0] : null;
    }

    static bool Prepare()
    {
        return BUpdateLoopPrerequisites.Check(
            "OnUpdateLoop",
            CallbacksField, InvokeRepeatingField, IntervalField, LastUpdateField, CallbackField);
    }

    static bool Prefix()
    {
        BUpdateLoopRunner.Run(CallbacksField, Invoke, "OnUpdateLoop");
        RunInvokeRepeating();
        return false; // original fully replaced above.
    }

    private static void RunInvokeRepeating()
    {
        IList instances = InvokeRepeatingField.GetValue(null) as IList;
        if (instances == null)
        {
            return;
        }

        // Vanilla foreach-es this list, which throws InvalidOperationException if a callback calls
        // BUpdate.CancelInvoke (a RemoveAll) while it is being iterated - a perfectly natural thing
        // for a repeating callback to do, e.g. a timer that cancels itself on its final tick.
        // Indexing with Count re-read each iteration removes that failure mode.
        for (int i = 0; i < instances.Count; i++)
        {
            object instance = instances[i];
            if (instance == null)
            {
                continue;
            }

            float lastUpdate = (float)LastUpdateField.GetValue(instance);
            float interval = (float)IntervalField.GetValue(instance);
            float elapsed = Time.time - lastUpdate;

            // Vanilla condition is `elapsed > interval`; this is the same test inverted.
            if (elapsed <= interval)
            {
                continue;
            }

            try
            {
                (CallbackField.GetValue(instance) as Action<float>)?.Invoke(elapsed);
            }
            catch (Exception ex)
            {
                ThrottledLog.Warn(
                    "bupdate-throw-InvokeRepeating",
                    $"Caught {ex.GetType().Name} from a BUpdate.InvokeRepeating callback that would otherwise " +
                    $"have aborted the rest of this frame's update loop: {ex.Message}");
            }

            // Stamped even when the callback threw, so a consistently failing repeating callback
            // retries on its normal interval instead of spinning at full frame rate.
            LastUpdateField.SetValue(instance, Time.time);
        }
    }
}

// The loop every logged NullReferenceException in this session came through.
[HarmonyPatch(typeof(BUpdate), "OnLateUpdateLoop")]
static class PatchBUpdateOnLateUpdateLoop
{
    private static readonly FieldInfo CallbacksField = AccessTools.Field(typeof(BUpdate), "lateUpdateCallbacks");
    private static readonly Action<ILateBUpdateCallback> Invoke = callback => callback.OnLateBUpdate();

    static bool Prepare()
    {
        return BUpdateLoopPrerequisites.Check("OnLateUpdateLoop", CallbacksField);
    }

    static bool Prefix()
    {
        BUpdateLoopRunner.Run(CallbacksField, Invoke, "OnLateUpdateLoop");
        return false;
    }
}

[HarmonyPatch(typeof(BUpdate), "OnPreLateUpdateLoop")]
static class PatchBUpdateOnPreLateUpdateLoop
{
    private static readonly FieldInfo CallbacksField = AccessTools.Field(typeof(BUpdate), "preLateUpdateCallbacks");
    private static readonly Action<IPreLateBUpdateCallback> Invoke = callback => callback.OnPreLateBUpdate();

    static bool Prepare()
    {
        return BUpdateLoopPrerequisites.Check("OnPreLateUpdateLoop", CallbacksField);
    }

    static bool Prefix()
    {
        BUpdateLoopRunner.Run(CallbacksField, Invoke, "OnPreLateUpdateLoop");
        return false;
    }
}

[HarmonyPatch(typeof(BUpdate), "OnFixedUpdateLoop")]
static class PatchBUpdateOnFixedUpdateLoop
{
    private static readonly FieldInfo CallbacksField = AccessTools.Field(typeof(BUpdate), "fixedUpdateCallbacks");
    private static readonly Action<IFixedBUpdateCallback> Invoke = callback => callback.OnFixedBUpdate();

    static bool Prepare()
    {
        return BUpdateLoopPrerequisites.Check("OnFixedUpdateLoop", CallbacksField);
    }

    static bool Prefix()
    {
        BUpdateLoopRunner.Run(CallbacksField, Invoke, "OnFixedUpdateLoop");
        return false;
    }
}

[HarmonyPatch(typeof(BUpdate), "OnPostFixedUpdateLoop")]
static class PatchBUpdateOnPostFixedUpdateLoop
{
    private static readonly FieldInfo CallbacksField = AccessTools.Field(typeof(BUpdate), "postFixedUpdateCallbacks");
    private static readonly Action<IPostFixedBUpdateCallback> Invoke = callback => callback.OnPostFixedBUpdate();

    static bool Prepare()
    {
        return BUpdateLoopPrerequisites.Check("OnPostFixedUpdateLoop", CallbacksField);
    }

    static bool Prefix()
    {
        BUpdateLoopRunner.Run(CallbacksField, Invoke, "OnPostFixedUpdateLoop");
        return false;
    }
}

// NOTE, not patched: BUpdate.OnApplicationQuit() clears updateCallbacks, lateUpdateCallbacks,
// fixedUpdateCallbacks, postFixedUpdateCallbacks and invokeRepeating - but not
// preLateUpdateCallbacks. It's a genuine vanilla oversight, and it's left alone deliberately: the
// process is exiting, nothing reads the list afterwards, and the PlayerLoop is restored to default
// on the next line anyway.
//
// NOTE, not patched: BUpdateLoopType decompiles with PreLateUpdate, LateUpdate, FixedUpdate and
// PostFixedUpdate all equal to 0, which makes ContainsLoop() return true for all of them
// unconditionally, so Register/DeregisterCallback effectively ignore their mask argument. In
// practice every caller in the game passes BUpdateLoopType.All, so the observable behaviour is
// identical - and the failure direction is safe (over-registering and over-deregistering, never
// under-deregistering). Enum values are compile-time constants baked into every caller's IL, so
// this isn't meaningfully patchable from a mod regardless.
