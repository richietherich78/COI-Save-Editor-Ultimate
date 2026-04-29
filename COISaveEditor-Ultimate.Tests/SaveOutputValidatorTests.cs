using System.Text;
using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

public class LoadableTypeIndexTests
{
    [Fact]
    public void WhenNoModsRemovedThenIndexContainsCurrentAssemblyType()
    {
        var idx = LoadableTypeIndex.Build(Array.Empty<string>());

        // The test assembly is loaded; one of its own types must be present by FullName.
        Assert.True(idx.ContainsExact(typeof(LoadableTypeIndexTests).FullName!));
    }

    [Fact]
    public void WhenModsRemovedThenLoadableAssemblySetExcludesThem()
    {
        var idx = LoadableTypeIndex.Build(new[] { "SomeFakeRemovedMod.Core" });

        Assert.False(idx.IsLoadableAssemblySimpleName("SomeFakeRemovedMod.Core"));
        // And the test assembly is still present.
        Assert.True(idx.IsLoadableAssemblySimpleName(
            typeof(LoadableTypeIndexTests).Assembly.GetName().Name!));
    }

    [Fact]
    public void WhenLookupForFabricatedTypeNameThenReturnsFalse()
    {
        var idx = LoadableTypeIndex.Build(Array.Empty<string>());

        Assert.False(idx.ContainsExact("COIExtended.Core.Prototypes.Buildings.FishFarm.FishFarm"));
    }
}

public class SaveOutputValidatorTests
{
    [Fact]
    public void WhenPayloadIsEmptyThenReportIsClean()
    {
        var report = SaveOutputValidator.Validate(Array.Empty<byte>(), new[] { "COIExtended.Core" });

        Assert.True(report.IsClean);
        Assert.Equal(0, report.TotalOccurrences);
    }

    [Fact]
    public void WhenNoRemovedAssembliesThenReportIsClean()
    {
        var payload = Encoding.UTF8.GetBytes(", COIExtended.Core, Version=0.8.2.559, Culture=neutral");

        var report = SaveOutputValidator.Validate(payload, Array.Empty<string>());

        Assert.True(report.IsClean);
    }

    [Fact]
    public void WhenPayloadContainsSingleRemovedModAqnThenReportContainsOneViolation()
    {
        // Synthetic AQN inside an arbitrary byte buffer.
        var aqn = "COIExtended.Core.Prototypes.Buildings.FishFarm.FishFarm, COIExtended.Core, Version=0.8.2.559, Culture=neutral, PublicKeyToken=null";
        var payload = new byte[64].Concat(Encoding.UTF8.GetBytes(aqn)).Concat(new byte[64]).ToArray();

        var report = SaveOutputValidator.Validate(payload, new[] { "COIExtended.Core" });

        Assert.False(report.IsClean);
        Assert.Single(report.Violations);
        Assert.Equal(1, report.TotalOccurrences);
        Assert.Contains("FishFarm", report.Violations[0].TypeStringSnippet);
        Assert.Equal("COIExtended.Core", report.Violations[0].AssemblySimpleName);
    }

    [Fact]
    public void WhenPayloadContainsMultipleOccurrencesOfSameTypeThenCoalescedToOneViolationWithCount()
    {
        var aqn = "X.Y, COIExtended.Core, Version=0.8.2.559, Culture=neutral, PublicKeyToken=null";
        var bytes = Encoding.UTF8.GetBytes(aqn);
        var payload = bytes.Concat(new byte[8]).Concat(bytes).Concat(new byte[8]).Concat(bytes).ToArray();

        var report = SaveOutputValidator.Validate(payload, new[] { "COIExtended.Core" });

        Assert.False(report.IsClean);
        Assert.Single(report.Violations);
        Assert.Equal(3, report.Violations[0].OccurrenceCount);
        Assert.Equal(3, report.TotalOccurrences);
    }

    [Fact]
    public void WhenPayloadHasDistinctTypesFromSameRemovedAsmThenSeparateViolations()
    {
        var a = Encoding.UTF8.GetBytes("X.A, COIExtended.Core, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var b = Encoding.UTF8.GetBytes("X.B, COIExtended.Core, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var payload = a.Concat(new byte[8]).Concat(b).ToArray();

        var report = SaveOutputValidator.Validate(payload, new[] { "COIExtended.Core" });

        Assert.False(report.IsClean);
        Assert.Equal(2, report.Violations.Count);
        Assert.Equal(2, report.TotalOccurrences);
    }

    [Fact]
    public void WhenPayloadOnlyContainsKeptAssemblyAqnThenReportIsClean()
    {
        var payload = Encoding.UTF8.GetBytes("Mafi.Core.Whatever, Mafi.Core, Version=0.8.2.0, Culture=neutral, PublicKeyToken=null");

        var report = SaveOutputValidator.Validate(payload, new[] { "COIExtended.Core" });

        Assert.True(report.IsClean);
    }

    [Fact]
    public void WhenPayloadContainsPhantomProtoIdThenReportContainsViolation()
    {
        var payload = new byte[32].Concat(Encoding.ASCII.GetBytes("__phantom_258")).Concat(new byte[32]).ToArray();

        var report = SaveOutputValidator.Validate(payload, Array.Empty<string>());

        Assert.False(report.IsClean);
        Assert.Single(report.Violations);
        Assert.Equal("(phantom proto ID)", report.Violations[0].AssemblySimpleName);
        Assert.Equal("__phantom_258", report.Violations[0].TypeStringSnippet);
    }

    [Fact]
    public void WhenPayloadContainsMultiplePhantomProtoIdsThenCoalescedByExactId()
    {
        var p258 = Encoding.ASCII.GetBytes("__phantom_258");
        var p99  = Encoding.ASCII.GetBytes("__phantom_99");
        var payload = p258.Concat(new byte[8]).Concat(p258).Concat(new byte[8]).Concat(p99).ToArray();

        var report = SaveOutputValidator.Validate(payload, Array.Empty<string>());

        Assert.False(report.IsClean);
        Assert.Equal(2, report.Violations.Count);
        Assert.Equal(3, report.TotalOccurrences);
        var v258 = report.Violations.Single(v => v.TypeStringSnippet == "__phantom_258");
        Assert.Equal(2, v258.OccurrenceCount);
    }

    [Fact]
    public void WhenPhantomIdIsPrecededByOnlyAqnTailThenNearestTypeIsNotTheAssemblyName()
    {
        // Synthetic layout that mimics what the validator was misreporting:
        // a real type-name "Mafi.Foo.MyType" then ", Mafi.Core, Version=…" assembly tail,
        // then later a __phantom_42 hit. The walker should return "Mafi.Foo.MyType",
        // not "Mafi.Core" (which is just the assembly-name segment of the AQN).
        var aqn = Encoding.ASCII.GetBytes(
            "Mafi.Foo.MyType, Mafi.Core, Version=0.8.2.0, Culture=neutral, PublicKeyToken=null");
        var phantomId = Encoding.ASCII.GetBytes("__phantom_42");
        var payload = new byte[16].Concat(aqn).Concat(new byte[64]).Concat(phantomId).ToArray();

        var report = SaveOutputValidator.Validate(payload, Array.Empty<string>());

        Assert.Single(report.Violations);
        Assert.Equal("Mafi.Foo.MyType", report.Violations[0].NearestPrecedingType);
    }

    [Fact]
    public void WhenNoPrecedingTypeNameInWindowThenNearestTypeIsNull()
    {
        var phantomId = Encoding.ASCII.GetBytes("__phantom_7");
        var payload = new byte[64].Concat(phantomId).ToArray();

        var report = SaveOutputValidator.Validate(payload, Array.Empty<string>());

        Assert.Single(report.Violations);
        Assert.Null(report.Violations[0].NearestPrecedingType);
    }
}

public class BuildRemovedAssemblyNamesTests
{
    private static HashSet<string> Strip(params string[] ids)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids) { s.Add(id.Replace('-', '.')); s.Add(id); }
        return s;
    }

    [Fact]
    public void NullModsToRemove_ReturnsStripAssembliesUnchanged()
    {
        var strip = Strip("SomeMod");
        var result = DeepEditEngine.BuildRemovedAssemblyNames(null, strip);

        Assert.True(result.SetEquals(strip));
    }

    [Fact]
    public void EmptyModsToRemove_ReturnsStripAssembliesUnchanged()
    {
        var strip = Strip("SomeMod");
        var result = DeepEditEngine.BuildRemovedAssemblyNames(new HashSet<string>(), strip);

        Assert.True(result.SetEquals(strip));
    }

    [Fact]
    public void DashedModId_FindsSubAssemblyByFamilyRoot()
    {
        // Simulate: user removed "COIExtended-Core" → family root "COIExtended".
        // A loaded assembly named "COIExtended.Automation" (like xunit.runner.visualstudio
        // but for testing we use the actual test assembly name pattern) should be found.
        // We can't inject fake assemblies into AppDomain, so we verify the logic
        // using the *test assembly itself*: it's named "COISaveEditor-Ultimate.Tests" → root "COISaveEditor".
        var modsToRemove = new HashSet<string> { "COISaveEditor-Ultimate" };
        var strip = Strip("COISaveEditor-Ultimate");

        var result = DeepEditEngine.BuildRemovedAssemblyNames(modsToRemove, strip);

        // Family root = "COISaveEditor" → should find "COISaveEditor-Ultimate.Tests" (this assembly)
        // because it starts with "COISaveEditor-".
        string thisAsm = typeof(BuildRemovedAssemblyNamesTests).Assembly.GetName().Name!;
        Assert.Contains(thisAsm, result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnrelatedLoadedAssemblyIsNotIncluded()
    {
        // "COIExtended-Core" → root "COIExtended" — "xunit.core" does NOT start with "COIExtended".
        var modsToRemove = new HashSet<string> { "COIExtended-Core" };
        var strip = Strip("COIExtended-Core");

        var result = DeepEditEngine.BuildRemovedAssemblyNames(modsToRemove, strip);

        Assert.DoesNotContain("xunit.core", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripAssembliesAlwaysIncludedRegardlessOfFamilyMatch()
    {
        // Even if the family-root scan finds nothing new, the original stripAssemblies
        // entries are always in the result.
        var strip = Strip("SomeObscureModThatMatchesNothing99999");

        var result = DeepEditEngine.BuildRemovedAssemblyNames(
            new HashSet<string> { "SomeObscureModThatMatchesNothing99999" }, strip);

        Assert.Contains("SomeObscureModThatMatchesNothing99999", result, StringComparer.OrdinalIgnoreCase);
    }
}
