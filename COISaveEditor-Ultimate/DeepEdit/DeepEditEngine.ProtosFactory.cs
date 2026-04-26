using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Protos serializer factory construction ────────────────────────────

    /// <summary>
    /// Builds an ImmutableArray&lt;ISpecialSerializerFactory&gt; containing a
    /// ProtosSerializerFactory that handles all Proto-derived types.
    /// </summary>
    private object? BuildGameSpecialSerializersArray(IProgress<string>? progress)
    {
        try
        {
            var protoFactory = BuildProtosSerializerFactory(progress);
            if (protoFactory is null) return null;

            var tISSF = AssemblyLoader.FindType("Mafi.Serialization.ISpecialSerializerFactory");
            if (tISSF is null) { progress?.Report("  Note: ISpecialSerializerFactory type not found."); return null; }

            var arr = Array.CreateInstance(tISSF, 1);
            arr.SetValue(protoFactory, 0);

            var immArray = CreateImmutableArray(tISSF, arr);
            progress?.Report("  Game special serializers array built successfully.");
            return immArray;
        }
        catch (Exception ex)
        {
            progress?.Report($"  Note: Could not build game serializers array: {ex.Message}");
            return null;
        }
    }

    private object? BuildProtosSerializerFactory(IProgress<string>? progress)
    {
        var tProtosSerializerFactory = AssemblyLoader.FindType("Mafi.Core.Prototypes.ProtosSerializerFactory");
        if (tProtosSerializerFactory is null)
        {
            progress?.Report("  Note: ProtosSerializerFactory type not found.");
            return null;
        }

        object? realFactory = null;
        try
        {
            realFactory = BuildRealProtosSerializerFactory(tProtosSerializerFactory, progress);
        }
        catch (Exception ex)
        {
            progress?.Report($"  Populated ProtosDb failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (realFactory is not null)
            return realFactory;

        progress?.Report("  Falling back to hollow ProtosSerializerFactory (protos will be null)…");
        return BuildHollowProtosSerializerFactory(tProtosSerializerFactory, progress);
    }

    private object? BuildRealProtosSerializerFactory(Type tProtosSerializerFactory, IProgress<string>? progress)
    {
        progress?.Report("  Building populated ProtosDb for proto serialization…");

        var tProtosDb = AssemblyLoader.FindType("Mafi.Core.Prototypes.ProtosDb")
            ?? throw new InvalidOperationException("ProtosDb type not found.");
        var tCoreMod = AssemblyLoader.FindType("Mafi.Core.CoreMod")
            ?? throw new InvalidOperationException("CoreMod type not found.");
        var tBaseMod = AssemblyLoader.FindType("Mafi.Base.BaseMod")
            ?? throw new InvalidOperationException("BaseMod type not found.");
        var tCoreModConfig = AssemblyLoader.FindType("Mafi.Core.CoreModConfig")
            ?? throw new InvalidOperationException("CoreModConfig type not found.");
        var tBaseModConfig = AssemblyLoader.FindType("Mafi.Base.BaseModConfig")
            ?? throw new InvalidOperationException("BaseModConfig type not found.");
        var tModManifest = AssemblyLoader.FindType("Mafi.Core.Mods.ModManifest")
            ?? throw new InvalidOperationException("ModManifest type not found.");
        var tIMod = AssemblyLoader.FindType("Mafi.Core.Mods.IMod")
            ?? throw new InvalidOperationException("IMod type not found.");
        var tIConfig = AssemblyLoader.FindType("Mafi.Core.Game.IConfig")
            ?? throw new InvalidOperationException("IConfig type not found.");
        var tIEntityLayoutParser = AssemblyLoader.FindType("Mafi.Core.Entities.Static.Layout.IEntityLayoutParser")
            ?? throw new InvalidOperationException("IEntityLayoutParser type not found.");
        var tGameBuilder = AssemblyLoader.FindType("Mafi.Core.Game.GameBuilder")
            ?? throw new InvalidOperationException("GameBuilder type not found.");
        var tLoadedModData = AssemblyLoader.FindType("Mafi.Core.Mods.LoadedModData")
            ?? throw new InvalidOperationException("LoadedModData type not found.");

        var protosDbCtor = tProtosDb.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (protosDbCtor is null)
            throw new InvalidOperationException("No ProtosDb constructor found.");
        var protosDbCtorParams = protosDbCtor.GetParameters();
        var protosDbArgs = new object?[protosDbCtorParams.Length];
        for (int i = 0; i < protosDbCtorParams.Length; i++)
            protosDbArgs[i] = protosDbCtorParams[i].HasDefaultValue ? protosDbCtorParams[i].DefaultValue : null;
        var protosDb = protosDbCtor.Invoke(protosDbArgs)!;
        progress?.Report("    ProtosDb created (empty).");

        // Prefer the REAL EntityLayoutParser: it parses actual layout strings so that
        // layout-dependent Data classes (TransportsData, BuildingsData, etc.) can register
        // all their protos.  FakeEntityLayoutParser returns stub/null layouts which causes
        // ProtoBuilderException in TransportsData and stops BaseMod from registering any
        // further Data classes — leaving ShipyardProto, CaptainOfficeProto, GoalListProto,
        // TruckProto etc. absent from ProtosDb and unrecoverable by the healing pass.
        object layoutParser;
        var tRealParser = AssemblyLoader.FindType("Mafi.Core.Entities.Static.Layout.EntityLayoutParser");
        var tFakeParser = AssemblyLoader.FindType("Mafi.Core.Entities.Static.Layout.FakeEntityLayoutParser");
        if (tRealParser is not null)
        {
            try
            {
                // EntityLayoutParser takes ProtosDb as its constructor argument.
                // Use reflection to pick the most-param constructor and fill it, passing
                // protosDb for any IProtosDb/ProtosDb-assignable parameter.
                var parserCtor = tRealParser.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .OrderByDescending(c => c.GetParameters().Length)
                    .First();
                var parserParams = parserCtor.GetParameters();
                var parserArgs = new object?[parserParams.Length];
                for (int i = 0; i < parserParams.Length; i++)
                {
                    var pt = parserParams[i].ParameterType;
                    if (pt.IsAssignableFrom(tProtosDb))
                        parserArgs[i] = protosDb;
                    else
                        parserArgs[i] = parserParams[i].HasDefaultValue ? parserParams[i].DefaultValue
                            : pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }
                layoutParser = parserCtor.Invoke(parserArgs)!;
                progress?.Report("    Using EntityLayoutParser (real — parses layout strings).");
            }
            catch (Exception ex)
            {
                progress?.Report($"    EntityLayoutParser init failed ({ex.InnerException?.Message ?? ex.Message}), falling back to Fake.");
                if (tFakeParser is not null)
                {
                    layoutParser = Activator.CreateInstance(tFakeParser)!;
                    progress?.Report("    Using FakeEntityLayoutParser (fallback — TransportsData may fail).");
                }
                else
                    throw new InvalidOperationException("Neither EntityLayoutParser nor FakeEntityLayoutParser available.", ex);
            }
        }
        else if (tFakeParser is not null)
        {
            layoutParser = Activator.CreateInstance(tFakeParser)!;
            progress?.Report("    Using FakeEntityLayoutParser (real parser not found — TransportsData may fail).");
        }
        else
        {
            throw new InvalidOperationException("No IEntityLayoutParser implementation found in loaded assemblies.");
        }

        var miCreateEmpty = tModManifest.GetMethod("CreateEmpty", BindingFlags.Public | BindingFlags.Static);
        if (miCreateEmpty is null)
            throw new InvalidOperationException("ModManifest.CreateEmpty not found.");
        var coreManifest = miCreateEmpty.Invoke(null, new object[] { "COI-Core" })!;
        var baseManifest = miCreateEmpty.Invoke(null, new object[] { "COI-CoreData" })!;

        var coreConfig = Activator.CreateInstance(tCoreModConfig)!;
        var baseConfig = Activator.CreateInstance(tBaseModConfig)!;

        var coreMod = Activator.CreateInstance(tCoreMod, coreManifest, coreConfig)!;
        var baseMod = Activator.CreateInstance(tBaseMod, baseManifest, baseConfig)!;
        progress?.Report("    CoreMod + BaseMod instantiated.");

        // Discover additional DLC/data-only mods from loaded assemblies.
        var allMods = new List<object> { coreMod, baseMod };
        var allModTypes = new List<Type> { tCoreMod, tBaseMod };
        var allConfigs = new List<object> { coreConfig, baseConfig };
        var dlcMods = DiscoverAdditionalMods(tIMod, tModManifest, miCreateEmpty, progress);
        foreach (var (dlcMod, dlcModType) in dlcMods)
        {
            allMods.Add(dlcMod);
            allModTypes.Add(dlcModType);
            // DLC mods typically have no IConfig; add a null placeholder that
            // RegisterModsPrototypes ignores (configs array is parallel to mods).
            allConfigs.Add(null!);
        }

        var modsArr = Array.CreateInstance(tIMod, allMods.Count);
        for (int i = 0; i < allMods.Count; i++)
            modsArr.SetValue(allMods[i], i);
        var immMods = CreateImmutableArray(tIMod, modsArr);

        var tOptionType = AssemblyLoader.FindType("Mafi.Option`1")!;

        var modsDataArr = Array.CreateInstance(tLoadedModData, allMods.Count);
        for (int i = 0; i < allMods.Count; i++)
        {
            var manifest = (i == 0) ? coreManifest : miCreateEmpty.Invoke(null, new object[] { $"COI-Mod-{i}" })!;
            if (i == 0) manifest = coreManifest;
            else if (i == 1) manifest = baseManifest;
            modsDataArr.SetValue(CreateLoadedModData(tLoadedModData, tOptionType, manifest, allModTypes[i]), i);
        }
        var immModsData = CreateImmutableArray(tLoadedModData, modsDataArr);

        var configsArr = Array.CreateInstance(tIConfig, allConfigs.Count);
        for (int i = 0; i < allConfigs.Count; i++)
        {
            if (allConfigs[i] is not null && tIConfig.IsInstanceOfType(allConfigs[i]))
                configsArr.SetValue(allConfigs[i], i);
        }
        var immConfigs = CreateImmutableArray(tIConfig, configsArr);

        var miRegister = tGameBuilder.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "RegisterModsPrototypes")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 6 && tProtosDb.IsAssignableFrom(p[3].ParameterType);
            });
        if (miRegister is null)
            throw new InvalidOperationException("GameBuilder.RegisterModsPrototypes (6-param overload) not found.");

        var regParams = miRegister.GetParameters();
        var regArgs = new object?[regParams.Length];
        regArgs[0] = immMods;
        regArgs[1] = immModsData;
        regArgs[2] = immConfigs;
        regArgs[3] = protosDb;
        regArgs[4] = layoutParser;
        regArgs[5] = false;
        for (int i = 6; i < regParams.Length; i++)
        {
            regArgs[i] = regParams[i].HasDefaultValue ? regParams[i].DefaultValue
                : regParams[i].ParameterType.IsValueType ? Activator.CreateInstance(regParams[i].ParameterType)
                : null;
        }

        progress?.Report($"    Registering prototypes ({allMods.Count} mod(s))…");
        try
        {
            var errors = miRegister.Invoke(null, regArgs);

            if (errors is System.Collections.IEnumerable errorList)
            {
                int errCount = 0;
                foreach (var err in errorList)
                {
                    var msg = err?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        errCount++;
                        if (errCount <= 10)
                        {
                            progress?.Report($"    Proto registration note: {msg}");
                            // Try to surface inner exception (RegisterModsPrototypes typically
                            // returns an Either/error wrapper that hides the real cause).
                            TryReportInnerException(err!, progress);
                        }
                    }
                }
                if (errCount > 10)
                    progress?.Report($"    … and {errCount - 10} more registration note(s).");
            }
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            progress?.Report($"    ⚠ RegisterModsPrototypes threw: {inner.GetType().Name}: {inner.Message}");
            progress?.Report($"    ⚠ Registration may be partial — some protos will be phantom.");
        }

        int protoCount = 0;
        try
        {
            var miAll = tProtosDb.GetMethod("All", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (miAll is not null)
            {
                var allProtos = miAll.Invoke(protosDb, null) as System.Collections.IEnumerable;
                if (allProtos is not null)
                    protoCount = allProtos.Cast<object>().Count();
            }
        }
        catch { }
        progress?.Report($"    ProtosDb populated: {protoCount} prototype(s) registered.");

        var ctor = tProtosSerializerFactory.GetConstructor(new[] { tProtosDb });
        if (ctor is null)
            throw new InvalidOperationException("ProtosSerializerFactory(ProtosDb) constructor not found.");
        var factory = ctor.Invoke(new[] { protosDb });
        progress?.Report("  ProtosSerializerFactory created with REAL populated ProtosDb.");

        PatchFactoryForTypeSafeResolution(factory, tProtosSerializerFactory, protosDb, tProtosDb, progress);

        _populatedProtosDb = protosDb;

        return factory;
    }

    private void PatchFactoryForTypeSafeResolution(
        object factory, Type tFactory, object protosDb, Type tProtosDb, IProgress<string>? progress)
    {
        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        if (tProto is null) { progress?.Report("    Note: Proto type not found, skipping factory patch."); return; }

        var miReadStringNoRef = _tBlobReader!.GetMethod("ReadStringNoRef",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (miReadStringNoRef is null) { progress?.Report("    Note: ReadStringNoRef not found, skipping factory patch."); return; }

        var fiCustom = tFactory.GetField("m_customNewObjFactory", BindingFlags.NonPublic | BindingFlags.Instance);
        var fiFallback = tFactory.GetField("m_fallbackObjFactory", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fiCustom is null) { progress?.Report("    Note: m_customNewObjFactory field not found."); return; }

        var tProtoID = tProto.GetNestedType("ID", BindingFlags.Public);
        var ctorProtoID = tProtoID?.GetConstructor(new[] { typeof(string) });
        var fiProtoId = tProto.GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        var miTryGetProto = tProtosDb.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TryGetProto" && m.IsGenericMethodDefinition)
            ?.MakeGenericMethod(tProto);

        if (tProtoID is null || ctorProtoID is null || fiProtoId is null || miTryGetProto is null)
        {
            progress?.Report("    Note: Could not resolve Proto.ID internals — falling back to null-returning stubs.");
            PatchFactoryNullFallback(factory, tFactory, tProto, fiCustom, fiFallback, miReadStringNoRef, progress);
            return;
        }

        Func<object, Type, object?> customImpl = (readerObj, expectedType) =>
        {
            try
            {
                string? idStr = (string?)miReadStringNoRef.Invoke(readerObj, null);
                if (string.IsNullOrEmpty(idStr)) return null;

                object protoId = ctorProtoID.Invoke(new object[] { idStr })!;
                var tryGetArgs = new object?[] { protoId, null };
                bool found = (bool)miTryGetProto.Invoke(protosDb, tryGetArgs)!;
                if (found && expectedType.IsInstanceOfType(tryGetArgs[1]))
                    return tryGetArgs[1];

                var stub = RuntimeHelpers.GetUninitializedObject(expectedType);
                fiProtoId.SetValue(stub, protoId);
                _phantomProtoStubs?.Add(stub);
                return stub;
            }
            catch
            {
                return null;
            }
        };

        {
            var pReader = Expression.Parameter(_tBlobReader, "reader");
            var pType = Expression.Parameter(typeof(Type), "expectedType");
            var callImpl = Expression.Call(
                Expression.Constant(customImpl),
                customImpl.GetType().GetMethod("Invoke")!,
                Expression.Convert(pReader, typeof(object)),
                pType);
            var body = Expression.Convert(callImpl, tProto);
            var funcType = typeof(Func<,,>).MakeGenericType(_tBlobReader, typeof(Type), tProto);
            var compiled = Expression.Lambda(funcType, body, pReader, pType).Compile();
            fiCustom.SetValue(factory, compiled);
        }

        if (fiFallback is not null)
        {
            Func<object, string, object?> fallbackImpl = (readerObj, protoTypeName) =>
            {
                try
                {
                    string? idStr = (string?)miReadStringNoRef.Invoke(readerObj, null);
                    if (string.IsNullOrEmpty(idStr)) return null;

                    object protoId = ctorProtoID.Invoke(new object[] { idStr })!;
                    var tryGetArgs = new object?[] { protoId, null };
                    bool found = (bool)miTryGetProto.Invoke(protosDb, tryGetArgs)!;
                    if (found) return tryGetArgs[1];

                    return null;
                }
                catch
                {
                    return null;
                }
            };

            var pReader = Expression.Parameter(_tBlobReader, "reader");
            var pTypeName = Expression.Parameter(typeof(string), "typeName");
            var callFb = Expression.Call(
                Expression.Constant(fallbackImpl),
                fallbackImpl.GetType().GetMethod("Invoke")!,
                Expression.Convert(pReader, typeof(object)),
                pTypeName);
            var fbBody = Expression.Convert(callFb, tProto);
            var fbFuncType = typeof(Func<,,>).MakeGenericType(_tBlobReader, typeof(string), tProto);
            var fbCompiled = Expression.Lambda(fbFuncType, fbBody, pReader, pTypeName).Compile();
            fiFallback.SetValue(factory, fbCompiled);
        }

        _phantomProtoStubs = new HashSet<object>(ReferenceEqualityComparer.Instance);
        progress?.Report("    Factory delegates patched for type-safe proto resolution.");
    }

    private void PatchFactoryNullFallback(
        object factory, Type tFactory, Type tProto,
        FieldInfo fiCustom, FieldInfo? fiFallback,
        MethodInfo miReadStringNoRef, IProgress<string>? progress)
    {
        var originalCustom = fiCustom.GetValue(factory);
        if (originalCustom is null) return;
        var miOrigInvoke = originalCustom.GetType().GetMethod("Invoke")!;
        var nullProto = Expression.Constant(null, tProto);

        var pReader = Expression.Parameter(_tBlobReader!, "reader");
        var pType = Expression.Parameter(typeof(Type), "expectedType");
        var protoVar = Expression.Variable(tProto, "proto");

        var callOriginal = Expression.Assign(protoVar,
            Expression.Convert(
                Expression.Call(Expression.Constant(originalCustom), miOrigInvoke, pReader, pType),
                tProto));
        var isNullOrMatch = Expression.OrElse(
            Expression.Equal(protoVar, nullProto),
            Expression.Call(pType, typeof(Type).GetMethod("IsInstanceOfType")!, Expression.Convert(protoVar, typeof(object))));
        var tryBody = Expression.Block(tProto, callOriginal,
            Expression.Condition(isNullOrMatch, protoVar, nullProto));
        var body = Expression.Block(tProto, new[] { protoVar },
            Expression.TryCatch(tryBody, Expression.Catch(typeof(Exception), nullProto)));

        var funcType = typeof(Func<,,>).MakeGenericType(_tBlobReader!, typeof(Type), tProto);
        var compiled = Expression.Lambda(funcType, body, pReader, pType).Compile();
        fiCustom.SetValue(factory, compiled);
        progress?.Report("    Factory delegates patched (null-fallback mode).");
    }

    /// <summary>Creates Mafi ImmutableArray&lt;T&gt; from a typed array via reflection.</summary>
    private static object CreateImmutableArray(Type elementType, Array array)
    {
        var tImmArrayStatic = AssemblyLoader.FindType("Mafi.Collections.ImmutableCollections.ImmutableArray")
            ?? throw new InvalidOperationException("Mafi ImmutableArray static helper not found.");
        var miCreate = tImmArrayStatic
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Create" && m.IsGenericMethodDefinition)
            .First(m =>
            {
                var p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType.IsArray;
            });
        return miCreate.MakeGenericMethod(elementType).Invoke(null, new object[] { array })!;
    }

    /// <summary>Creates a LoadedModData instance for a given mod manifest + type.</summary>
    private static object CreateLoadedModData(Type tLoadedModData, Type tOptionGeneric, object manifest, Type modType)
    {
        var ctor = tLoadedModData.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First(c => c.GetParameters().Length >= 3);

        var optionTypeType = tOptionGeneric.MakeGenericType(typeof(Type));
        var someMethod = optionTypeType.GetMethod("Some", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Option<Type>.Some not found.");

        object optModType;
        if (someMethod.GetParameters().Length == 1)
        {
            optModType = someMethod.Invoke(null, new object[] { modType })!;
        }
        else
        {
            var tOptionNonGeneric = AssemblyLoader.FindType("Mafi.Option");
            var miSome = tOptionNonGeneric?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Some" && m.IsGenericMethodDefinition);
            if (miSome is not null)
                optModType = miSome.MakeGenericMethod(typeof(Type)).Invoke(null, new object[] { modType })!;
            else
                optModType = Activator.CreateInstance(optionTypeType)!;
        }

        var ctorParams = ctor.GetParameters();
        var args = new object?[ctorParams.Length];
        args[0] = manifest;
        args[1] = optModType;
        args[2] = Activator.CreateInstance(ctorParams[2].ParameterType)!;
        for (int i = 3; i < ctorParams.Length; i++)
        {
            args[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue
                : ctorParams[i].ParameterType.IsValueType ? Activator.CreateInstance(ctorParams[i].ParameterType)
                : null;
        }

        return ctor.Invoke(args)!;
    }

    /// <summary>
    /// Scans loaded assemblies for IMod implementations beyond CoreMod and BaseMod
    /// (e.g. TrainsDlcMod and other DLC/data-only mods) and instantiates them.
    /// </summary>
    private static List<(object mod, Type modType)> DiscoverAdditionalMods(
        Type tIMod, Type tModManifest, MethodInfo miCreateEmpty, IProgress<string>? progress)
    {
        var result = new List<(object, Type)>();

        // Known core types we already handle explicitly.
        var skipNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Mafi.Core.CoreMod",
            "Mafi.Base.BaseMod",
        };

        // Namespace prefixes that indicate Unity render-engine mods — skip those.
        var skipPrefixes = new[] { "Mafi.Unity", "Mafi.UnityCore", "Mafi.TrainsDlc.Unity" };

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Only scan Mafi.* assemblies.
            string? asmName = asm.GetName().Name;
            if (asmName is null || !asmName.StartsWith("Mafi", StringComparison.Ordinal))
                continue;

            // Skip Unity assemblies entirely.
            if (skipPrefixes.Any(p => asmName.StartsWith(p, StringComparison.Ordinal)))
                continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface || !tIMod.IsAssignableFrom(t))
                    continue;
                if (skipNames.Contains(t.FullName ?? ""))
                    continue;

                try
                {
                    // Try manifest-only constructor first (DataOnlyMod pattern),
                    // then fallback to parameterless.
                    var ctorManifest = t.GetConstructor(new[] { tModManifest });
                    object mod;
                    if (ctorManifest is not null)
                    {
                        var manifest = miCreateEmpty.Invoke(null, new object[] { t.Name })!;
                        mod = ctorManifest.Invoke(new[] { manifest });
                    }
                    else
                    {
                        var ctorDefault = t.GetConstructor(Type.EmptyTypes);
                        if (ctorDefault is null) continue;
                        mod = ctorDefault.Invoke(null);
                    }

                    result.Add((mod, t));
                    progress?.Report($"    Discovered DLC/data mod: {t.FullName}");
                }
                catch (Exception ex)
                {
                    progress?.Report($"    Note: Could not instantiate {t.FullName}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// RegisterModsPrototypes returns a list of error tuples whose ToString() collapses
    /// the real cause. Reflectively pull out any Exception-typed field/property and log
    /// the inner-most message + originating type so we can pinpoint why a Data class
    /// (e.g. TransportsData) failed to register.
    /// </summary>
    private static void TryReportInnerException(object err, IProgress<string>? progress)
    {
        try
        {
            var t = err.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            Exception? ex = null;

            // Search fields and properties of the error record for an Exception value.
            foreach (var fi in t.GetFields(flags))
            {
                if (typeof(Exception).IsAssignableFrom(fi.FieldType))
                { ex = fi.GetValue(err) as Exception; if (ex is not null) break; }
            }
            if (ex is null)
            {
                foreach (var pi in t.GetProperties(flags))
                {
                    if (typeof(Exception).IsAssignableFrom(pi.PropertyType) && pi.GetIndexParameters().Length == 0)
                    { try { ex = pi.GetValue(err) as Exception; } catch { } if (ex is not null) break; }
                }
            }

            // If we found an Exception, walk to innermost cause and log it.
            if (ex is not null)
            {
                var inner = ex;
                while (inner.InnerException is not null) inner = inner.InnerException;
                progress?.Report($"      → cause: {inner.GetType().Name}: {inner.Message}");
                var firstFrame = inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(firstFrame))
                    progress?.Report($"        at {firstFrame}");
                return;
            }

            // No Exception field — dump all fields/properties for diagnostics so we can
            // understand the error structure even when the exception is stored as a string.
            bool anyPrinted = false;
            foreach (var fi in t.GetFields(flags))
            {
                try
                {
                    var v = fi.GetValue(err);
                    if (v is null || (v is string s && string.IsNullOrWhiteSpace(s))) continue;
                    progress?.Report($"      → {fi.Name}: {v}");
                    anyPrinted = true;
                }
                catch { }
            }
            if (!anyPrinted)
            {
                // Attempt to read individual items if the error is a tuple-like value type.
                foreach (var pi in t.GetProperties(flags))
                {
                    if (pi.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        var v = pi.GetValue(err);
                        if (v is null || (v is string s && string.IsNullOrWhiteSpace(s))) continue;
                        progress?.Report($"      → {pi.Name}: {v}");
                    }
                    catch { }
                }
            }
        }
        catch { /* diagnostic only */ }
    }

    private object? BuildHollowProtosSerializerFactory(Type tProtosSerializerFactory, IProgress<string>? progress)
    {
        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        if (tProto is null) { progress?.Report("  Note: Proto type not found."); return null; }

        var miReadStringNoRef = _tBlobReader!.GetMethod("ReadStringNoRef",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (miReadStringNoRef is null)
        {
            progress?.Report("  Note: BlobReader.ReadStringNoRef not found.");
            return null;
        }

        var factory = RuntimeHelpers.GetUninitializedObject(tProtosSerializerFactory);

        {
            var pReader = Expression.Parameter(_tBlobReader, "reader");
            var pType   = Expression.Parameter(typeof(Type), "type");
            var readCall = Expression.Call(pReader, miReadStringNoRef);
            var body = Expression.Block(tProto,
                readCall,
                Expression.Constant(null, tProto)
            );
            var funcType = typeof(Func<,,>).MakeGenericType(_tBlobReader, typeof(Type), tProto);
            var lambda = Expression.Lambda(funcType, body, pReader, pType);
            var compiled = lambda.Compile();

            var fi = tProtosSerializerFactory.GetField("m_customNewObjFactory",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi is null)
            {
                progress?.Report("  Note: m_customNewObjFactory field not found.");
                return null;
            }
            fi.SetValue(factory, compiled);
        }

        {
            var pReader   = Expression.Parameter(_tBlobReader, "reader");
            var pTypeName = Expression.Parameter(typeof(string), "typeName");
            var readCall = Expression.Call(pReader, miReadStringNoRef);
            var body = Expression.Block(tProto,
                readCall,
                Expression.Constant(null, tProto)
            );
            var funcType = typeof(Func<,,>).MakeGenericType(_tBlobReader, typeof(string), tProto);
            var lambda = Expression.Lambda(funcType, body, pReader, pTypeName);
            var compiled = lambda.Compile();

            var fi = tProtosSerializerFactory.GetField("m_fallbackObjFactory",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi is null)
            {
                progress?.Report("  Note: m_fallbackObjFactory field not found.");
                return null;
            }
            fi.SetValue(factory, compiled);
        }

        progress?.Report("  Hollow ProtosSerializerFactory created (protos resolve to null).");
        return factory;
    }
}
