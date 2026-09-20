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

    public static void Warn(string key, string message)
    {
        float now = Time.unscaledTime;
        float last;
        if (LastLoggedAt.TryGetValue(key, out last) && now - last < ThrottleSeconds)
        {
            return;
        }

        LastLoggedAt[key] = now;

        // Null-conditional: a patch can in principle fire before the plugin's Awake has run.
        MoreGolfersPlugin.Logger?.LogWarning(message);
    }
}
