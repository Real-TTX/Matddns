using Matddns.Services;

namespace Matddns.Tests;

public class IpFilterTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]    // just outside 172.16.0.0/12
    [InlineData("100.128.0.1", true)]   // just outside CGNAT 100.64.0.0/10
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-an-ip", false)]
    [InlineData("2001:db8::1", false)]  // wrong family
    public void IsPublicV4(string? address, bool expected) =>
        Assert.Equal(expected, IpFilter.IsPublicV4(address));

    [Theory]
    [InlineData("2001:db8::1", true)]
    [InlineData("2a00:1450:4001::1", true)]
    [InlineData("fe80::1", false)]      // link-local
    [InlineData("fd00::1", false)]      // ULA
    [InlineData("fc00::1", false)]      // ULA
    [InlineData("ff02::1", false)]      // multicast
    [InlineData("::1", false)]          // loopback
    [InlineData("1.2.3.4", false)]      // wrong family
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGlobalV6(string? address, bool expected) =>
        Assert.Equal(expected, IpFilter.IsGlobalV6(address));
}
