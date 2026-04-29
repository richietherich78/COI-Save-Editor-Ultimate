using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

public class ShouldStripTests
{
    [Fact]
    public void WhenTypeAssemblyMatchesThenReturnsTrue()
    {
        var stripAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mscorlib" };

        bool result = DeepEditEngine.ShouldStrip(typeof(int), stripAssemblies);

        // typeof(int) lives in a core assembly — name varies by runtime,
        // so let's use the actual assembly name for a reliable test
        string actualAsm = typeof(int).Assembly.GetName().Name!;
        var matchSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { actualAsm };

        Assert.True(DeepEditEngine.ShouldStrip(typeof(int), matchSet));
    }

    [Fact]
    public void WhenTypeAssemblyDoesNotMatchThenReturnsFalse()
    {
        var stripAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NonExistentMod" };

        bool result = DeepEditEngine.ShouldStrip(typeof(string), stripAssemblies);

        Assert.False(result);
    }

    [Fact]
    public void WhenTypeIsNullThenReturnsFalse()
    {
        var stripAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anything" };

        bool result = DeepEditEngine.ShouldStrip(null, stripAssemblies);

        Assert.False(result);
    }

    [Fact]
    public void WhenMatchIsCaseInsensitiveThenReturnsTrue()
    {
        string actualAsm = typeof(ShouldStripTests).Assembly.GetName().Name!;
        var stripAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { actualAsm.ToUpperInvariant() };

        bool result = DeepEditEngine.ShouldStrip(typeof(ShouldStripTests), stripAssemblies);

        Assert.True(result);
    }
}
