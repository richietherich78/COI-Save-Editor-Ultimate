// SPDX-License-Identifier: see project root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Index of every type-name string the game's deserializer is expected to be able to
/// resolve on load. Built from all assemblies currently loaded into the AppDomain,
/// minus the assemblies of any mods being removed by deep edit.
///
/// <para/>
/// Mafi's <c>BlobWriter.WriteTypeNameAsStrNoRef</c> writes either
/// <see cref="Type.FullName"/> (for the executing assembly or mscorlib) or
/// <see cref="Type.AssemblyQualifiedName"/> (for everything else). For closed
/// generic types both forms include nested-bracketed AQNs of each type argument,
/// e.g.
/// <c>Mafi.Option`1[[COIExtended.Core.Prototypes.Buildings.FishFarm.FishFarm,
///   COIExtended.Core, Version=…, Culture=…, PublicKeyToken=…]]</c>.
/// We index both forms for every type, plus a "FullName + simple assembly name"
/// short form because Mono and the game's own loader both accept that shape.
/// </summary>
public sealed class LoadableTypeIndex
{
    private readonly HashSet<string> _exactNames;
    private readonly HashSet<string> _loadableAssemblySimpleNames;

    /// <summary>Number of unique loadable type-name strings indexed.</summary>
    public int Count => _exactNames.Count;

    /// <summary>Simple names of every assembly considered "kept" (loadable on the game side).</summary>
    public IReadOnlyCollection<string> LoadableAssemblySimpleNames => _loadableAssemblySimpleNames;

    private LoadableTypeIndex(HashSet<string> exact, HashSet<string> loadableAsmNames)
    {
        _exactNames = exact;
        _loadableAssemblySimpleNames = loadableAsmNames;
    }

    /// <summary>True if a candidate type-name string matches an exact form we indexed.</summary>
    public bool ContainsExact(string typeName) => _exactNames.Contains(typeName);

    /// <summary>True if the given simple assembly name belongs to a kept (loadable) assembly.</summary>
    public bool IsLoadableAssemblySimpleName(string simpleAsmName) =>
        _loadableAssemblySimpleNames.Contains(simpleAsmName);

    /// <summary>
    /// Build the index from all currently-loaded assemblies, excluding any whose simple name
    /// matches one of <paramref name="removedModSimpleNames"/> (case-insensitive).
    /// </summary>
    public static LoadableTypeIndex Build(IReadOnlyCollection<string> removedModSimpleNames)
    {
        var removed = new HashSet<string>(
            removedModSimpleNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var loadableAsm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = asm.GetName().Name;
            if (asmName is null) continue;
            if (removed.Contains(asmName)) continue;
            loadableAsm.Add(asmName);

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t is null) continue;
                if (t.FullName is { } fn) exact.Add(fn);
                if (t.AssemblyQualifiedName is { } aqn) exact.Add(aqn);
            }
        }

        return new LoadableTypeIndex(exact, loadableAsm);
    }
}
