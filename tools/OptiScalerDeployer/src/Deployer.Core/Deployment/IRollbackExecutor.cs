using Deployer.Core.Domain;

namespace Deployer.Core.Deployment;

public interface IRollbackExecutor
{
    RollbackResult Rollback(string manifestPath);
}
