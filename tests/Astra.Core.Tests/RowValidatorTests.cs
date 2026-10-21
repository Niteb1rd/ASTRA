using Astra.Core.Cache;
using Astra.Core.Config;
using Astra.Core.Rsi;
using Xunit;

namespace Astra.Core.Tests;

public class RowValidatorTests : IDisposable
{
    private readonly CacheEnv _env = new();

    public void Dispose() => _env.Dispose();

    private RowValidator Validator(int ttlDays = 30) =>
        new(_env.Db, _env.Rsi, _env.Clock, new CacheSettings(ttlDays), _env.Log);

    private static readonly string[] NoOthers = Array.Empty<string>();

    // ----- format -----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]                        // 3 characters
    [InlineData("abcdefghijklmnopqrstuvwxyz")] // 26 characters
    [InlineData("has space")]
    [InlineData("bad/../name")]
    public async Task Player_BadFormat_IsInvalidFormat_WithoutAnyRequest(string text)
    {
        var result = await Validator().ValidatePlayerAsync(text, NoOthers);

        Assert.Equal(RowCheckState.InvalidFormat, result.State);
        Assert.Equal("Handles are 4 to 25 characters: letters, digits, - and _", result.Message);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("this_sid_is_way_too_long_for_rsi")]
    public async Task Org_BadFormat_IsInvalidFormat_WithoutAnyRequest(string text)
    {
        var result = await Validator().ValidateOrgAsync(text, NoOthers);

        Assert.Equal(RowCheckState.InvalidFormat, result.State);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Org_OneCharacterSid_IsFormatOk()
    {
        _env.Rsi.OrgInfos["X"] = CacheEnv.Found(new OrgInfo("X", "Ex Org", 1, null, null, false, Array.Empty<string>()));

        var result = await Validator().ValidateOrgAsync("X", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
    }

    // ----- duplicates -----

    [Fact]
    public async Task Player_Duplicate_IsInvalidFormat_IgnoringCase_WithoutAnyRequest()
    {
        var result = await Validator().ValidatePlayerAsync("nitebird", new[] { "Other_Guy", "NITEBIRD" });

        Assert.Equal(RowCheckState.InvalidFormat, result.State);
        Assert.Equal("already in the list", result.Message);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Org_Duplicate_IsInvalidFormat()
    {
        var result = await Validator().ValidateOrgAsync("j0k3r5", new[] { "J0K3R5" });

        Assert.Equal(RowCheckState.InvalidFormat, result.State);
        Assert.Equal("already in the list", result.Message);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Fact]
    public void IsDuplicate_TrimsAndIgnoresCase()
    {
        Assert.True(RowValidator.IsDuplicate(" Abcd ", new[] { "aBCD" }));
        Assert.False(RowValidator.IsDuplicate("Abcd", new[] { "Abcde" }));
        Assert.False(RowValidator.IsDuplicate("Abcd", NoOthers));
    }

    // ----- players: cache and request -----

    [Fact]
    public async Task Player_FreshInCache_IsValid_WithoutARequest_AndShowsRsiCapitalisation()
    {
        _env.Db.SavePlayer(CacheEnv.Player("NiteBird", CacheEnv.MainOrg("J0K3R5")), CacheSource.Hotkey, _env.Clock.UtcNow);
        _env.Clock.Advance(TimeSpan.FromDays(29));

        var result = await Validator().ValidatePlayerAsync("nitebird", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
        Assert.Equal("NiteBird", result.DisplayText);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Player_FreshInCache_FromHotkey_IsPromotedToPlayerList_KeepingItsAge()
    {
        var saved = _env.Clock.UtcNow;
        _env.Db.SavePlayer(CacheEnv.Player("NiteBird", CacheEnv.MainOrg("J0K3R5")), CacheSource.Hotkey, saved);
        _env.Clock.Advance(TimeSpan.FromDays(2));

        await Validator().ValidatePlayerAsync("NiteBird", NoOthers);

        var cached = _env.Db.GetPlayer("NiteBird")!;
        Assert.Equal(CacheSource.PlayerList, cached.Source);
        Assert.Equal(saved, cached.LastRefreshed);
        Assert.Equal("J0K3R5", cached.Orgs[0].Sid);
    }

    [Fact]
    public async Task Player_StaleInCache_AsksRsiAgain()
    {
        _env.Db.SavePlayer(CacheEnv.Player("NiteBird"), CacheSource.PlayerList, _env.Clock.UtcNow);
        _env.Clock.Advance(TimeSpan.FromDays(31));
        _env.Rsi.Players["NiteBird"] = CacheEnv.Found(CacheEnv.Player("NiteBird"));

        var result = await Validator().ValidatePlayerAsync("NiteBird", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
        Assert.Equal(new[] { "player:NiteBird" }, _env.Rsi.Calls);
    }

    [Fact]
    public async Task Player_KnownNotFound_IsNotFound_WithoutARequest()
    {
        _env.Db.SaveNotFound(NotFoundKind.Player, "Ghosty", CacheSource.Hotkey, _env.Clock.UtcNow);

        var result = await Validator().ValidatePlayerAsync("ghosty", NoOthers);

        Assert.Equal(RowCheckState.NotFound, result.State);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Player_ExpiredNotFoundNote_AsksRsiAgain()
    {
        _env.Db.SaveNotFound(NotFoundKind.Player, "Ghosty", CacheSource.Hotkey, _env.Clock.UtcNow);
        _env.Clock.Advance(TimeSpan.FromDays(31));
        _env.Rsi.Players["Ghosty"] = CacheEnv.Found(CacheEnv.Player("Ghosty"));

        var result = await Validator().ValidatePlayerAsync("Ghosty", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
        Assert.Single(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Player_FoundOnRsi_IsValid_AndCachedAsPlayerList()
    {
        _env.Rsi.Players["nitebird"] = CacheEnv.Found(CacheEnv.Player("NiteBird", CacheEnv.MainOrg("J0K3R5", "PMC")));

        var result = await Validator().ValidatePlayerAsync("nitebird", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
        Assert.Equal("NiteBird", result.DisplayText);
        Assert.Equal(new[] { "player:nitebird" }, _env.Rsi.Calls);
        var cached = _env.Db.GetPlayer("NiteBird")!;
        Assert.Equal(CacheSource.PlayerList, cached.Source);
        Assert.Equal(_env.Clock.UtcNow, cached.LastRefreshed);
    }

    [Fact]
    public async Task Player_SecondCheck_UsesTheCache()
    {
        _env.Rsi.Players["NiteBird"] = CacheEnv.Found(CacheEnv.Player("NiteBird"));
        var validator = Validator();

        await validator.ValidatePlayerAsync("NiteBird", NoOthers);
        var second = await validator.ValidatePlayerAsync("NiteBird", NoOthers);

        Assert.Equal(RowCheckState.Valid, second.State);
        Assert.Single(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Player_404_IsNotFound_AndNegativeCached()
    {
        _env.Rsi.Players["Ghosty"] = RsiResult<PlayerOrgs>.NotFound();
        var validator = Validator();

        var first = await validator.ValidatePlayerAsync("Ghosty", NoOthers);
        var second = await validator.ValidatePlayerAsync("Ghosty", NoOthers);

        Assert.Equal(RowCheckState.NotFound, first.State);
        Assert.Equal(RowCheckState.NotFound, second.State);
        Assert.Single(_env.Rsi.Calls);
        Assert.NotNull(_env.Db.GetNotFoundTime(NotFoundKind.Player, "Ghosty"));
    }

    [Fact]
    public async Task Player_FailedRequest_IsFailed_AndNothingIsCached()
    {
        _env.Rsi.Players["Ghosty"] = RsiResult<PlayerOrgs>.Failed("RSI not reachable");

        var result = await Validator().ValidatePlayerAsync("Ghosty", NoOthers);

        Assert.Equal(RowCheckState.Failed, result.State);
        Assert.Equal("RSI not reachable", result.Message);
        Assert.Null(_env.Db.GetNotFoundTime(NotFoundKind.Player, "Ghosty"));
        Assert.Null(_env.Db.GetPlayer("Ghosty"));
        Assert.NotEmpty(_env.Log.Entries);
    }

    [Fact]
    public async Task Player_FailedRequest_ThenSuccess_AsksAgain()
    {
        var validator = Validator();
        _env.Rsi.Players["Ghosty"] = RsiResult<PlayerOrgs>.Failed("timeout");
        await validator.ValidatePlayerAsync("Ghosty", NoOthers);

        _env.Rsi.Players["Ghosty"] = CacheEnv.Found(CacheEnv.Player("Ghosty"));
        var result = await validator.ValidatePlayerAsync("Ghosty", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
        Assert.Equal(2, _env.Rsi.Calls.Count);
    }

    [Fact]
    public async Task Player_NoReasonGiven_UsesTheFallbackMessage()
    {
        _env.Rsi.Players["Ghosty"] = RsiResult<PlayerOrgs>.Failed("");

        var result = await Validator().ValidatePlayerAsync("Ghosty", NoOthers);

        Assert.Equal("RSI not reachable", result.Message);
    }

    // ----- orgs: cache and request -----

    [Fact]
    public async Task Org_FoundOnRsi_IsValidWithLongName_AndCachedAsOrgList_WithoutFetchingMembers()
    {
        _env.Rsi.OrgInfos["J0K3R5"] = CacheEnv.Found(CacheEnv.Org("J0K3R5", "PMC", "Piracy"));

        var result = await Validator().ValidateOrgAsync("j0k3r5", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
        Assert.Equal("Long name of J0K3R5", result.DisplayText);
        Assert.Equal(new[] { "org:j0k3r5" }, _env.Rsi.Calls);
        var cached = _env.Db.GetOrg("J0K3R5")!;
        Assert.Equal(CacheSource.OrgList, cached.Source);
        Assert.Equal("Long name of J0K3R5", cached.LongName);
        Assert.Null(cached.MembersRefreshed);
        Assert.Empty(_env.Db.GetOrgMembers("J0K3R5"));
    }

    [Fact]
    public async Task Org_FreshInCache_IsValid_WithoutARequest()
    {
        _env.Db.SaveOrgListEntry("J0K3R5", "Jokers Gambit", new[] { "A_player" }, _env.Clock.UtcNow);
        _env.Clock.Advance(TimeSpan.FromDays(10));

        var result = await Validator().ValidateOrgAsync("J0K3R5", NoOthers);

        Assert.Equal(RowCheckState.Valid, result.State);
        Assert.Equal("Jokers Gambit", result.DisplayText);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Org_StaleInCache_AsksRsiAgain()
    {
        _env.Db.SaveOrgListEntry("J0K3R5", "Old name", new[] { "A_player" }, _env.Clock.UtcNow);
        _env.Clock.Advance(TimeSpan.FromDays(8));
        _env.Rsi.OrgInfos["J0K3R5"] = CacheEnv.Found(CacheEnv.Org("J0K3R5"));

        var result = await Validator(ttlDays: 7).ValidateOrgAsync("J0K3R5", NoOthers);

        Assert.Equal("Long name of J0K3R5", result.DisplayText);
        Assert.Single(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Org_KnownNotFound_IsNotFound_WithoutARequest()
    {
        _env.Db.SaveNotFound(NotFoundKind.Org, "NOPE", CacheSource.OrgList, _env.Clock.UtcNow);

        var result = await Validator().ValidateOrgAsync("nope", NoOthers);

        Assert.Equal(RowCheckState.NotFound, result.State);
        Assert.Empty(_env.Rsi.Calls);
    }

    [Fact]
    public async Task Org_404_IsNotFound_AndNegativeCached()
    {
        _env.Rsi.OrgInfos["NOPE"] = RsiResult<OrgInfo>.NotFound();

        var result = await Validator().ValidateOrgAsync("NOPE", NoOthers);

        Assert.Equal(RowCheckState.NotFound, result.State);
        Assert.NotNull(_env.Db.GetNotFoundTime(NotFoundKind.Org, "NOPE"));
    }

    [Fact]
    public async Task Org_FailedRequest_IsFailed_AndNothingIsCached()
    {
        _env.Rsi.OrgInfos["J0K3R5"] = RsiResult<OrgInfo>.Failed("timeout after 15 s");

        var result = await Validator().ValidateOrgAsync("J0K3R5", NoOthers);

        Assert.Equal(RowCheckState.Failed, result.State);
        Assert.Equal("timeout after 15 s", result.Message);
        Assert.Null(_env.Db.GetNotFoundTime(NotFoundKind.Org, "J0K3R5"));
        Assert.Null(_env.Db.GetOrg("J0K3R5"));
    }
}
