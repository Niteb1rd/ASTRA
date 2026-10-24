using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class ContactCodeTests
{
    [Theory]
    [InlineData("PA-5968-XF")]
    [InlineData("pa-5968-xf")]
    [InlineData("QR-2750-MV")]
    public void CodeShapes_AreContactCodes(string name) => Assert.True(ContactCode.IsContactCode(name));

    [Theory]
    [InlineData("PA5968XF")]        // the hyphens are required
    [InlineData("PA5968-XF")]
    [InlineData("PA-5968XF")]
    [InlineData("P1-5968-XF")]      // no look-alikes: a 1 is not a letter
    [InlineData("PA-59O8-XF")]      // an O is not a digit
    [InlineData("PA-5968-X0")]
    [InlineData("PA-IIII-XF")]
    [InlineData("PA-5968-X")]       // too short
    [InlineData("PA-5968-XFF")]
    [InlineData("PA-596-XF")]
    [InlineData("PA-59680-XF")]
    [InlineData("PA_5968_XF")]
    [InlineData("PA-5968-X2")]
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
