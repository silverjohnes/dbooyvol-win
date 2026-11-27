using DBooyvol.Core.Sql;
using Xunit;

namespace DBooyvol.Core.Tests;

public class SqlFormatterTests
{
    private static string F(string sql, SqlFormatterOptions? options = null) =>
        SqlFormatter.Format(sql, options);

    [Fact]
    public void FormatsClausesAndSelectList()
    {
        var result = F("select a,b,c from users u where u.active=true and u.id>1 order by u.id desc;");
        Assert.Equal(
            "SELECT\n    a,\n    b,\n    c\nFROM users u\nWHERE u.active = TRUE\n    AND u.id > 1\nORDER BY u.id DESC;",
            result);
    }

    [Fact]
    public void JoinsAndOnCondition()
    {
        var result = F("select 1 from a left join b on b.id=a.id and b.x>0;");
        Assert.Equal(
            "SELECT 1\nFROM a\nLEFT JOIN b ON b.id = a.id\n    AND b.x > 0;",
            result);
    }

    [Fact]
    public void BetweenDoesNotBreakOnAnd()
    {
        Assert.Equal(
            "SELECT count(*)\nFROM t\nWHERE x BETWEEN 1 AND 10;",
            F("select count(*) from t where x between 1 and 10;"));
    }

    [Fact]
    public void SubqueryIsIndented()
    {
        var result = F("with x as (select id,name from users where id>10) select * from x;");
        Assert.Equal(
            "WITH x AS (\n    SELECT\n        id,\n        name\n    FROM users\n    WHERE id > 10\n)\nSELECT *\nFROM x;",
            result);
    }

    [Fact]
    public void InsertValuesAreOnePerLine()
    {
        Assert.Equal(
            "INSERT INTO t(a, b)\nVALUES\n    (1, 2),\n    (3, 4);",
            F("insert into t(a,b) values (1,2),(3,4);"));
    }

    [Fact]
    public void UpdateSetList()
    {
        Assert.Equal(
            "UPDATE t\nSET\n    a = 1,\n    b = 'x'\nWHERE id = 5;",
            F("update t set a=1, b='x' where id=5;"));
    }

    [Fact]
    public void CaseExpression()
    {
        var result = F("select case when a=1 then 'x' else 'y' end as v from t;");
        Assert.Equal(
            "SELECT CASE\n    WHEN a = 1 THEN 'x'\n    ELSE 'y'\nEND AS v\nFROM t;",
            result);
    }

    [Fact]
    public void PreservesLiteralsIdentifiersAndComments()
    {
        var result = F("select e'it\\'s', $$dollar 'quoted'$$, \"Weird Col\" from t -- tail\nwhere a=1;");
        Assert.Contains("e'it\\'s'", result);
        Assert.Contains("$$dollar 'quoted'$$", result);
        Assert.Contains("\"Weird Col\"", result);
        Assert.Contains("-- tail", result);
    }

    [Fact]
    public void FunctionCallsKeepNoSpace()
    {
        Assert.Equal(
            "SELECT\n    lower(name),\n    coalesce(a, b)\nFROM t;",
            F("select lower(name), coalesce(a,b) from t;"));
    }

    [Fact]
    public void CommaFirst()
    {
        var result = F("select a,b from t;", new SqlFormatterOptions { CommaFirst = true });
        Assert.Equal("SELECT\n    a\n    , b\nFROM t;", result);
    }

    [Fact]
    public void IdempotentForEverySample()
    {
        var samples = new[]
        {
            "select a,b from t where x=1 and y=2;",
            "with x as (select 1) select * from x;",
            "insert into t(a,b) values (1,2),(3,4);",
            "update t set a=1, b=2 where id=3;",
            "select case when a=1 then 'x' else 'y' end from t;",
            "select * from a join b on a.id=b.id;",
            "select * from a where (x=1 and y=2) or z=3;",
            "select f(a,b), g(c) from t group by a,b;",
            "delete from t where id in (select id from u);",
            "select 1; select 2;"
        };
        foreach (var sample in samples)
        {
            var once = F(sample);
            Assert.Equal(once, F(once));
        }
    }

    [Fact]
    public void EmptyInputStaysEmpty()
    {
        Assert.Equal("", F("   \n\t "));
    }

    [Fact]
    public void TokensRoundTrip()
    {
        var tokens = SqlFormatter.Tokenize("select 'a''b', :name, $1 from t");
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.String && t.Text == "'a''b'");
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.Parameter && t.Text == ":name");
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.Parameter && t.Text == "$1");
    }
}
