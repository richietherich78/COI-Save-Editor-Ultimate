using Xunit;

namespace COISaveEditorUltimate.Tests;

using System.Reflection;
using COISaveEditorUltimate.DeepEdit;

public class ReflectionUtilityTests
{
    private class GrandParent
    {
        private int _grandParentField = 42;
        private void GrandParentMethod() { }
    }

    private class Parent : GrandParent
    {
        private int _parentField = 99;
        private void ParentMethod(int x) { }
    }

    private class Child : Parent
    {
        private int _childField = 7;
    }

    [Fact]
    public void WhenFieldOnDeclaredTypeThenFindsIt()
    {
        FieldInfo? fi = DeepEditEngine.FindFieldDeep(typeof(Child), "_childField");

        Assert.NotNull(fi);
        Assert.Equal("_childField", fi.Name);
    }

    [Fact]
    public void WhenFieldOnParentThenFindsIt()
    {
        FieldInfo? fi = DeepEditEngine.FindFieldDeep(typeof(Child), "_parentField");

        Assert.NotNull(fi);
        Assert.Equal("_parentField", fi.Name);
    }

    [Fact]
    public void WhenFieldOnGrandParentThenFindsIt()
    {
        FieldInfo? fi = DeepEditEngine.FindFieldDeep(typeof(Child), "_grandParentField");

        Assert.NotNull(fi);
        Assert.Equal("_grandParentField", fi.Name);
    }

    [Fact]
    public void WhenFieldDoesNotExistThenReturnsNull()
    {
        FieldInfo? fi = DeepEditEngine.FindFieldDeep(typeof(Child), "_nonExistent");

        Assert.Null(fi);
    }

    [Fact]
    public void WhenMethodOnDeclaredTypeThenFindsIt()
    {
        MethodInfo? mi = DeepEditEngine.FindMethodDeep(typeof(Parent), "ParentMethod");

        Assert.NotNull(mi);
        Assert.Equal("ParentMethod", mi.Name);
    }

    [Fact]
    public void WhenMethodOnAncestorThenFindsIt()
    {
        MethodInfo? mi = DeepEditEngine.FindMethodDeep(typeof(Child), "GrandParentMethod");

        Assert.NotNull(mi);
    }

    [Fact]
    public void WhenMethodDoesNotExistThenReturnsNull()
    {
        MethodInfo? mi = DeepEditEngine.FindMethodDeep(typeof(Child), "NoSuchMethod");

        Assert.Null(mi);
    }

    [Fact]
    public void WhenMethodWithParamTypesThenFindsCorrectOverload()
    {
        MethodInfo? mi = DeepEditEngine.FindMethodDeep(typeof(Child), "ParentMethod", typeof(int));

        Assert.NotNull(mi);
        Assert.Single(mi.GetParameters());
        Assert.Equal(typeof(int), mi.GetParameters()[0].ParameterType);
    }
}
