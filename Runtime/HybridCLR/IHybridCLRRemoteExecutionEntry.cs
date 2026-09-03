using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution.HybridCLR
{
    public interface IHybridCLRRemoteExecutionEntry
    {
        Task ExecuteAsync(CancellationToken cancellationToken);
    }
}
