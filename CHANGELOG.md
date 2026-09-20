DISCLAIMER - ALL CHANGES WERE MADE BY CLAUDE AI (SONNET) 

ALL CREDIT GOES TO SULAYRE AND CATALYSS
https://github.com/Sulayre/
https://github.com/Catalyss

**Tee placement**
- Fixed golfers teeing off from the centre of the platform and drifting rightwards off the edge. Tees now anchor to the platform's left end and fill right, and every layout is fitted to the platform width, so a tee can no longer be placed off the platform at any player count.

**Hole progress bar (the repeating NullReferenceException)**
- Fixed a `NullReferenceException` spamming from `HoleProgressBarPlayerEntryUi.OnLateUpdate` during hole transitions. Three vanilla lifecycle defects compound once the lobby outgrows the pool: a pooled entry was destroyed as a *component* and then pooled anyway (leaking its GameObject and filling the pool with dead entries), the pool's null check ran after the reference it was meant to guard, and the scene-change handler pooled every entry without clearing the collections still pointing at them.
- Pooled entries no longer retain a reference to the player they last represented.

**Object pools**
- Progress-bar entry and hit-tee pools are now sized from the configured player limit instead of vanilla's 16-player cap, removing the destroy/re-instantiate churn on every hole transition and tee-off.

**BUpdate loop robustness**
- All five `BUpdate` loops now isolate each callback in its own try/catch. Previously only `OnUpdateLoop` was protected - and every logged exception in testing came through `OnLateUpdateLoop`, which had none. Because the callback lists are static and survive scene loads, one throwing callback would otherwise skip every callback below it in the list on every subsequent frame, permanently.
- The loops also now tolerate a callback that deregisters several others mid-tick (previously an `ArgumentOutOfRangeException`), skip destroyed `MonoBehaviour`s that never deregistered, and survive `CancelInvoke` being called from inside an `InvokeRepeating` callback (previously an `InvalidOperationException`).
- Each of these patches declines to apply if it cannot resolve what it needs, rather than replacing a loop with nothing.

**Golf cart network traffic**
- Confirmed via Mirror's own generated serialization code that `EntityStateMessage` is a stock Mirror message (`Mirror.EntityStateMessage`), not custom game code: a generic per-object envelope carrying the combined state of every `NetworkBehaviour` on that object. This makes the `GolfCartMovement`/`GolfCartInfo` sync-interval throttle below a confirmed reduction in real `EntityStateMessage` traffic, not just a plausible one.
- Extended the same throttle to `Entity.NetworkRigidbody` (type `NetworkRigidbodyUnreliable`), scoped specifically to entities where `IsGolfCart` is true. The Baseline/Delta unreliable message variants Mirror generates match exactly the pattern a continuously-changing rigidbody position/velocity sync would use, and this is the component `Entity.HasRigidbodyAuthority` already treats as the cart's live sync target. Fetched by reflection rather than a typed property access: `NetworkRigidbodyUnreliable` lives in `Mirror.Components`, a separate assembly this project doesn't reference, which the first version of this patch missed (confirmed by a real `CS0012` build failure). Reflection sidesteps that at the compile-time boundary entirely rather than adding a `<Reference>` for a specific DLL path that might not match every install.
- Each golf cart's own `[SyncVar]` broadcasts (`GolfCartMovement`'s accelerating/braking/steering, `GolfCartInfo`'s driver seat/team state) and its rigidbody position/velocity sync now all slow down together as the configured player cap rises above vanilla's 16, capped at a 3x interval increase. At vanilla's own player count this changes nothing.
- This remains a real, verified mitigation, not a confirmed complete fix. `PredictedGolfCart` and `PhysicsManager`'s own role, if any, in the flood is still unverified - their source hasn't been supplied - so this should be judged by testing, not assumed to fully resolve the crash.

**Dead code removed**
- Removed a disabled `PlayerOcclusionManager.Awake` patch (and its blocking TODO) that assumed the class's native collections had a hard capacity tied to vanilla's 16-player cap. With the real source now available: `TransformAccessArray`/`NativeList<T>` there are only ever given an initial-capacity *hint* at construction: every add goes through their own auto-growing `.Add()`, and every later read uses the live count. There was never a cap to patch.
