using Jellyfin.Plugin.MyLists;
using Xunit;

public class ImporterTests
{
    private static (string? Imdb, string Title, int? Year)[] Parse(string text) =>
        Importers.ParseText(text).Select(c => (c.Imdb, c.Title, c.Year)).ToArray();

    [Fact]
    public void Plain_title_year_formats()
    {
        var r = Parse("Pulp Fiction (1994)\nJackie Brown, 1997\nReservoir Dogs - 1992\nDeath Proof [2007]");
        Assert.Equal(new (string?, string, int?)[]
        {
            (null, "Pulp Fiction", 1994), (null, "Jackie Brown", 1997), (null, "Reservoir Dogs", 1992), (null, "Death Proof", 2007),
        }, r);
    }

    [Fact]
    public void Chat_assistant_noise_is_removed()
    {
        var r = Parse("Here is your list:\n```\n1. **Metropolis** (1927)\n2) \"The General\", 1926\n- Nosferatu - 1922 – silent classic\n* Sherlock Jr. [1924]\n```");
        Assert.Equal(new (string?, string, int?)[]
        {
            (null, "Metropolis", 1927), (null, "The General", 1926), (null, "Nosferatu", 1922), (null, "Sherlock Jr.", 1924),
        }, r);
    }

    [Fact]
    public void Title_that_ends_in_a_number_is_not_cut()
    {
        var r = Parse("Blade Runner 2049\n2001: A Space Odyssey (1968)");
        Assert.Equal(new (string?, string, int?)[] { (null, "Blade Runner 2049", null), (null, "2001: A Space Odyssey", 1968) }, r);
    }

    [Fact]
    public void Imdb_ids_in_plain_text()
    {
        var r = Parse("tt0110912\nhttps://www.imdb.com/title/tt0114369/");
        Assert.Equal(new[] { "tt0110912", "tt0114369" }, r.Select(x => x.Imdb));
    }

    [Fact]
    public void Imdb_export_csv()
    {
        var csv = "Position,Const,Created,Modified,Description,Title,URL,Title Type,IMDb Rating,Runtime (mins),Year\n1,tt0013442,x,x,,Nosferatu,u,movie,7.9,94,1922\n2,tt0054033,x,x,,\"The Little Shop of Horrors, 1960\",u,movie,6.3,72,1960\n";
        var r = Parse(csv);
        Assert.Equal(2, r.Length);
        Assert.Equal(("tt0013442", "Nosferatu", 1922), (r[0].Imdb, r[0].Title, r[0].Year));
        Assert.Equal("The Little Shop of Horrors, 1960", r[1].Title); // quoted comma stays inside the field
    }

    [Fact]
    public void Letterboxd_export_csv_with_metadata_lines()
    {
        var csv = "Letterboxd list export v7\nDate,Name,Tags,URL,Description\n2024-01-01,My list,,https://boxd.it/x,\n\nPosition,Name,Year,URL,Description\n1,Pulp Fiction,1994,https://boxd.it/a,\n2,\"Léon: The Professional\",1994,https://boxd.it/b,\n";
        var r = Parse(csv);
        Assert.Contains(r, x => x.Title == "Pulp Fiction" && x.Year == 1994);
        Assert.Contains(r, x => x.Title == "Léon: The Professional" && x.Year == 1994);
    }

    [Theory]
    [InlineData("Amélie", "amelie")]
    [InlineData("Léon: The Professional", "leontheprofessional")]
    [InlineData("  Quentin  Tarantino ", "quentintarantino")]
    public void Norm_ignores_case_accents_and_punctuation(string input, string expected) =>
        Assert.Equal(expected, Importers.Norm(input));

    [Fact]
    public void Empty_input_gives_no_rows() => Assert.Empty(Parse("  \n\n"));
}
