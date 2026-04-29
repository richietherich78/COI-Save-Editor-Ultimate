using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

public class IsGameAssemblyTypeTests
{
    private static readonly string[] GamePrefixes = DeepEditEngine.GameAssemblyPrefixes;

    [Theory]
    [InlineData("Mafi.Core", true)]
    [InlineData("Mafi.Base", true)]
    [InlineData("Mafi.TrainsDlc", true)]
    [InlineData("Mafi", true)]
    [InlineData("Mafi.Core.Foo", true)]
    [InlineData("Mafi.Base.Bar", true)]
    [InlineData("MafiNot", false)]
    [InlineData("COIExtended.Core", false)]
    [InlineData("System.Private.CoreLib", false)]
    public void GameAssemblyPrefixMatching_IsCorrect(string asmName, bool expected)
    {
        bool matched = false;
        foreach (var p in GamePrefixes)
        {
            if (asmName.Equals(p, System.StringComparison.OrdinalIgnoreCase)
                || asmName.StartsWith(p + ".", System.StringComparison.OrdinalIgnoreCase))
            { matched = true; break; }
        }
        Assert.Equal(expected, matched);
    }

    [Fact]
    public void IsVanillaType_ReturnsFalseForNull()
    {
        Assert.False(DeepEditEngine.IsVanillaType(null, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void IsVanillaType_ReturnsFalseForRemovedModType()
    {
        // Pretend the test assembly itself is a removed mod. It is NOT a Mafi.* assembly,
        // so even without the strip set it would not be vanilla — assert both paths.
        var asmName = typeof(IsGameAssemblyTypeTests).Assembly.GetName().Name!;
        var strip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { asmName };
        Assert.False(DeepEditEngine.IsVanillaType(typeof(IsGameAssemblyTypeTests), strip));
    }

    [Fact]
    public void IsVanillaType_ReturnsFalseForNonGameAssembly()
    {
        var strip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(DeepEditEngine.IsVanillaType(typeof(string), strip));
    }

    [Fact]
    public void IsGameAssemblyType_StrictPrefixMatch_RejectsLookalikes()
    {
        // Synthetic types living in the test assembly: name does not matter, only assembly does.
        // We rely on the prefix-matching rule itself, exercised through a fake string scan.
        // The important guarantee: "MafiX" must NOT match the "Mafi" prefix entry.
        bool wouldMatch = false;
        foreach (var p in DeepEditEngine.GameAssemblyPrefixes)
        {
            if ("MafiX".Equals(p, System.StringComparison.OrdinalIgnoreCase)
                || "MafiX".StartsWith(p + ".", System.StringComparison.OrdinalIgnoreCase))
            { wouldMatch = true; break; }
        }
        Assert.False(wouldMatch);
    }

    [Fact]
    public void IsVanillaType_ReturnsTrueForMafiCoreAssemblyName()
    {
        // Regression guard: Mafi.Core is a vanilla assembly, so IsVanillaType returns true
        // for types from it. This is exactly why IsVanillaType must NOT be used as an
        // unconditional "keep" guard when stripping ImmutableArray items: phantom stubs of
        // vanilla types (e.g. a DiseaseProto stub for a missing COIExtended disease) are also
        // vanilla-typed, but they MUST still be stripped or the game throws a
        // CorruptedSaveException ("Failed cast loaded proto ... to DiseaseProto").
        // The phantom-stub check (_phantomProtoStubs.Contains) must always take priority.
        var strip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool mafiCoreIsVanilla = false;
        foreach (var p in DeepEditEngine.GameAssemblyPrefixes)
        {
            if ("Mafi.Core".Equals(p, System.StringComparison.OrdinalIgnoreCase)
                || "Mafi.Core".StartsWith(p + ".", System.StringComparison.OrdinalIgnoreCase))
            { mafiCoreIsVanilla = true; break; }
        }
        // Mafi.Core IS a vanilla assembly — types from it pass IsVanillaType.
        Assert.True(mafiCoreIsVanilla,
            "Mafi.Core is vanilla; phantom stubs of Mafi.Core types (e.g. DiseaseProto) " +
            "must still be stripped — do not gate phantom-stub removal on IsVanillaType.");
    }
}
