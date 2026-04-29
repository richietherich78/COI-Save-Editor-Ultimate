using System.Reflection;
using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Regression tests for the GoalsList/GoalsTab crash:
///   "DependencyResolverException: Failed to instantiate GoalsTab"
///   ---> NullReferenceException at GoalsList.get_Title() (Prototype was null)
///
/// Root cause: GoalsList.Prototype (a GoalListProto field) was a phantom stub when
/// COIExtended added custom goal lists. The mutable-collection strip did not consider
/// vanilla non-entity objects (GoalsList is Mafi.Core, not a mod assembly), so it
/// left the GoalsList in GoalsManager collections with a nulled Prototype field.
///
/// Fix: IsNonEntityWithPhantomProto strips vanilla non-entity objects whose proto
/// fields are phantom stubs, while deliberately leaving vanilla entities (trucks etc.)
/// alone since the entity pipeline handles those separately.
/// </summary>
public class GoalsListPhantomStripTests
{
    private static readonly BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // ── Fake types used as stand-ins for game types ──────────────────────────

    private interface IFakeEntity { }

    private class FakeProto { }

    private class FakeGoalsList
    {
        public FakeProto? Prototype;
    }

    private class FakeEntity : IFakeEntity
    {
        public FakeProto? Proto;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void NonEntity_WithPhantomProtoField_ReturnsTrue()
    {
        var phantom = new FakeProto();
        var stubs = new HashSet<object>(ReferenceEqualityComparer.Instance) { phantom };
        var item = new FakeGoalsList { Prototype = phantom };

        bool result = DeepEditEngine.IsNonEntityWithPhantomProto(
            item, typeof(FakeProto), typeof(IFakeEntity), stubs, AllFlags);

        Assert.True(result, "GoalsList-like objects with phantom proto fields should be stripped from mutable collections.");
    }

    [Fact]
    public void Entity_WithPhantomProtoField_ReturnsFalse()
    {
        // Vanilla entities (e.g. trucks) with phantom protos must NOT be stripped
        // from mutable collections — the entity pipeline handles them separately.
        var phantom = new FakeProto();
        var stubs = new HashSet<object>(ReferenceEqualityComparer.Instance) { phantom };
        var item = new FakeEntity { Proto = phantom };

        bool result = DeepEditEngine.IsNonEntityWithPhantomProto(
            item, typeof(FakeProto), typeof(IFakeEntity), stubs, AllFlags);

        Assert.False(result, "Entity objects with phantom proto fields must NOT be stripped by the non-entity path.");
    }

    [Fact]
    public void NonEntity_WithNoPhantomProtoField_ReturnsFalse()
    {
        var stubs = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var item = new FakeGoalsList { Prototype = new FakeProto() };

        bool result = DeepEditEngine.IsNonEntityWithPhantomProto(
            item, typeof(FakeProto), typeof(IFakeEntity), stubs, AllFlags);

        Assert.False(result, "Non-entity object with a live (non-phantom) proto should not be stripped.");
    }

    [Fact]
    public void NonEntity_IsPhantomItself_ReturnsTrue()
    {
        var item = new FakeGoalsList();
        var stubs = new HashSet<object>(ReferenceEqualityComparer.Instance) { item };

        bool result = DeepEditEngine.IsNonEntityWithPhantomProto(
            item, typeof(FakeProto), typeof(IFakeEntity), stubs, AllFlags);

        Assert.True(result, "An item that is itself a phantom stub should always be flagged.");
    }

    [Fact]
    public void WhenIEntityTypeIsNull_NonEntity_WithPhantomField_ReturnsTrue()
    {
        // If IEntity can't be found at runtime (e.g. assembly not loaded), the guard
        // is skipped and the phantom check still runs.
        var phantom = new FakeProto();
        var stubs = new HashSet<object>(ReferenceEqualityComparer.Instance) { phantom };
        var item = new FakeGoalsList { Prototype = phantom };

        bool result = DeepEditEngine.IsNonEntityWithPhantomProto(
            item, typeof(FakeProto), tIEntity: null, stubs, AllFlags);

        Assert.True(result, "When IEntity type is unavailable, phantom proto check still applies.");
    }
}
