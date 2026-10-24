using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class ContactCodeTests
{
    [Theory]
    [InlineData("PA-5968-XF")]
    [InlineData("PA5968XF")]
    [InlineData("pa-5968-xf")]
    [InlineData("QR-2750-MV")]
    [InlineData("PA5968-XF")]      // each hyphen is optional on its own
    [InlineData("PA-5968XF")]
    [InlineData("P1-5968-XF")]     // an I read as 1 in a letter slot
    [InlineData("PA-59O8-XF")]     // a 0 read as O in a digit slot
    [InlineData("PA-5968-X0")]
    [InlineData("PA-IIII-XF")]
    public void CodeShapes_AreContactCodes(string name) => Assert.True(ContactCode.IsContactCode(name));

    [Theory]
    [InlineData("PA-5968-X")]       // too short
    [InlineData("PA-5968-XFF")]
    [InlineData("PA-596-XF")]
    [InlineData("PA-59680-XF")]
    [InlineData("PA_5968_XF")]
    [InlineData("PA-5968-X2")]      // 2 is not a letter look-alike
    [InlineData("P2-5968-XF")]
    [InlineData("PA-59E8-XF")]
    [InlineData("1234567890")]
    [InlineData("NITEBIRD")]
    [InlineData("SKYWREN42")]
    [InlineData("AB1234CDE")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherShapes_AreNot(string? name) => Assert.False(ContactCode.IsContactCode(name));
}
