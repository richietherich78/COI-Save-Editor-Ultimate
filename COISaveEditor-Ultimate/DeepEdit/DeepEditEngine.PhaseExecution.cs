using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Phase 1–3: Best-effort FinalizeLoading ────────────────────────────

    /// <summary>
    /// Replaces FinalizeLoading entirely.  The game's FinalizeLoading has three
    /// sequential phases and aborts on the FIRST failure in any phase.  We
    /// re-implement all three phases with PER-ITEM error handling so every object
    /// gets the best chance of being fully initialised.
    /// </summary>
    private HashSet<object> InvokeFinalizeLoadingBestEffort(object reader, object resolver, IProgress<string>? progress, System.Text.StringBuilder? detailLog = null, bool skipPhase3 = false)
    {
        var failedPhase1Objects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int phase1Ok = 0, phase1Fail = 0, phase1Timeout = 0;
        int phase2Ok = 0, phase2Fail = 0;
        int phase3Ok = 0, phase3Fail = 0, p3Timeout = 0, p3AutoSkip = 0;

        // ── Phase 1: Drain delayed reads ──────────────────────────────────
        progress?.Report("Phase 1: Draining delayed data deserializations…");
        var fiDelayed = _tBlobReader!.GetField("m_delayedDataDeserializations",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fiDelayed is not null)
        {
            var queue = fiDelayed.GetValue(reader);
            if (queue is not null)
            {
                var queueType = queue.GetType();
                var piIsNotEmpty = queueType.GetProperty("IsNotEmpty", BindingFlags.Public | BindingFlags.Instance)
                    ?? queueType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                var miDequeue = queueType.GetMethod("Dequeue", BindingFlags.Public | BindingFlags.Instance);

                if (piIsNotEmpty is not null && miDequeue is not null)
                {
                    bool HasItems()
                    {
                        var val = piIsNotEmpty.GetValue(queue);
                        if (val is bool b) return b;
                        if (val is int c) return c > 0;
                        return false;
                    }

                    int totalQueued = 0;
                    var piCount = queueType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                    if (piCount is not null)
                    {
                        var countVal = piCount.GetValue(queue);
                        if (countVal is int cnt) totalQueued = cnt;
                    }
                    if (totalQueued == 0) totalQueued = 20000;

                    FieldInfo? fiReadAction = null;
                    FieldInfo? fiObject = null;
                    MethodInfo? miInvokeCached = null;
                    bool recordTypeCached = false;
                    int totalProcessed = 0;
                    const int Phase1TimeoutMs = 5000;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    var timedOutTypes = new HashSet<string>(StringComparer.Ordinal);
                    var safeTypes = new HashSet<string>(StringComparer.Ordinal);

                    while (HasItems())
                    {
                        object? record = null;
                        try
                        {
                            record = miDequeue.Invoke(queue, null);
                        }
                        catch { break; }

                        if (record is null) break;
                        try
                        {
                            if (!recordTypeCached)
                            {
                                var recType = record.GetType();
                                fiReadAction = recType.GetField("ReadAction", BindingFlags.Public | BindingFlags.Instance);
                                fiObject = recType.GetField("Object", BindingFlags.Public | BindingFlags.Instance);
                                recordTypeCached = true;
                            }

                            var readAction = fiReadAction?.GetValue(record);
                            var targetObj  = fiObject?.GetValue(record);

                            if (readAction is not null && targetObj is not null)
                            {
                                miInvokeCached ??= readAction.GetType().GetMethod("Invoke");

                                string objTypeName = targetObj.GetType().Name;

                                if (timedOutTypes.Contains(objTypeName))
                                {
                                    phase1Timeout++;
                                    phase1Fail++;
                                    failedPhase1Objects.Add(targetObj);
                                    totalProcessed++;
                                    continue;
                                }

                                if (safeTypes.Contains(objTypeName))
                                {
                                    miInvokeCached?.Invoke(readAction, new[] { targetObj, reader });
                                }
                                else
                                {
                                    var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                                    var thread = new Thread(() =>
                                    {
                                        try { tcs.TrySetResult(miInvokeCached?.Invoke(readAction, new[] { targetObj, reader })); }
                                        catch (Exception ex) { tcs.TrySetException(ex); }
                                    });
                                    thread.IsBackground = true;
                                    thread.Start();

                                    try { tcs.Task.Wait(Phase1TimeoutMs); }
                                    catch { /* handled below */ }

                                    if (!tcs.Task.IsCompleted)
                                    {
                                        phase1Timeout++;
                                        phase1Fail++;
                                        failedPhase1Objects.Add(targetObj);
                                        timedOutTypes.Add(objTypeName);
                                        progress?.Report($"⏱ Phase 1 TIMEOUT: {objTypeName} — skipped after {Phase1TimeoutMs}ms (will auto-skip remaining)");
                                        detailLog?.AppendLine($"    Phase 1 timeout ({objTypeName}) — skipped after {Phase1TimeoutMs}ms, auto-skipping all remaining");
                                        totalProcessed++;
                                        continue;
                                    }

                                    if (tcs.Task.IsFaulted)
                                    {
                                        throw tcs.Task.Exception!.InnerExceptions[0];
                                    }

                                    safeTypes.Add(objTypeName);
                                }
                            }
                            phase1Ok++;
                        }
                        catch (TargetInvocationException tie)
                        {
                            phase1Fail++;
                            var inner = tie.InnerException;
                            var targetObj2 = fiObject?.GetValue(record);
                            var objName = targetObj2?.GetType().Name ?? "?";
                            if (targetObj2 is not null) failedPhase1Objects.Add(targetObj2);
                            detailLog?.AppendLine($"    Phase 1 skip ({objName}): {inner?.GetType().Name}: {inner?.Message}");
                        }
                        catch (Exception ex) when (ex is not ThreadAbortException)
                        {
                            phase1Fail++;
                            var targetObj2 = fiObject?.GetValue(record);
                            if (targetObj2 is not null) failedPhase1Objects.Add(targetObj2);
                            detailLog?.AppendLine($"    Phase 1 skip: {ex.GetType().Name}: {ex.Message}");
                        }

                        totalProcessed++;
                        if (sw.ElapsedMilliseconds > 500)
                        {
                            int pct = totalQueued > 0 ? Math.Min(100, (int)(totalProcessed * 100L / totalQueued)) : 0;
                            progress?.Report($"[PROGRESS:{pct}]");
                            progress?.Report($"Phase 1 progress: {totalProcessed}/{totalQueued} ({phase1Ok} OK, {phase1Fail} failed, {phase1Timeout} timeout) — {pct}%");
                            sw.Restart();
                        }
                    }
                }
            }
        }
        progress?.Report("[PROGRESS:100]");
        progress?.Report($"Phase 1 complete: {phase1Ok} OK, {phase1Fail} skipped, {phase1Timeout} timed out.");

        // ── Phase 2: Resolve members ──────────────────────────────────────

        // Diagnostic: check resolver state before Phase 2 begins.
        DiagnoseResolverState(resolver, progress);

        progress?.Report("[STEP:4:8:Phase 2 — Resolving members…]");
        progress?.Report("[PROGRESS:0]");
        progress?.Report("Phase 2: Resolving member fields from DependencyResolver…");
        var fiMembers = _tBlobReader!.GetField("m_membersToResolve",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fiMembers is not null)
        {
            var membersList = fiMembers.GetValue(reader);
            if (membersList is not null)
            {
                var items = (membersList as System.Collections.IEnumerable)?.Cast<object>().ToList();
                if (items is not null && items.Count > 0)
                {
                    int p2Total = items.Count, p2Done = 0;

                    var miResolve = resolver.GetType().GetMethod("Resolve",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(Type) }, null);
                    var miTryResolve = resolver.GetType().GetMethod("TryResolve",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(Type) }, null);

                    System.Collections.IList? readObjectsList = null;
                    if (_fiReadObjects is not null)
                    {
                        var readObjs = _fiReadObjects.GetValue(reader);
                        readObjectsList = readObjs as System.Collections.IList
                            ?? (readObjs as System.Collections.IEnumerable)?.Cast<object>().ToList();
                    }

                    // Memoize readObjectsList lookups: the first scan for a given resolvedType
                    // is O(n) over 734k objects; subsequent lookups for the same type are O(1).
                    // Without this, Phase 2 does 734k × 123k = ~90 billion type-checks (~36 min).
                    // With the cache, each distinct resolvedType scans at most once.
                    var readObjsCache = new Dictionary<Type, object?>();

                    // Cache per-item member struct FieldInfos — all members share the same struct
                    // type so these are looked up once and reused for all 124k+ iterations.
                    Type? lastMemberType = null;
                    FieldInfo? fiMemberObj = null, fiMemberName = null, fiMemberResolvedTy = null;
                    FieldInfo? fiMemberIsField = null, fiMemberDeclType = null, fiMemberConvert = null;

                    // Cache the Option<T> property infos for HasValue / Value — same type every time.
                    PropertyInfo? piOptionHasValue = null, piOptionValue = null;
                    PropertyInfo? piConvHasValue = null, piConvValue = null;

                    // Cache (Type, fieldName) → FieldInfo for the success-path FindFieldDeep calls.
                    var fieldDeepCache = new Dictionary<(Type, string), FieldInfo?>();

                    var p2Sw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var member in items)
                    {
                        p2Done++;
                        if (p2Sw.ElapsedMilliseconds > 500)
                        {
                            int pct = (int)(p2Done * 100L / p2Total);
                            progress?.Report($"[PROGRESS:{pct}]");
                            progress?.Report($"Phase 2 progress: {p2Done}/{p2Total} ({phase2Ok} OK, {phase2Fail} failed) — {pct}%");
                            p2Sw.Restart();
                        }
                        try
                        {
                            var memberType = member.GetType();
                            if (memberType != lastMemberType)
                            {
                                fiMemberObj       = memberType.GetField("Obj",                    BindingFlags.Public | BindingFlags.Instance);
                                fiMemberName      = memberType.GetField("Name",                   BindingFlags.Public | BindingFlags.Instance);
                                fiMemberResolvedTy = memberType.GetField("ResolvedType",          BindingFlags.Public | BindingFlags.Instance);
                                fiMemberIsField   = memberType.GetField("IsField",               BindingFlags.Public | BindingFlags.Instance);
                                fiMemberDeclType  = memberType.GetField("Type",                  BindingFlags.Public | BindingFlags.Instance);
                                fiMemberConvert   = memberType.GetField("ConvertBeforeAssignment", BindingFlags.Public | BindingFlags.Instance);
                                lastMemberType = memberType;
                            }

                            var obj        = fiMemberObj?.GetValue(member);
                            var name       = fiMemberName?.GetValue(member) as string;
                            var resolvedTy = fiMemberResolvedTy?.GetValue(member) as Type;
                            var isField    = fiMemberIsField?.GetValue(member) is true;
                            var declType   = fiMemberDeclType?.GetValue(member) as Type;

                            if (obj is null || name is null || resolvedTy is null) continue;

                            object? dep = null;
                            // When TryResolve is available, use it exclusively — Resolve() throws a
                            // DependencyResolverException for every unregistered type (~97% of items),
                            // and each caught exception costs ~10 ms, making Phase 2 take ~25 minutes.
                            // TryResolve returns Option{HasValue=false} instead of throwing, so we
                            // never fall back to Resolve once TryResolve has given us an answer.
                            bool tryResolveRan = false;
                            try
                            {
                                if (miTryResolve is not null)
                                {
                                    var optResult = miTryResolve.Invoke(resolver, new object[] { resolvedTy });
                                    tryResolveRan = true;
                                    if (optResult is not null)
                                    {
                                        piOptionHasValue ??= optResult.GetType().GetProperty("HasValue");
                                        // Option<T>.Value is a FIELD (not a property) — GetProperty("Value")
                                        // returns null, making dep permanently null. Use ValueOrNull which
                                        // is a public property returning the same backing field.
                                        piOptionValue    ??= optResult.GetType().GetProperty("ValueOrNull");
                                        if (piOptionHasValue?.GetValue(optResult) is true)
                                            dep = piOptionValue?.GetValue(optResult);
                                    }
                                }
                                if (!tryResolveRan)
                                    dep ??= miResolve?.Invoke(resolver, new object[] { resolvedTy });
                            }
                            catch (TargetInvocationException) { }
                            catch { }

                            if (dep is null && readObjectsList is not null)
                            {
                                if (!readObjsCache.TryGetValue(resolvedTy, out dep))
                                {
                                    dep = null;
                                    foreach (var candidate in readObjectsList)
                                    {
                                        if (candidate is not null && resolvedTy.IsAssignableFrom(candidate.GetType()))
                                        {
                                            dep = candidate;
                                            break;
                                        }
                                    }
                                    readObjsCache[resolvedTy] = dep; // cache null too — avoids re-scanning
                                }
                            }

                            if (dep is null)
                            {
                                phase2Fail++;
                                detailLog?.AppendLine($"    Phase 2 skip: [{obj.GetType().Name}].{name} — " +
                                    $"could not resolve {resolvedTy.Name}");
                                continue;
                            }

                            if (fiMemberConvert is not null)
                            {
                                var optConvert = fiMemberConvert.GetValue(member);
                                if (optConvert is not null)
                                {
                                    piConvHasValue ??= optConvert.GetType().GetProperty("HasValue");
                                    piConvValue    ??= optConvert.GetType().GetProperty("ValueOrNull");
                                    if (piConvHasValue?.GetValue(optConvert) is true)
                                    {
                                        var converter = piConvValue?.GetValue(optConvert) as Func<object, object>;
                                        if (converter is not null)
                                            dep = converter(dep);
                                    }
                                }
                            }

                            var targetType = declType ?? obj.GetType();
                            if (isField)
                            {
                                var key = (targetType, name);
                                if (!fieldDeepCache.TryGetValue(key, out var fi))
                                    fieldDeepCache[key] = fi = FindFieldDeep(targetType, name);
                                fi?.SetValue(obj, dep);
                            }
                            else
                            {
                                var pi = targetType.GetProperty(name,
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                pi?.SetValue(obj, dep);
                            }
                            phase2Ok++;
                        }
                        catch (TargetInvocationException tie)
                        {
                            phase2Fail++;
                            var inner = tie.InnerException ?? tie;
                            var objName = "?";
                            try { objName = fiMemberObj?.GetValue(member)?.GetType().Name ?? "?"; } catch { }
                            var fieldName = fiMemberName?.GetValue(member) as string ?? "?";
                            detailLog?.AppendLine($"    Phase 2 skip: [{objName}].{fieldName} — {inner.GetType().Name}: {inner.Message}");
                        }
                        catch (Exception ex)
                        {
                            phase2Fail++;
                            detailLog?.AppendLine($"    Phase 2 skip: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    var miClear = membersList.GetType().GetMethod("Clear", Type.EmptyTypes)
                        ?? membersList.GetType().GetMethod("Clear");
                    miClear?.Invoke(membersList, null);
                }
            }
        }
        progress?.Report($"Phase 2 complete: {phase2Ok} resolved, {phase2Fail} skipped.");

        // ── Phase 3: InitAfterLoad (best-effort) ─────────────────────────
        // Skip when using a populated resolver: initSelf callbacks do real work
        // (register into PowerManager, WaterPollutionManager, etc.), and that
        // manager state gets serialised into the save.  On game-side reload the
        // type layout doesn't match, producing CorruptedSaveException.
        // The game runs its own Phase 3 from a clean state on load, so we never
        // need to run it here when a real resolver is available.
        if (skipPhase3)
        {
            progress?.Report("Phase 3: SKIPPED (populated resolver in use — game will run Phase 3 on load).");
            return failedPhase1Objects;
        }

        progress?.Report("[STEP:4:8:Phase 3 — InitAfterLoad…]");
        progress?.Report("[PROGRESS:0]");
        progress?.Report("Phase 3: Running InitAfterLoad callbacks (best-effort)…");
        var fiInit = _tBlobReader!.GetField("m_objsToInit",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fiInit is not null)
        {
            var initList = fiInit.GetValue(reader);
            if (initList is not null)
            {
                var items = (initList as System.Collections.IEnumerable)?.Cast<object>().ToList();
                if (items is not null && items.Count > 0)
                {
                    // CRITICAL: the game sorts m_objsToInit by InitPriority before iterating
                    // (see BlobReader.FinalizeLoading: m_objsToInit.OrderBy(x => x.Priority)).
                    // Iterating in raw insertion order causes Dict<,>.initAfterLoad (Highest=1)
                    // to run AFTER consumers like PropsDb.initSelf (Normal=10), producing
                    // cascade failures: "Inserting to a loaded dict that was not initialized yet."
                    // Sort by the same Priority field, falling back to original index for stability.
                    var fiPriorityField = items[0].GetType().GetField("Priority",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (fiPriorityField is not null)
                    {
                        items = items
                            .Select((it, idx) => (it, idx, prio: Convert.ToInt32(fiPriorityField.GetValue(it))))
                            .OrderBy(t => t.prio)
                            .ThenBy(t => t.idx)
                            .Select(t => t.it)
                            .ToList();
                    }

                    int p3Total = items.Count, p3Done = 0;
                    const int TimeoutMs = 3000;
                    const int MaxTimeoutsPerMethod = 1;
                    var timeoutCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var failCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    // True (post-cap) totals for per-method failures and auto-skips.  failCounts
                    // freezes at MaxFailsPerMethod by design (it gates the catch path), so we
                    // need a separate counter to know how many entities are *really* affected.
                    var trueFailCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var autoSkipCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    const int MaxFailsPerMethod = 10;
                    // Once any method times out, a leaked thread may be holding a Mafi lock.
                    // We stop re-populating _phase3SafeMethods so all subsequent calls remain
                    // under the timeout guard for the rest of this run.
                    bool anyTimeoutOccurred = false;
                    progress?.Report($"Phase 3: {p3Total} InitAfterLoad callbacks to process…");
                    var p3Sw = System.Diagnostics.Stopwatch.StartNew();

                    // Memory cap: with a populated resolver, every initSelf does real work
                    // (manager registrations, listener allocations, graph mutations).
                    // For huge saves with hundreds of thousands of entities this can blow
                    // past 20+ GB. Abort Phase 3 once the process crosses the cap and ship
                    // a partially-init save rather than OOMing the box.
                    const long Phase3MemoryCapBytes = 6L * 1024 * 1024 * 1024; // 6 GB
                    bool p3MemoryCapReached = false;

                    foreach (var initData in items)
                    {
                        p3Done++;
                        string typeName = "?", methName = "?";
                        if (p3Sw.ElapsedMilliseconds > 500)
                        {
                            int pct = (int)(p3Done * 100L / p3Total);
                            long workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
                            progress?.Report($"[PROGRESS:{pct}]");
                            progress?.Report($"Phase 3 progress: {p3Done}/{p3Total} ({phase3Ok} OK, {phase3Fail} fail, {p3Timeout} timeout, {p3AutoSkip} auto-skip) — {pct}% [RAM: {workingSet / (1024 * 1024)} MB]");
                            p3Sw.Restart();

                            if (workingSet > Phase3MemoryCapBytes)
                            {
                                p3MemoryCapReached = true;
                                progress?.Report($"⚠ Phase 3 memory cap hit ({workingSet / (1024 * 1024)} MB > {Phase3MemoryCapBytes / (1024 * 1024)} MB cap) — aborting remaining {p3Total - p3Done} init calls.");
                                break;
                            }
                        }
                        try
                        {
                            var initType   = initData.GetType();
                            var obj        = initType.GetField("Obj",        BindingFlags.Public | BindingFlags.Instance)?.GetValue(initData);
                            var methodName = initType.GetField("MethodName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(initData) as string;
                            var declType   = initType.GetField("Type",       BindingFlags.Public | BindingFlags.Instance)?.GetValue(initData) as Type;

                            if (obj is null || methodName is null) continue;

                            var targetType = declType ?? obj.GetType();
                            typeName = targetType.Name;
                            methName = methodName;

                            string methodKey = $"{typeName}.{methName}";
                            if (timeoutCounts.TryGetValue(methodKey, out int prevTimeouts) && prevTimeouts >= MaxTimeoutsPerMethod)
                            {
                                p3AutoSkip++;
                                phase3Fail++;
                                continue;
                            }
                            if (failCounts.TryGetValue(methodKey, out int prevFails) && prevFails >= MaxFailsPerMethod)
                            {
                                p3AutoSkip++;
                                phase3Fail++;
                                autoSkipCounts[methodKey] = (autoSkipCounts.TryGetValue(methodKey, out int asc) ? asc : 0) + 1;
                                trueFailCounts[methodKey] = (trueFailCounts.TryGetValue(methodKey, out int tfc) ? tfc : 0) + 1;
                                continue;
                            }

                            var mi = targetType.GetMethod(methodName,
                                BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                BindingFlags.Public | BindingFlags.NonPublic);
                            if (mi is null) continue;

                            var parms = mi.GetParameters();
                            object?[] args;
                            if (parms.Length == 0)
                            {
                                args = Array.Empty<object>();
                            }
                            else
                            {
                                args = new object?[parms.Length];
                                for (int i = 0; i < parms.Length; i++)
                                {
                                    if (parms[i].ParameterType == typeof(int))
                                        args[i] = 0;
                                    else if (_tDependencyResolver!.IsAssignableFrom(parms[i].ParameterType))
                                        args[i] = resolver;
                                    else
                                        args[i] = parms[i].ParameterType.IsValueType
                                            ? Activator.CreateInstance(parms[i].ParameterType)
                                            : null;
                                }
                            }

                            if (!anyTimeoutOccurred && _phase3SafeMethods.Contains(methodKey))
                            {
                                // Fast path: proven safe before any timeout occurred.
                                mi.Invoke(obj, args);
                                phase3Ok++;
                            }
                            else
                            {
                                Exception? threadEx = null;
                                var thread = new Thread(() =>
                                {
                                    try { mi.Invoke(obj, args); }
                                    catch (Exception ex) { threadEx = ex; }
                                });
                                thread.IsBackground = true;
                                thread.Start();
                                bool joined = thread.Join(TimeoutMs);

                                if (!joined)
                                {
                                    p3Timeout++;
                                    // A leaked thread may now hold a Mafi lock.
                                    // Never re-use the safe path for the rest of this run.
                                    anyTimeoutOccurred = true;
                                    _phase3SafeMethods.Clear();

                                    string diagInfo;
                                    try
                                    {
                                        var sb = new System.Text.StringBuilder();
                                        sb.AppendLine($"  Object type: {obj.GetType().FullName}");
                                        sb.AppendLine($"  Method: {mi.DeclaringType?.FullName}.{mi.Name}");
                                        sb.AppendLine($"  Thread state: {thread.ThreadState}");
                                        var fields = obj.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                                        var nullFields = fields.Where(f =>
                                        {
                                            try { return !f.FieldType.IsValueType && f.GetValue(obj) is null; }
                                            catch { return false; }
                                        }).Select(f => f.Name).Take(20).ToList();
                                        if (nullFields.Count > 0)
                                            sb.AppendLine($"  Null instance fields: {string.Join(", ", nullFields)}");
                                        diagInfo = sb.ToString();
                                    }
                                    catch (Exception diagEx) { diagInfo = $"(diagnostics failed: {diagEx.Message})"; }

                                    timeoutCounts[methodKey] = (timeoutCounts.TryGetValue(methodKey, out int tc2) ? tc2 : 0) + 1;
                                    if (timeoutCounts[methodKey] >= MaxTimeoutsPerMethod)
                                    {
                                        progress?.Report($"⏱ Phase 3 TIMEOUT: {methodKey} — hit {MaxTimeoutsPerMethod} timeouts, auto-skipping all remaining instances");
                                        detailLog?.AppendLine($"    Phase 3 timeout ({methodKey}) — reached {MaxTimeoutsPerMethod} timeouts, will auto-skip remaining");
                                        detailLog?.AppendLine($"    Last hung diagnostics:\n{diagInfo}");
                                    }
                                    else
                                    {
                                        progress?.Report($"⏱ Phase 3 TIMEOUT: {methodKey} — skipped after {TimeoutMs}ms ({timeoutCounts[methodKey]}/{MaxTimeoutsPerMethod})");
                                        detailLog?.AppendLine($"    Phase 3 timeout ({methodKey}) — skipped after {TimeoutMs}ms");
                                        detailLog?.AppendLine($"    Hung thread diagnostics:\n{diagInfo}");
                                    }
                                    continue;
                                }

                                if (threadEx is not null)
                                    throw threadEx;

                                // Only cache as safe while no timeout has occurred.
                                // Once a thread leaks, we keep every call guarded.
                                if (!anyTimeoutOccurred)
                                    _phase3SafeMethods.Add(methodKey);
                                phase3Ok++;
                            }
                        }
                        catch (Exception catchEx)
                        {
                            phase3Fail++;
                            var innerEx = (catchEx as TargetInvocationException)?.InnerException ?? catchEx;
                            string methodKey = $"{typeName}.{methName}";
                            int newCount = (failCounts.TryGetValue(methodKey, out int fc) ? fc : 0) + 1;
                            failCounts[methodKey] = newCount;
                            trueFailCounts[methodKey] = (trueFailCounts.TryGetValue(methodKey, out int tfc) ? tfc : 0) + 1;
                            detailLog?.AppendLine($"    Phase 3 exception ({methodKey}): {innerEx.GetType().Name}: {innerEx.Message}");
                            // First failure of each kind: also log the full stack trace so we can
                            // pinpoint exactly which line in the game code NREs. Cheap (one entry
                            // per method-key) and invaluable for diagnosing init failures.
                            if (newCount == 1 && innerEx.StackTrace is { Length: > 0 } st)
                            {
                                detailLog?.AppendLine($"      first-stack ({methodKey}):");
                                foreach (var line in st.Split('\n'))
                                    detailLog?.AppendLine($"        {line.TrimEnd('\r')}");
                            }
                            if (newCount == MaxFailsPerMethod)
                            {
                                progress?.Report($"⚠ Phase 3: {methodKey} — hit {MaxFailsPerMethod} failures, auto-skipping remaining instances");
                            }
                        }
                    }

                    // ── Log summary of auto-skipped methods ──────────────────────
                    if (timeoutCounts.Count > 0 || failCounts.Any(kv => kv.Value >= MaxFailsPerMethod))
                    {
                        detailLog?.AppendLine("    ── Phase 3 auto-skip summary ──");
                        foreach (var kv in timeoutCounts.Where(kv => kv.Value >= MaxTimeoutsPerMethod))
                            detailLog?.AppendLine($"    TIMEOUT auto-skip: {kv.Key} (timed out {kv.Value}x)");
                        foreach (var kv in failCounts.Where(kv => kv.Value >= MaxFailsPerMethod)
                                                    .OrderByDescending(kv => trueFailCounts.TryGetValue(kv.Key, out var t) ? t : kv.Value))
                        {
                            int trueTotal = trueFailCounts.TryGetValue(kv.Key, out var t2) ? t2 : kv.Value;
                            int autoSkipped = autoSkipCounts.TryGetValue(kv.Key, out var ask) ? ask : 0;
                            detailLog?.AppendLine($"    FAILURE auto-skip: {kv.Key} (failed {trueTotal}x total — {kv.Value} logged, {autoSkipped} auto-skipped)");
                        }
                    }

                    var miClear = initList.GetType().GetMethod("Clear", Type.EmptyTypes)
                        ?? initList.GetType().GetMethod("Clear");
                    miClear?.Invoke(initList, null);
                }
            }
        }
        progress?.Report($"Phase 3 complete: {phase3Ok} OK, {phase3Fail} skipped, {p3Timeout} timed out, {p3AutoSkip} auto-skipped.");
        return failedPhase1Objects;
    }
}
