using System.IO;
using System.IO.Compression;
using System.Reflection;
using COISaveEditorUltimate.Models;
using COISaveEditorUltimate.Parsing;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Full re-serialise (all chunks through one writer) ────────────────

    /// <summary>
    /// Creates a single MemoryBlobWriter and writes every chunk exactly as
    /// the game's GameSaver.StartSave() does:
    ///   MOD_TYPES → SAVE_INFO → CONFIGS → RESOLVER → SAVE_END
    /// </summary>
    private byte[] ReserialiseAllChunks(
        ParsedSave save,
        ISet<string> modsToRemove,
        object? saveInfo,
        Array? configsArray,
        object resolver,
        IProgress<string>? progress)
    {
        var tMemWriter = AssemblyLoader.FindType("Mafi.Serialization.MemoryBlobWriter")
            ?? throw new InvalidOperationException("MemoryBlobWriter type not found.");

        object? configSerializers = GetConfigSpecialSerializers();
        object writer = Activator.CreateInstance(
            tMemWriter,
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            args: new object?[] { configSerializers },
            culture: null)!;

        PatchWriterForMonoCompat(writer, progress);

        var writerType = writer.GetType();
        var miWriteULong    = FindMethodDeep(writerType, "WriteULongNonVariable", typeof(ulong))!;
        var miWriteString   = FindMethodDeep(writerType, "WriteString", typeof(string))!;
        var miWriteIntNotNeg = FindMethodDeep(writerType, "WriteIntNotNegative", typeof(int))!;
        var miFinalize      = FindMethodDeep(writerType, "FinalizeSerialization")!;
        var miWriterSetSpec = FindMethodDeep(writerType, "SetSpecialSerializers");

        var stripAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in modsToRemove)
        {
            stripAssemblies.Add(id.Replace('-', '.'));
            stripAssemblies.Add(id);
        }

        // ── MOD_TYPES ─────────────────────────────────────────────────────
        progress?.Report("  Writing MOD_TYPES…");
        miWriteULong.Invoke(writer, new object[] { H_MOD_TYPES });
        var keptMods = save.Mods.Where(m => !modsToRemove.Contains(m.Id)).ToList();
        miWriteIntNotNeg.Invoke(writer, new object[] { keptMods.Count });
        foreach (var mod in keptMods)
        {
            miWriteString.Invoke(writer, new object[] { mod.Id });
            miWriteULong.Invoke(writer, new object[] { mod.VersionRaw });
        }
        miFinalize.Invoke(writer, null);

        // ── SAVE_INFO ─────────────────────────────────────────────────────
        progress?.Report("  Writing SAVE_INFO…");
        miWriteULong.Invoke(writer, new object[] { H_SAVE_INFO });
        if (saveInfo is not null)
        {
            var miSer = saveInfo.GetType().GetMethod("Serialize",
                BindingFlags.Public | BindingFlags.Static);
            miSer?.Invoke(null, new[] { saveInfo, writer });
        }
        miFinalize.Invoke(writer, null);

        // ── CONFIGS (filtered) ────────────────────────────────────────────
        progress?.Report("  Writing CONFIGS…");
        miWriteULong.Invoke(writer, new object[] { H_CONFIGS });
        WriteFilteredConfigs(writer, configsArray, stripAssemblies, progress);
        miFinalize.Invoke(writer, null);

        // ── Switch to game special serializers before RESOLVER ────────────
        var gameSerializers = GetGameSpecialSerializers();
        if (gameSerializers is not null && miWriterSetSpec is not null)
        {
            try
            {
                miWriterSetSpec.Invoke(writer, new[] { gameSerializers });
                progress?.Report("  Game special serializers applied to writer.");
            }
            catch (Exception ex) { progress?.Report($"  Note: Could not set writer serializers: {ex.Message}"); }
        }

        // ── RESOLVER ──────────────────────────────────────────────────────
        progress?.Report("  Writing RESOLVER…");
        miWriteULong.Invoke(writer, new object[] { H_RESOLVER });
        _miSerialize!.Invoke(null, new[] { resolver, writer });

        try
        {
            miFinalize.Invoke(writer, null);
            progress?.Report("  RESOLVER FinalizeSerialization complete.");
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            progress?.Report($"  WARNING: RESOLVER FinalizeSerialization error: {inner.GetType().Name}: {inner.Message}");
            progress?.Report($"  at: {inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }

        // ── SAVE_END ──────────────────────────────────────────────────────
        miWriteULong.Invoke(writer, new object[] { H_SAVE_END });

        return ExtractWriterBytes(writer);
    }

    /// <summary>
    /// Writes the filtered IConfig[] array via the game's BlobWriter.
    /// </summary>
    private void WriteFilteredConfigs(
        object writer, Array? originalConfigs,
        HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        var kept = new List<object>();
        if (originalConfigs is not null)
        {
            for (int i = 0; i < originalConfigs.Length; i++)
            {
                var cfg = originalConfigs.GetValue(i);
                if (cfg is null) continue;
                var cfgType = cfg.GetType();
                if (ShouldStrip(cfgType, stripAssemblies))
                {
                    progress?.Report($"    Stripping config {cfgType.FullName}");
                    continue;
                }

                // ModJsonConfig.SerializeData throws NRE under our reflection-built
                // dependency graph (mod owner reference / json store not initialised).
                // Skip it regardless of which assembly produced it — match by simple
                // name AND full name suffix so generic / namespace variants don't slip through.
                bool isModJsonConfig = cfgType.Name == "ModJsonConfig"
                    || (cfgType.FullName?.EndsWith(".ModJsonConfig", StringComparison.Ordinal) ?? false)
                    || cfgType.Name.StartsWith("ModJsonConfig`", StringComparison.Ordinal);
                if (isModJsonConfig)
                {
                    progress?.Report($"    Stripping ModJsonConfig (unsafe for re-serialization): {cfgType.FullName}");
                    continue;
                }

                kept.Add(cfg);
                progress?.Report($"    Keeping config {cfgType.FullName}");
            }
        }
        progress?.Report($"    Keeping {kept.Count} config(s), stripped {(originalConfigs?.Length ?? 0) - kept.Count}.");

        // Scrub any reference-typed field on the kept configs that points to a
        // ModJsonConfig instance. Otherwise the BlobWriter's class-serializer
        // dispatcher queues that instance for write during FinalizeSerialization
        // and ModJsonConfig.SerializeData NREs (its mod backreference is null in
        // our reflection-built graph).
        ScrubModJsonConfigFieldsOnKeptConfigs(kept, progress);

        var tIConfig = AssemblyLoader.FindType("Mafi.Core.Game.IConfig");
        if (tIConfig is null)
        {
            var miWriteIntNotNeg = FindMethodDeep(writer.GetType(), "WriteIntNotNegative", typeof(int));
            miWriteIntNotNeg?.Invoke(writer, new object[] { 0 });
            return;
        }

        var filteredArray = Array.CreateInstance(tIConfig, kept.Count);
        for (int i = 0; i < kept.Count; i++)
            filteredArray.SetValue(kept[i], i);

        var miWriteArray = writer.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "WriteArray" &&
                                 m.IsGenericMethodDefinition &&
                                 m.GetParameters().Length == 1);

        if (miWriteArray is not null)
        {
            miWriteArray.MakeGenericMethod(tIConfig).Invoke(writer, new object[] { filteredArray });
        }
        else
        {
            progress?.Report("    WARNING: WriteArray<T> not found on writer — configs will be empty.");
            var miWriteIntNotNeg = FindMethodDeep(writer.GetType(), "WriteIntNotNegative", typeof(int));
            miWriteIntNotNeg?.Invoke(writer, new object[] { 0 });
        }
    }

    /// <summary>
    /// Walks every reference-typed field on each kept config and nulls out anything
    /// pointing to a ModJsonConfig instance. The class-serializer dispatcher would
    /// otherwise queue that instance for serialisation during FinalizeSerialization,
    /// and ModJsonConfig.SerializeData NREs because its mod-owner backreference is
    /// null in the reflection-built graph.
    /// </summary>
    private void ScrubModJsonConfigFieldsOnKeptConfigs(List<object> kept, IProgress<string>? progress)
    {
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int nulled = 0;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>(kept);
        foreach (var k in kept) visited.Add(k);

        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            for (var cur = obj.GetType(); cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft.IsValueType || ft == typeof(string)) continue;

                    bool isModJsonField = ft.Name == "ModJsonConfig"
                        || (ft.FullName?.EndsWith(".ModJsonConfig", StringComparison.Ordinal) ?? false);
                    if (isModJsonField)
                    {
                        try
                        {
                            if (fi.GetValue(obj) is not null)
                            {
                                fi.SetValue(obj, null);
                                nulled++;
                                progress?.Report($"      Nulled ModJsonConfig field {obj.GetType().Name}.{fi.Name}");
                            }
                        }
                        catch { }
                        continue;
                    }

                    // Also catch instance whose runtime type IS ModJsonConfig even if declared
                    // as a base/interface type (e.g. IConfig).
                    try
                    {
                        var v = fi.GetValue(obj);
                        if (v is null) continue;
                        var vt = v.GetType();
                        bool runtimeIsModJsonConfig = vt.Name == "ModJsonConfig"
                            || (vt.FullName?.EndsWith(".ModJsonConfig", StringComparison.Ordinal) ?? false);
                        if (runtimeIsModJsonConfig)
                        {
                            fi.SetValue(obj, null);
                            nulled++;
                            progress?.Report($"      Nulled ModJsonConfig instance via {obj.GetType().Name}.{fi.Name} (declared {ft.Name})");
                            continue;
                        }
                        if (visited.Add(v)) queue.Enqueue(v);
                    }
                    catch { }
                }
            }
        }

        if (nulled > 0)
            progress?.Report($"    Scrubbed {nulled} ModJsonConfig reference(s) on kept configs.");
    }

    /// <summary>Gets config special serializers (for MemoryBlobWriter constructor).</summary>
    private object? GetConfigSpecialSerializers()
    {
        try
        {
            var tFactory = AssemblyLoader.FindType("Mafi.Core.Game.SpecialSerializerFactories");
            if (tFactory is null) return null;
            var mi = tFactory.GetMethod("GetSerializersForConfigs",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public | BindingFlags.Static);
            return mi?.Invoke(null, null);
        }
        catch { return null; }
    }

    /// <summary>Gets game special serializers (for RESOLVER serialization).</summary>
    private object? GetGameSpecialSerializers() => _gameSpecialSerializers;

    /// <summary>
    /// Replaces the BlobWriter's internal BinaryWriter with a
    /// <see cref="MonoCompatBinaryWriter"/> that rewrites .NET 8
    /// assembly-qualified type names to Mono/Unity equivalents.
    /// </summary>
    private void PatchWriterForMonoCompat(object writer, IProgress<string>? progress)
    {
        try
        {
            var fiWriter = FindFieldDeep(writer.GetType(), "Writer");
            if (fiWriter is null)
            {
                progress?.Report("  Note: Could not find Writer field — Mono compat patch skipped.");
                return;
            }

            var oldBw = fiWriter.GetValue(writer) as System.IO.BinaryWriter;
            if (oldBw is null)
            {
                progress?.Report("  Note: Writer field was null — Mono compat patch skipped.");
                return;
            }

            var stream = oldBw.BaseStream;
            oldBw.Flush();
            var monoWriter = new MonoCompatBinaryWriter(stream);
            fiWriter.SetValue(writer, monoWriter);
            progress?.Report("  Mono-compatible BinaryWriter installed.");
        }
        catch (Exception ex)
        {
            progress?.Report($"  Note: Mono compat patch failed: {ex.Message}");
        }
    }

    /// <summary>Extracts the raw bytes from a MemoryBlobWriter/BlobWriter.</summary>
    private byte[] ExtractWriterBytes(object writer)
    {
        var baseStreamProp = writer.GetType().GetProperty("BaseStream",
            BindingFlags.Public | BindingFlags.Instance);
        if (baseStreamProp?.GetValue(writer) is MemoryStream msBase)
            return msBase.ToArray();

        var miFAS = writer.GetType().GetMethod("FinalizeAndReturnStream",
            BindingFlags.Public | BindingFlags.Instance);
        if (miFAS is not null)
        {
            if (miFAS.Invoke(writer, null) is MemoryStream msRet)
                return msRet.ToArray();
        }

        var streamProp = writer.GetType().GetProperty("OutputStream",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (streamProp?.GetValue(writer) is MemoryStream ms2)
            return ms2.ToArray();

        var streamField = FindFieldDeep(writer.GetType(), "m_outputStream")
            ?? FindFieldDeep(writer.GetType(), "OutputStream");
        if (streamField?.GetValue(writer) is MemoryStream ms3)
            return ms3.ToArray();

        throw new InvalidOperationException("Cannot extract bytes from BlobWriter.");
    }
}
