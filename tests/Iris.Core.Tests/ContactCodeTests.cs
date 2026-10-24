using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class ContactCodeTests
{
    [Theory]
    [InlineData("PA-5968-XF")]
    [InlineData("pa-5968-xf")]
    [InlineData("QR-2750-MV")]
    [InlineData("P1-5968-XF")]      // any letter or digit in any of the 8 places
    [InlineData("PA-59O8-XF")]
    [InlineData("PA-5968-X0")]
    [InlineData("PA-IIII-XF")]
    [InlineData("PA-59E8-XF")]
    [InlineData("12-3456-78")]
    public void CodeShapes_AreContactCodes(string name) => Assert.True(ContactCode.IsContactCode(name));

    [Theory]
    [InlineData("PA5968XF")]        // the hyphens are required
    [InlineData("PA5968-XF")]
    [InlineData("PA-5968XF")]
    [InlineData("PA-5968-X")]       // too short
    [InlineData("PA-5968-XFF")]
    [InlineData("PA-596-XF")]
    [InlineData("PA-59680-XF")]
    [InlineData("PA_5968_XF")]
    [InlineData("P_-5968-XF")]      // an underscore is no letter or digit
    [InlineData("PA--968-XF")]
    [InlineData("1234567890")]
    [InlineData("NITEBIRD")]
    [InlineData("SKYWREN42")]
    [InlineData("AB1234CDE")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherShapes_AreNot(string? name) => Assert.False(ContactCode.IsContactCode(name));
}
