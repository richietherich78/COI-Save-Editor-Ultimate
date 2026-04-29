using System.Reflection;
using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Tests for the new assignable-type healing strategy and the robust ID extraction
/// fallbacks added to <see cref="DeepEditEngine.ProtoHealingLookup"/>.
/// </summary>
public class ProtoHealingAssignableTests
{
    private class FakeProto { }
    private class FakeBarrierProto : FakeProto { }
    private class FakeBarrierProtoTier1 : FakeBarrierProto { }
    private class FakeBarrierProtoTier2 : FakeBarrierProto { }

    private class FakeEntity
    {
        public FakeBarrierProto? m_proto;
    }

    // Helper to build a lookup with the assignable index populated by mimicking
    // the production build pass (every base up to the engine's "Proto" sentinel).
    private static DeepEditEngine.ProtoHealingLookup BuildAssignable(
        params (Type concrete, object instance)[] entries)
    {
        var lookup = new DeepEditEngine.ProtoHealingLookup();
        foreach (var (t, inst) in entries)
        {
            if (!lookup.ByExactType.TryGetValue(t, out var list))
                lookup.ByExactType[t] = list = new List<object>();
            list.Add(inst);

            // Walk bases up to (but not including) FakeProto to mimic production
            // (which stops at Mafi.Core.Prototypes.Proto).
            for (var bt = t.BaseType; bt is not null && bt != typeof(object) && bt != typeof(FakeProto); bt = bt.BaseType)
            {
                if (!lookup.ByAssignableType.TryGetValue(bt, out var bl))
                    lookup.ByAssignableType[bt] = bl = new List<object>();
                bl.Add(inst);
            }
        }
        return lookup;
    }

    [Fact]
    public void AssignableSearch_HealsWhenOnlySubclassesAreRegistered()
    {
        // Reproduces the BarrierProto scenario: vanilla DB only contains tier subclasses,
        // never an exact BarrierProto entry. The phantom is typed BarrierProto.
        var phantom = new FakeBarrierProto();
        var vanilla = new FakeBarrierProtoTier1();
        var entity = new FakeEntity { m_proto = phantom };
        var fi = typeof(FakeEntity).GetField(nameof(FakeEntity.m_proto))!;

        var lookup = BuildAssignable((typeof(FakeBarrierProtoTier1), vanilla));

        // Strategies 1–4 fail (ByExactType[FakeBarrierProto] is empty).
        // Strategy 5 finds the sole assignable candidate.
        bool result = DeepEditEngine.TryHealPhantomField(entity, fi, phantom, lookup);

        Assert.True(result, "Sole assignable subclass should heal the field.");
        Assert.Same(vanilla, entity.m_proto);
    }

    [Fact]
    public void AssignableSearch_MultipleCandidatesNoIdMatch_ReturnsFalse()
    {
        var phantom = new FakeBarrierProto();
        var t1 = new FakeBarrierProtoTier1();
        var t2 = new FakeBarrierProtoTier2();
        var entity = new FakeEntity { m_proto = phantom };
        var fi = typeof(FakeEntity).GetField(nameof(FakeEntity.m_proto))!;

        var lookup = BuildAssignable(
            (typeof(FakeBarrierProtoTier1), t1),
            (typeof(FakeBarrierProtoTier2), t2));

        // Two assignable candidates, no IDs available → cannot pick safely.
        bool result = DeepEditEngine.TryHealPhantomField(entity, fi, phantom, lookup);

        Assert.False(result);
        Assert.Same(phantom, entity.m_proto);
    }

    [Fact]
    public void AssignableIndex_ExcludesNothingFromExactBucket()
    {
        // The exact-type bucket must always be authoritative and untouched by
        // assignable indexing (i.e. ByAssignableType stores the SAME instance
        // not a transformed copy).
        var t1 = new FakeBarrierProtoTier1();
        var lookup = BuildAssignable((typeof(FakeBarrierProtoTier1), t1));

        Assert.True(lookup.ByExactType.ContainsKey(typeof(FakeBarrierProtoTier1)));
        Assert.True(lookup.ByAssignableType.ContainsKey(typeof(FakeBarrierProto)));
        Assert.Same(t1, lookup.ByExactType[typeof(FakeBarrierProtoTier1)][0]);
        Assert.Same(t1, lookup.ByAssignableType[typeof(FakeBarrierProto)][0]);
    }

    // ── ExtractIdString fallback tests ─────────────────────────────────────

    private struct FakeIdField
    {
        public string Value;
        public FakeIdField(string v) { Value = v; }
        public override string ToString() => Value ?? "";
    }

    private struct FakeIdProperty
    {
        private readonly string _v;
        public FakeIdProperty(string v) { _v = v; }
        public string Value => _v;
        public override string ToString() => _v ?? "";
    }

    private struct FakeIdToStringOnly
    {
        private readonly string _v;
        public FakeIdToStringOnly(string v) { _v = v; }
        public override string ToString() => _v ?? "";
    }

    [Fact]
    public void ExtractIdString_PrefersField_WhenAvailable()
    {
        var fiVal = typeof(FakeIdField).GetField(nameof(FakeIdField.Value));
        PropertyInfo? piVal = null;
        var lookup = new DeepEditEngine.ProtoHealingLookup(null, fiVal, piVal);

        Assert.Equal("foo", lookup.ExtractIdString(new FakeIdField("foo")));
    }

    [Fact]
    public void ExtractIdString_FallsBackToProperty_WhenFieldMissing()
    {
        var piVal = typeof(FakeIdProperty).GetProperty(nameof(FakeIdProperty.Value));
        var lookup = new DeepEditEngine.ProtoHealingLookup(null, fiProtoIdValueField: null, piProtoIdValueProp: piVal);

        Assert.Equal("bar", lookup.ExtractIdString(new FakeIdProperty("bar")));
    }

    [Fact]
    public void ExtractIdString_FallsBackToToString_WhenNoFieldOrProperty()
    {
        var lookup = new DeepEditEngine.ProtoHealingLookup(null, fiProtoIdValueField: null, piProtoIdValueProp: null);

        Assert.Equal("baz", lookup.ExtractIdString(new FakeIdToStringOnly("baz")));
    }

    [Fact]
    public void ExtractIdString_ReturnsNull_ForNullOrEmptyToString()
    {
        var lookup = new DeepEditEngine.ProtoHealingLookup(null, null, null);

        Assert.Null(lookup.ExtractIdString(new FakeIdToStringOnly("")));
    }
}
