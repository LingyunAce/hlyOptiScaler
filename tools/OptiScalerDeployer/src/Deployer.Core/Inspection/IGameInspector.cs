using Deployer.Core.Domain;

namespace Deployer.Core.Inspection;

public interface IGameInspector
{
    Task<InspectionReport> InspectAsync(GameTarget game, CancellationToken cancellationToken = default);
}
