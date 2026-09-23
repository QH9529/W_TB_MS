using System.Windows.Forms;
using Xunit;

namespace W_TB_MS.Tests;

public class TreeSelectorToggleTests
{
    [Fact]
    public void ApplyTreeSelectorToggle_HidesAllTrees_WhenFirstIsVisible()
    {
        var first = new TreeView();
        var second = new TreeView();
        var third = new TreeView();
        first.Visible = true;
        second.Visible = true;
        third.Visible = true;

        bool shown = MainForm.ApplyTreeSelectorToggle(first, second, third);

        Assert.False(shown);
        Assert.False(first.Visible);
        Assert.False(second.Visible);
        Assert.False(third.Visible);
    }

    [Fact]
    public void ApplyTreeSelectorToggle_ShowsAllTrees_WhenFirstIsHidden()
    {
        var first = new TreeView();
        var second = new TreeView();
        var third = new TreeView();
        first.Visible = false;
        second.Visible = false;
        third.Visible = false;

        bool shown = MainForm.ApplyTreeSelectorToggle(first, second, third);

        Assert.True(shown);
        Assert.True(first.Visible);
        Assert.True(second.Visible);
        Assert.True(third.Visible);
    }

    [Fact]
    public void ApplyTreeSelectorToggle_SynchronizesMixedState_FromFirstTree()
    {
        var first = new TreeView();
        var second = new TreeView();
        var third = new TreeView();
        first.Visible = true;
        second.Visible = false;
        third.Visible = false;

        bool shown = MainForm.ApplyTreeSelectorToggle(first, second, third);

        Assert.False(shown);
        Assert.All(new[] { first, second, third }, tree => Assert.False(tree.Visible));
    }

    [Fact]
    public void ApplyTreeSelectorToggle_TogglesBack_AndForwards()
    {
        var first = new TreeView();
        var second = new TreeView();
        var third = new TreeView();

        Assert.False(MainForm.ApplyTreeSelectorToggle(first, second, third));
        Assert.All(new[] { first, second, third }, tree => Assert.False(tree.Visible));

        Assert.True(MainForm.ApplyTreeSelectorToggle(first, second, third));
        Assert.All(new[] { first, second, third }, tree => Assert.True(tree.Visible));
    }
}
