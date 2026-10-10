using Deployer.Core.Domain;

namespace Deployer.Core.Payload;

public interface IPayloadSource
{
    /// <summary>A short description shown to users and included in failure diagnostics.</summary>
    string Description { get; }

    /// <summary>Returns a bundle when available, or null so the source chain can continue.</summary>
    Task<PayloadBundle?> TryGetAsync(CancellationToken ct);
}
