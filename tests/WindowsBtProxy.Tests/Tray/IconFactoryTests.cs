using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class IconFactoryTests
{
    [Theory]
    [InlineData(Light.Green, 16)]
    [InlineData(Light.Amber, 20)]
    [InlineData(Light.Red, 32)]
    public void Creates_and_destroys_an_icon_for_each_light(Light light, int size)
    {
        var icon = IconFactory.Create(light, size);

        Assert.NotEqual(0, icon);
        Assert.True(Native.DestroyIcon(icon));
    }
}
