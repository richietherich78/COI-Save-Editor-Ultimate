using System.Reflection;
using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Tests for the PortsShape compatibility guard in proto healing.
///
/// Entities such as Transport and Zipper have a PortsShape field on their proto that
/// encodes the physical connector geometry. Substituting a vanilla proto with a
/// different PortsShape causes NullReferenceException / IndexOutOfRangeException in
/// initSelf / initAfterLoad. These tests verify that:
///   - Compatible candidates (same or absent PortsShape) are still used.
///   - Incompatible candidates (different PortsShape) are rejected so the entity is
///     stripped instead of loaded with a mismatched proto.
/// </summary>
public class ProtoHealingShapeCompatibilityTests
{
    // ── Fake types ──────────────────────────────────────────────────────────

    private class FakePortShape
    {
        public string Id { get; }
        public FakePortShape(string id) => Id = id;
        public override string ToString() => Id;
    }

    private class FakeProto { }

    private class FakeTransportProto : FakeProto
    {
        // Mirror of the real TransportProto.PortsShape field.
        public readonly FakePortShape PortsShape;
        public FakeTransportProto(FakePortShape shape) => PortsShape = shape;
        public override string ToString() => PortsShape.Id;
    }

    private class FakeProtoNoShape : FakeProto
    {
        // No PortsShape field — should always be treated as compatible.
        public override string ToString() => "NoShape";
    }

    private class FakeEntity
    {
        public FakeProto? m_proto;
    }

    private static DeepEditEngine.ProtoHealingLookup EmptyLookup() =>
        new DeepEditEngine.ProtoHealingLookup();

    // ── AreProtoShapesCompatible ────────────────────────────────────────────

    [Fact]
    public void WhenBothProtosHaveNoPortsShapeField_ThenCompatible()
    {
        var phantom   = new FakeProtoNoShape();
        var candidate = new FakeProtoNoShape();
        var lookup    = EmptyLookup();

        Assert.True(DeepEditEngine.AreProtoShapesCompatible(phantom, candidate, lookup));
    }

    [Fact]
    public void WhenBothProtosHaveMatchingPortsShapeIds_ThenCompatible()
    {
        var shape     = new FakePortShape("Pipe");
        var phantom   = new FakeTransportProto(shape);
        var candidate = new FakeTransportProto(shape);
        var lookup    = EmptyLookup();

        Assert.True(DeepEditEngine.AreProtoShapesCompatible(phantom, candidate, lookup));
    }

    [Fact]
    public void WhenBothProtosHaveDifferentPortsShapeIds_ThenIncompatible()
    {
        var phantom   = new FakeTransportProto(new FakePortShape("Pipe_long"));
        var candidate = new FakeTransportProto(new FakePortShape("Pipe"));
        var lookup    = EmptyLookup();

        Assert.False(DeepEditEngine.AreProtoShapesCompatible(phantom, candidate, lookup));
    }

    [Fact]
    public void WhenOnlyPhantomHasPortsShape_ThenCompatible()
    {
        var phantom   = new FakeTransportProto(new FakePortShape("Pipe"));
        var candidate = new FakeProtoNoShape();
        var lookup    = EmptyLookup();

        // Candidate doesn't expose PortsShape — be permissive.
        Assert.True(DeepEditEngine.AreProtoShapesCompatible(phantom, candidate, lookup));
    }

    // ── TryHealPhantomField shape-guard integration ─────────────────────────

    [Fact]
    public void WhenSoleCandidateHasIncompatibleShape_ThenHealingFails()
    {
        var phantomShape   = new FakePortShape("Pipe_long");
        var candidateShape = new FakePortShape("Pipe");
        var phantom   = new FakeTransportProto(phantomShape);
        var vanilla   = new FakeTransportProto(candidateShape);
        var entity    = new FakeEntity { m_proto = phantom };
        var fi        = typeof(FakeEntity).GetField("m_proto")!;

        var lookup = EmptyLookup();
        lookup.ByExactType[typeof(FakeTransportProto)] = new List<object> { vanilla };

        bool result = DeepEditEngine.TryHealPhantomField(entity, fi, phantom, lookup);

        Assert.False(result, "Incompatible shape candidate should not be used.");
        Assert.Same(phantom, entity.m_proto); // field unchanged
    }

    [Fact]
    public void WhenSoleCandidateHasMatchingShape_ThenHealingSucceeds()
    {
        var shape   = new FakePortShape("Pipe");
        var phantom = new FakeTransportProto(shape);
        var vanilla = new FakeTransportProto(shape);
        var entity  = new FakeEntity { m_proto = phantom };
        var fi      = typeof(FakeEntity).GetField("m_proto")!;

        var lookup = EmptyLookup();
        lookup.ByExactType[typeof(FakeTransportProto)] = new List<object> { vanilla };

        bool result = DeepEditEngine.TryHealPhantomField(entity, fi, phantom, lookup);

        Assert.True(result);
        Assert.Same(vanilla, entity.m_proto);
    }

    [Fact]
    public void WhenMultipleCandidates_OnlyShapeCompatibleOneIsUsed()
    {
        // phantom has shape "Pipe" — only the second vanilla candidate matches
        var phantomShape = new FakePortShape("Pipe");
        var phantom      = new FakeTransportProto(phantomShape);
        var wrongVanilla = new FakeTransportProto(new FakePortShape("Belt")); // incompatible
        var goodVanilla  = new FakeTransportProto(phantomShape);              // compatible
        var entity       = new FakeEntity { m_proto = phantom };
        var fi           = typeof(FakeEntity).GetField("m_proto")!;

        // Give the phantom a ToString ID so partial-match runs (phantom ID contains candidate ID).
        // FakeTransportProto.ToString() returns shape.Id, and goodVanilla.ToString() = "Pipe".
        var lookup = EmptyLookup();
        lookup.ByExactType[typeof(FakeTransportProto)] = new List<object> { wrongVanilla, goodVanilla };

        bool result = DeepEditEngine.TryHealPhantomField(entity, fi, phantom, lookup);

        Assert.True(result);
        Assert.Same(goodVanilla, entity.m_proto);
    }
}
