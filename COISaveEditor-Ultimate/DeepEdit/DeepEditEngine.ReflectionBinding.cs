using System.IO;
using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Reflection binding ────────────────────────────────────────────────

    private bool BindReflectedTypes(out string? error)
    {
        error = null;

        _tBlobReader = AssemblyLoader.FindType("Mafi.Serialization.BlobReader");
        if (_tBlobReader is null) { error = "Could not find Mafi.Serialization.BlobReader — are the game DLLs loaded?"; return false; }

        _tDependencyResolver = AssemblyLoader.FindType("Mafi.DependencyResolver");
        if (_tDependencyResolver is null) { error = "Could not find Mafi.DependencyResolver."; return false; }

        _tBlobWriter      = AssemblyLoader.FindType("Mafi.Serialization.BlobWriter");
        _tMemoryBlobWriter = AssemblyLoader.FindType("Mafi.Serialization.MemoryBlobWriter")
                          ?? AssemblyLoader.FindType("Mafi.Serialization.BlobWriter");

        // BlobReader constructor
        _miBlobReaderCtor = _tBlobReader
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().Length >= 2 &&
                                 c.GetParameters()[0].ParameterType == typeof(Stream));
        if (_miBlobReaderCtor is null) { error = "Could not find BlobReader constructor."; return false; }

        _miDeserializeInto = _tDependencyResolver.GetMethod("DeserializeInto",
            BindingFlags.Public | BindingFlags.Static);
        if (_miDeserializeInto is null) { error = "Could not find DependencyResolver.DeserializeInto."; return false; }

        _miCreateEmpty = _tDependencyResolver.GetMethod("CreateEmpty",
            BindingFlags.Public | BindingFlags.Static);
        if (_miCreateEmpty is null) { error = "Could not find DependencyResolver.CreateEmpty."; return false; }

        _miSerialize = _tDependencyResolver.GetMethod("Serialize",
            BindingFlags.Public | BindingFlags.Static);
        if (_miSerialize is null) { error = "Could not find DependencyResolver.Serialize."; return false; }

        _miFinalizeLoading = _tBlobReader.GetMethod("FinalizeLoading",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(object) /* Option<DR> */, typeof(Action) },
            modifiers: null)
            ?? _tBlobReader.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .FirstOrDefault(m => m.Name == "FinalizeLoading" &&
                                                m.GetParameters().Length >= 1);
        if (_miFinalizeLoading is null) { error = "Could not find BlobReader.FinalizeLoading."; return false; }

        _miReadULong = _tBlobReader.GetMethod("ReadULongNonVariable",
            BindingFlags.Public | BindingFlags.Instance);
        if (_miReadULong is null) { error = "Could not find BlobReader.ReadULongNonVariable."; return false; }

        _miSetSpecialSerializers = _tBlobReader.GetMethod("SetSpecialSerializers",
            BindingFlags.Public | BindingFlags.Instance);

        // Private fields we need for the strip step and for pre-populating the writer
        _fiReadObjects = _tBlobReader.GetField("m_readObjects",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fiReadTypes = _tBlobReader.GetField("m_readTypes",
            BindingFlags.NonPublic | BindingFlags.Instance);

        return true;
    }

    // ── Create BlobReader ─────────────────────────────────────────────────

    private object CreateBlobReader(Stream stream, int saveVersion)
    {
        var ctorParams = _miBlobReaderCtor!.GetParameters();
        var args = new object?[ctorParams.Length];
        args[0] = stream;
        args[1] = saveVersion;
        for (int i = 2; i < args.Length; i++)
        {
            var p = ctorParams[i];
            // CRITICAL: null is NOT valid for value-type (struct) parameters in reflection.
            // Pass default(T) via Activator.CreateInstance for structs; null for reference types.
            args[i] = p.ParameterType.IsValueType
                ? Activator.CreateInstance(p.ParameterType)
                : null;
        }
        return _miBlobReaderCtor.Invoke(args)!;
    }

    // ── Reflection utilities ──────────────────────────────────────────────

    internal static FieldInfo? FindFieldDeep(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var fi = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi is not null) return fi;
        }
        return null;
    }

    /// <summary>Finds a public/nonpublic instance method by name, optionally with parameter types.</summary>
    internal static MethodInfo? FindMethodDeep(Type type, string name, params Type[] paramTypes)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var t = type; t is not null; t = t.BaseType)
        {
            var mi = paramTypes.Length > 0
                ? t.GetMethod(name, flags, null, paramTypes, null)
                : t.GetMethod(name, flags);
            if (mi is not null) return mi;
        }
        return null;
    }
}
