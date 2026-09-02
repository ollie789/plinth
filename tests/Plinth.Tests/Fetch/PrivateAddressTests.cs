using System.Net;
using Plinth.Pipeline.Fetch;

namespace Plinth.Tests.Fetch;

public class PrivateAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("104.18.1.22", false)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2606:4700::6810:116", false)]
    public void Blocks_private_loopback_link_local_and_metadata_ranges(string ip, bool blocked) =>
        Assert.Equal(blocked, PrivateAddress.IsBlocked(IPAddress.Parse(ip)));
}
