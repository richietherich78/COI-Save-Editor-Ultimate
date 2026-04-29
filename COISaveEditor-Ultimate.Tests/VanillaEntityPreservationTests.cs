using System.Reflection;
using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Regression tests guarding the "vanilla entity preservation" contract:
/// when COIExtended replaced a unique vanilla entity's primary proto with its own
/// extended proto, deep edit must HEAL the entity (swap the phantom stub for the
/// vanilla proto) instead of stripping it.
///
/// The decisive call is <see cref="DeepEditEngine.TryHealPhantomField"/>, exercised
/// here via the same data shapes that <c>StripNullProtoEntitiesFromManagers</c>
/// constructs at runtime.
/// </summary>
public class VanillaEntityPreservationTests
{
    // Mimic the actual Mafi class hierarchy: a base Proto and a concrete subclass
    // that's unique in vanilla ProtosDb (one Shipyard, one CaptainOffice, etc.).
    private class FakeProto { }
    private class FakeShipyardProto : FakeProto { }
    private class FakeCaptainOfficeProto : FakeProto { }

    // Mirror an entity with the canonical m_proto + auto-property backing field shape.
    private class FakeShipyard
    {
        public FakeShipyardProto? m_proto;
    }

    private class FakeCaptainOffice
    {
        // Auto-property with backing field — same shape as
        // CaptainOffice.<Prototype>k__BackingField.
#pragma warning disable IDE1006
        public FakeCaptainOfficeProto? Prototype { get; set; }
#pragma warning restore IDE1006
    }

    [Fact]
    public void Shipyard_PhantomPrimaryProto_IsHealedNotStripped()
    {
        // Arrange: vanilla ProtosDb has exactly one ShipyardProto (the singleton case).
        var vanillaShipyard = new FakeShipyardProto();
        var phantomShipyard = new FakeShipyardProto(); // simulated phantom stub
        var entity = new FakeShipyard { m_proto = phantomShipyard };
        var fi = typeof(FakeShipyard).GetField(nameof(FakeShipyard.m_proto))!;

        var lookup = new DeepEditEngine.ProtoHealingLookup();
        lookup.ByExactType[typeof(FakeShipyardProto)] = new List<object> { vanillaShipyard };

        // Act
        bool healed = DeepEditEngine.TryHealPhantomField(entity, fi, phantomShipyard, lookup);

        // Assert: heal succeeded and the entity's primary proto now points to vanilla.
        Assert.True(healed, "Single-candidate vanilla proto must heal a phantom Shipyard.");
        Assert.Same(vanillaShipyard, entity.m_proto);
    }

    [Fact]
    public void CaptainOffice_PhantomBackingField_IsHealedNotStripped()
    {
        // Arrange
        var vanilla = new FakeCaptainOfficeProto();
        var phantom = new FakeCaptainOfficeProto();
        var entity = new FakeCaptainOffice { Prototype = phantom };

        // Resolve the auto-property's backing field, mirroring how
        // StripNullProtoEntitiesFromManagers detects the phantom site.
        var backingField = typeof(FakeCaptainOffice)
            .GetField($"<{nameof(FakeCaptainOffice.Prototype)}>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

        var lookup = new DeepEditEngine.ProtoHealingLookup();
        lookup.ByExactType[typeof(FakeCaptainOfficeProto)] = new List<object> { vanilla };

        // Act
        bool healed = DeepEditEngine.TryHealPhantomField(entity, backingField, phantom, lookup);

        // Assert
        Assert.True(healed, "Single-candidate vanilla proto must heal a phantom CaptainOffice backing field.");
        Assert.Same(vanilla, entity.Prototype);
    }

    [Fact]
    public void HealingDoesNotMutateOtherEntities_WithSameProtoType()
    {
        // Two separate Shipyard entities sharing the same phantom — heal the first
        // and verify the second still holds the phantom (no global side-effect).
        var vanilla = new FakeShipyardProto();
        var phantom = new FakeShipyardProto();
        var entity1 = new FakeShipyard { m_proto = phantom };
        var entity2 = new FakeShipyard { m_proto = phantom };
        var fi = typeof(FakeShipyard).GetField(nameof(FakeShipyard.m_proto))!;

        var lookup = new DeepEditEngine.ProtoHealingLookup();
        lookup.ByExactType[typeof(FakeShipyardProto)] = new List<object> { vanilla };

        DeepEditEngine.TryHealPhantomField(entity1, fi, phantom, lookup);

        Assert.Same(vanilla, entity1.m_proto);
        Assert.Same(phantom, entity2.m_proto); // entity2 untouched until healed itself
    }

    [Fact]
    public void HealingFailsClosed_WhenLookupHasNoCandidate()
    {
        // No vanilla candidate of any kind → TryHealPhantomField returns false so
        // StripNullProtoEntitiesFromManagers will fall through to stripping.
        var phantom = new FakeShipyardProto();
        var entity = new FakeShipyard { m_proto = phantom };
        var fi = typeof(FakeShipyard).GetField(nameof(FakeShipyard.m_proto))!;

        var lookup = new DeepEditEngine.ProtoHealingLookup(); // empty

        bool healed = DeepEditEngine.TryHealPhantomField(entity, fi, phantom, lookup);

        Assert.False(healed);
        Assert.Same(phantom, entity.m_proto); // field unchanged when heal fails
    }

    [Fact]
    public void HealingPrefersExactConcreteType_OverAssignableSiblings()
    {
        // ByExactType has the exact match AND ByAssignableType has unrelated siblings.
        // The exact-type bucket must win to keep behaviour deterministic.
        var exactVanilla = new FakeShipyardProto();
        var siblingVanilla = new FakeCaptainOfficeProto();
        var phantom = new FakeShipyardProto();
        var entity = new FakeShipyard { m_proto = phantom };
        var fi = typeof(FakeShipyard).GetField(nameof(FakeShipyard.m_proto))!;

        var lookup = new DeepEditEngine.ProtoHealingLookup();
        lookup.ByExactType[typeof(FakeShipyardProto)] = new List<object> { exactVanilla };
        lookup.ByAssignableType[typeof(FakeProto)] = new List<object> { siblingVanilla };

        bool healed = DeepEditEngine.TryHealPhantomField(entity, fi, phantom, lookup);

        Assert.True(healed);
        Assert.Same(exactVanilla, entity.m_proto);
    }
}
