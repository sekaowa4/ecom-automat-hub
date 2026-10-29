using Repricing.Execution;
namespace Repricing.Tests;

public class ZiniaoPortDiscoveryTests
{
    [Fact] public void OnlyUsesLoopbackListenersOwnedByTheExactStoreProcesses()
    {
        const string table="""
          TCP    127.0.0.1:38118    0.0.0.0:0       LISTENING       123
          TCP    127.0.0.1:38119    0.0.0.0:0       LISTENING       456
          TCP    0.0.0.0:38120      0.0.0.0:0       LISTENING       123
          TCP    127.0.0.1:38121    127.0.0.1:80    ESTABLISHED     123
          TCP    [::1]:38122       [::]:0          LISTENING       123
          TCP    127.0.0.1:38118    0.0.0.0:0       LISTENING       123
          TCP    127.0.0.1:99999    0.0.0.0:0       LISTENING       123
        """;
        Assert.Equal(new[]{38118},ZiniaoPortDiscovery.Parse(table,[123]));
        Assert.Empty(ZiniaoPortDiscovery.Parse(table,[]));
    }
}
