using DBooyvol.Core.Sql;
using DBooyvol.Core.Text;
using Xunit;

namespace DBooyvol.Core.Tests;

public class SQLTextTests
{
    [Fact]
    public void SplitsStatementsIgnoringQuotedSemicolons()
    {
        var sql = "select ';' as x; -- c;\nselect 2;";
        var statements = SQLText.Statements(sql);
        Assert.Equal(2, statements.Count);
        Assert.Contains("select ';' as x", statements[0].Text);
        Assert.Contains("select 2", statements[1].Text);
    }

    [Fact]
    public void DollarQuotedBodyIsNotSplit()
    {
        var sql = "do $$ begin perform 1; end $$; select 1;";
        Assert.Equal(2, SQLText.Statements(sql).Count);
    }

    [Fact]
    public void StatementAtCaretPicksEnclosingStatement()
    {
        var sql = "select 1;\nselect 2;";
        Assert.Equal("select 1", SQLText.StatementAt(sql, 3));
        Assert.Equal("select 2", SQLText.StatementAt(sql, sql.Length));
    }

    [Fact]
    public void FindsNamedParametersButNotCasts()
    {
        var sql = "select a::int, b := 1, :first, :second from t where x = :first";
        var parameters = SQLText.Parameters(sql);
        Assert.Equal(new[] { "first", "second" }, parameters);
    }

    [Fact]
    public void RowReturningDetection()
    {
        Assert.True(SQLText.IsRowReturning("-- c\n select 1"));
        Assert.True(SQLText.IsRowReturning("WITH x AS (SELECT 1) SELECT * FROM x"));
        Assert.False(SQLText.IsRowReturning("update t set a=1"));
        Assert.Equal("select 1", SQLText.FirstRowReturningStatement("update t set a=1; select 1;"));
    }

    [Fact]
    public void FirstQuotedFilterValueSkipsLikeMask()
    {
        var sql = "select * from t where name like '%abc%' and x='q'";
        var range = SQLText.FirstQuotedFilterValue(sql);
        Assert.NotNull(range);
        Assert.Equal("abc", sql.Substring(range!.Value.Start, range.Value.Length));
    }

    [Fact]
    public void ToggleCommentAddsAndRemoves()
    {
        var text = "select 1\nselect 2";
        var (commented, start, length) = SQLText.ToggleComment(text, 0, 0);
        Assert.Equal("--select 1\nselect 2", commented);
        Assert.Equal(0, start);
        Assert.Equal(0, length);

        var (restored, _, _) = SQLText.ToggleComment(commented, 0, 0);
        Assert.Equal(text, restored);
    }

    [Fact]
    public void ToggleCommentKeepsIndent()
    {
        var (text, _, _) = SQLText.ToggleComment("    select 1", 0, 0);
        Assert.Equal("    --select 1", text);
    }

    [Fact]
    public void MarkedCommentTogglesOnlyMarkedLines()
    {
        var text = "select 1 --!#\nselect 2";
        var (commented, _, _) = SQLText.ToggleMarkedComment(text, "!#", 0, 0);
        Assert.Equal("--select 1 --!#\nselect 2", commented);
    }
}

public class SelectionRulesTests
{
    [Fact]
    public void WordKeepsDotsAndUnderscores()
    {
        var range = TextSelectionRules.WordRange("select public.users.id from t", 12);
        Assert.NotNull(range);
        Assert.Equal("public.users.id", "select public.users.id from t".Substring(range!.Value.Start, range.Value.Length));
    }

    [Fact]
    public void TripleClickSelectsQuotedContents()
    {
        var text = "where name = 'abc'";
        var range = TextSelectionRules.LiteralContentsRange(text, 15);
        Assert.NotNull(range);
        Assert.Equal("abc", text.Substring(range!.Value.Start, range.Value.Length));
    }

    [Fact]
    public void TripleClickTrimsLikeWildcards()
    {
        var text = "where name like '%abc%'";
        var range = TextSelectionRules.LiteralContentsRange(text, 20);
        Assert.NotNull(range);
        Assert.Equal("abc", text.Substring(range!.Value.Start, range.Value.Length));
    }

    [Fact]
    public void TripleClickSelectsInnermostParens()
    {
        var text = "select f(g(1), 2)";
        var range = TextSelectionRules.ParenContentsRange(text, 12);
        Assert.NotNull(range);
        Assert.Equal("1", text.Substring(range!.Value.Start, range.Value.Length));
    }
}

public class SearchMatcherTests
{
    [Fact]
    public void MatchesAcrossLayouts()
    {
        // "users" typed on the Russian layout.
        Assert.True(SearchMatcher.Matches("SELECT * FROM users", "гыукы"));
    }

    [Fact]
    public void AllRangesFindsBothVariants()
    {
        var ranges = SearchMatcher.AllRanges("abc abc", "фис");
        Assert.Equal(2, ranges.Count);
    }
}
