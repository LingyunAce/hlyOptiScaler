using Deployer.Core.Domain;
using Deployer.Core.Errors;

namespace Deployer.Core.Payload;

/// <summary>Tries payload sources in order and returns the first available bundle.</summary>
public sealed class PayloadSourceChain : IPayloadSource
{
    private readonly IReadOnlyList<IPayloadSource> _sources;

    public PayloadSourceChain(IReadOnlyList<IPayloadSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("payload 来源链不能为空", nameof(sources));
        }

        _sources = sources;
    }

    public string Description => string.Join(" → ", _sources.Select(source => source.Description));

    public async Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
    {
        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            var bundle = await source.TryGetAsync(ct).ConfigureAwait(false);
            if (bundle is not null)
            {
                return bundle;
            }
        }

        return null;
    }

    /// <summary>Required form of TryGetAsync: throws with every attempted source when none work.</summary>
    public async Task<PayloadBundle> GetOrThrowAsync(CancellationToken ct)
    {
        var bundle = await TryGetAsync(ct).ConfigureAwait(false);
        if (bundle is not null)
        {
            return bundle;
        }

        throw new PayloadNotFoundException(
            "已尝试的来源：" + string.Join(" | ", _sources.Select(source =>
                source is GitHubReleasePayloadSource { LastError: { } error } ? source.Description + ": " + error : source.Description)));
    }
}
