using WindowScatter;
using Xunit;

public class WindowSurfacePolicyTests
{
    [Theory]
    [InlineData(0x08280080u, false)]
    [InlineData(0x00000100u, true)]
    [InlineData(0x00000108u, true)]
    [InlineData(0x00000080u, false)]
    [InlineData(0x00040080u, true)]
    [InlineData(0x082C0080u, true)]
    [InlineData(0x00000000u, true)]
    [InlineData(0x08000000u, true)]
    [InlineData(0x00280000u, true)]
    public void NativeFlagsDistinguishAuxiliaryToolWithoutDroppingExplicitAppWindows(uint style, bool eligible)
        => Assert.Equal(eligible, WindowSurfacePolicy.IsEligible(style));

    [Fact]
    public void IndependentAndOwnedAppSurfacesAreNotCollapsedByProcessOrIdentity()
    {
        uint[] surfaces = [0x100u, 0x100u, 0x40080u, 0x108u, 0x08280080u];
        Assert.Equal([true, true, true, true, false], surfaces.Select(WindowSurfacePolicy.IsEligible));
    }
}
