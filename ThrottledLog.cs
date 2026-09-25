using System.Collections.Generic;
using UnityEngine;

namespace MoreGolfers;

// Shared throttled logger for patches that guard per-frame code paths.
//
// Without throttling, a warning from inside an update loop reproduces exactly the spam it exists
// to stop: the bug that prompted these patches logged one full stack trace per orphaned UI entry
// per frame, for the whole duration of a hole transition. A guard that logged unconditionally
// would be no better than the exception it replaced.
//
// Keyed rather than global so an unrelated second problem isn't silenced by the first one's
// throttle window.
internal static class ThrottledLog
{
    private const float ThrottleSeconds = 5f;
    private static readonly Dictionary<string, float> LastLoggedAt = new Dictionary<string, float>();

    // Split out of Warn so a caller whose message is expensive to build (string interpolation,
    // concatenation of non-constant values) can check this FIRST and only build the message inside
    // the resulting `if` - rather than paying that cost on every call and discarding the result on
    // the ones the throttle window would have skipped anyway. A caller passing a message that's
    // already a compile-time-constant string literal (no interpolation, no non-constant
    // concatenation - the compiler constant-folds it to one literal with zero runtime cost) has no
    // reason to bother with this and can keep calling Warn directly, exactly as before.
    public static bool ShouldLog(string key)
    {
        float now = Time.unscaledTime;
        float last;
        if (LastLoggedAt.TryGetValue(key, out last) && now - last < ThrottleSeconds)
        {
            return false;
        }

        LastLoggedAt[key] = now;
        return true;
    }

    public static void Warn(string key, string message)
    {
        if (!ShouldLog(key))
        {
            return;
        }

        // Null-conditional: a patch can in principle fire before the plugin's Awake has run.
        MoreGolfersPlugin.Logger?.LogWarning(message);
    }
}
