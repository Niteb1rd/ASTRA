using Iris.Core.Config;
using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class NameFilterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ExclusionList ListWith(params string[] names)
    {
        var path = _dir.File("exclusions.txt");
        File.WriteAllText(path, string.Join("\n", names) + "\n");
        return ExclusionList.LoadOrCreate(path, new FakeClock(), new ListLog());
    }

    private static SocialConfig PlayersOnly(params string[] handles) =>
        CacheEnv.ConfigWith(handles, Array.Empty<string>());

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("Two Words")]
    [InlineData("bad/name")]
    public void InvalidOrTooShortNames_AreIgnored(string name) =>
        Assert.True(NameFilter.ShouldIgnore(name, ListWith("Other"), PlayersOnly()));

    [Fact]
    public void LengthBoundaries_Are4To25()
    {
        var list = ListWith("Other");
        var config = PlayersOnly();

        Assert.True(NameFilter.ShouldIgnore(new string('a', 3), list, config));
        Assert.False(NameFilter.ShouldIgnore(new string('a', 4), list, config));
        Assert.False(NameFilter.ShouldIgnore(new string('a', 25), list, config));
        Assert.True(NameFilter.ShouldIgnore(new string('a', 26), list, config));
    }

    [Fact]
    public void ExcludedNames_AreIgnored_IgnoringCase()
    {
        var list = ListWith("Stanton");

        Assert.True(NameFilter.ShouldIgnore("Stanton", list, PlayersOnly()));
        Assert.True(NameFilter.ShouldIgnore("STANTON", list, PlayersOnly()));
        Assert.False(NameFilter.ShouldIgnore("Nitebird", list, PlayersOnly()));
    }

    [Fact]
    public void ExcludedButOnThePlayerList_IsKept_ThePlayerListWins()
    {
        // Someone really is called "Gold": the user listed him, so the exclusion does not apply.
        var list = ListWith("Gold");

        Assert.False(NameFilter.ShouldIgnore("Gold", list, PlayersOnly("Gold")));
        Assert.False(NameFilter.ShouldIgnore("gold", list, PlayersOnly("GOLD")));
        Assert.True(NameFilter.ShouldIgnore("Gold", list, PlayersOnly("Nitebird")));
    }

    [Fact]
    public void ExcludedAndOnPlayerList_ButInvalid_IsStillIgnored()
    {
        var list = ListWith("Bob");

        Assert.True(NameFilter.ShouldIgnore("Bob", list, PlayersOnly("Bob")));
    }

    [Theory]
    [InlineData("PA-5968-XF")]
    [InlineData("PA5968XF")]
    [InlineData("pa-5968-xf")]
    public void ContactCodes_AreIgnored_UnlessOnThePlayerList(string handle)
    {
        var list = ListWith("Other");
        Assert.True(NameFilter.ShouldIgnore(handle, list, PlayersOnly()));
        Assert.False(NameFilter.ShouldIgnore(handle, list, PlayersOnly(handle)));
    }

    [Fact]
    public void ContactCodeOnThePlayerList_IsKept_AlsoWithLookAlikes()
    {
        var list = ListWith("Other");

        // the handle of a real player that has the shape of a code; the read has a zero where the handle has an O
        Assert.False(NameFilter.ShouldIgnore("0A-5968-XF", list, PlayersOnly("OA-5968-XF")));
        Assert.True(NameFilter.ShouldIgnore("0A-5968-XF", list, PlayersOnly("OA-5968-XX")));
    }

    [Fact]
    public void ExcludedNames_AreMatchedWithLookAlikes_ButNothingElse()
    {
        var list = ListWith("Hurston", "Lorville");

        Assert.True(NameFilter.ShouldIgnore("HURST0N", list, PlayersOnly()));     // 0 for O
        Assert.True(NameFilter.ShouldIgnore("L0RV1LLE", list, PlayersOnly()));    // 0 for O, 1 for I
        Assert.False(NameFilter.ShouldIgnore("HURSTQN", list, PlayersOnly()));    // Q is not a look-alike of O for names
        Assert.True(NameFilter.ShouldIgnore("HURST0N", list, PlayersOnly("Nitebird")));
        Assert.False(NameFilter.ShouldIgnore("HURST0N", list, PlayersOnly("Hurston")));   // the player list wins, also through a look-alike
    }
}
