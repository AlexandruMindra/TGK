using TGK.Client.Controls;
using Xunit;

namespace TGK.Client.Tests;

public sealed class TextDocumentTests
{
    private static TextDocument Doc(string text)
    {
        var doc = new TextDocument();
        doc.SetText(text);
        return doc;
    }

    private static void Type(TextDocument doc, string text)
    {
        foreach (char ch in text)
            doc.Insert(ch.ToString());
    }

    [Fact]
    public void SetText_SplitsLines_AnyLineEnd_AndIsNotModified()
    {
        TextDocument doc = Doc("a\r\nb\nc\r");
        Assert.Equal(4, doc.LineCount);
        Assert.Equal("a\nb\nc\n", doc.Text);
        Assert.False(doc.IsModified);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void Typing_IsUndoneByWords_AndRedone()
    {
        TextDocument doc = Doc("");
        Type(doc, "hello world");
        Assert.True(doc.IsModified);
        doc.Undo();
        Assert.Equal("hello", doc.Text);
        doc.Undo();
        Assert.Equal("", doc.Text);
        Assert.False(doc.IsModified);
        doc.Redo();
        doc.Redo();
        Assert.Equal("hello world", doc.Text);
        Assert.Equal(new TextPos(0, 11), doc.Caret);
    }

    [Fact]
    public void Paste_OverASelection_SpansLines_AndUndoRestoresTheSelection()
    {
        TextDocument doc = Doc("one\ntwo\nthree");
        doc.Select(new TextPos(0, 1), new TextPos(2, 2));
        doc.Insert("X\r\nY");
        Assert.Equal("oX\nYree", doc.Text);
        Assert.Equal(new TextPos(1, 1), doc.Caret);
        doc.Undo();
        Assert.Equal("one\ntwo\nthree", doc.Text);
        Assert.Equal("ne\ntwo\nth", doc.SelectedText);
    }

    [Fact]
    public void NewLine_KeepsTheIndent_AndBackspaceRemovesAnIndentStep()
    {
        TextDocument doc = Doc("server {\n    listen 80;");
        doc.MoveTo(new TextPos(1, 14));
        doc.NewLine();
        Assert.Equal("server {\n    listen 80;\n    ", doc.Text);
        doc.Backspace(word: false);
        Assert.Equal("server {\n    listen 80;\n", doc.Text);
        doc.Backspace(word: false);
        Assert.Equal("server {\n    listen 80;", doc.Text);
    }

    [Fact]
    public void Indent_DetectsTheUnit_AndShiftsSelectedLines()
    {
        TextDocument doc = Doc("a:\n  b: 1\n  c: 2\nd: 3");
        Assert.Equal("  ", doc.IndentUnit);
        doc.Select(new TextPos(1, 0), new TextPos(3, 0)); // lines 2 and 3, not the one the selection ends at
        doc.Indent(outdent: false);
        Assert.Equal("a:\n    b: 1\n    c: 2\nd: 3", doc.Text);
        Assert.Equal("    b: 1\n    c: 2", doc.SelectedText);
        doc.Indent(outdent: true);
        doc.Indent(outdent: true);
        Assert.Equal("a:\nb: 1\nc: 2\nd: 3", doc.Text);

        TextDocument tabs = Doc("x\n\ty");
        Assert.Equal("\t", tabs.IndentUnit);
        tabs.MoveTo(new TextPos(0, 0));
        tabs.Indent(outdent: false);
        Assert.Equal("\tx\n\ty", tabs.Text);
    }

    [Fact]
    public void Find_GoesBothWays_AndWrapsAround()
    {
        TextDocument doc = Doc("Port 22\n# port 2222\nListenAddress ::");
        Assert.Equal((new TextPos(1, 2), new TextPos(1, 6)), doc.Find("port", new TextPos(0, 1), forward: true, matchCase: false));
        Assert.Equal((new TextPos(0, 0), new TextPos(0, 4)), doc.Find("port", new TextPos(1, 3), forward: true, matchCase: false));
        Assert.Equal((new TextPos(0, 0), new TextPos(0, 4)), doc.Find("Port", new TextPos(0, 1), forward: true, matchCase: true)); // wrapped
        Assert.Equal((new TextPos(0, 0), new TextPos(0, 4)), doc.Find("port", new TextPos(1, 2), forward: false, matchCase: false));
        Assert.Equal((new TextPos(1, 2), new TextPos(1, 6)), doc.Find("port", new TextPos(0, 0), forward: false, matchCase: false));
        Assert.Null(doc.Find("missing", new TextPos(0, 0), forward: true, matchCase: false));
        Assert.Equal(2, doc.CountMatches("port", matchCase: false));
        Assert.Equal(1, doc.CountMatches("port", matchCase: true));
    }

    [Fact]
    public void ReplaceAll_IsOneUndoStep()
    {
        TextDocument doc = Doc("a=1\nb=1\nc=2");
        Assert.Equal(2, doc.ReplaceAll("=1", "=3", matchCase: true));
        Assert.Equal("a=3\nb=3\nc=2", doc.Text);
        doc.Undo();
        Assert.Equal("a=1\nb=1\nc=2", doc.Text);
    }

    [Fact]
    public void ReadOnly_RefusesTyping_ButAFollowedLogGrows()
    {
        TextDocument doc = Doc("line 1\n");
        doc.ReadOnly = true;
        doc.Insert("x");
        doc.Backspace(word: false);
        Assert.Equal("line 1\n", doc.Text);
        doc.Append("line 2\nline 3\n");
        Assert.Equal("line 1\nline 2\nline 3\n", doc.Text);
        Assert.False(doc.IsModified);
        doc.RemoveFirstLines(2);
        Assert.Equal("line 3\n", doc.Text);
    }

    [Fact]
    public void WordsLinesAndHome()
    {
        TextDocument doc = Doc("    key = some_value; # note");
        doc.SelectWordAt(new TextPos(0, 14));
        Assert.Equal("some_value", doc.SelectedText);
        Assert.Equal(new TextPos(0, 4), doc.Home(new TextPos(0, 10)));
        Assert.Equal(new TextPos(0, 0), doc.Home(new TextPos(0, 4)));
        Assert.Equal(new TextPos(0, 7), doc.Right(new TextPos(0, 4), word: true));
        Assert.Equal(new TextPos(0, 4), doc.Left(new TextPos(0, 7), word: true));

        TextDocument lines = Doc("a\nb");
        lines.SelectLine(0);
        Assert.Equal("a\n", lines.SelectedText);
        Assert.Equal(new TextPos(0, 1), lines.Left(new TextPos(1, 0), word: false));
    }
}
