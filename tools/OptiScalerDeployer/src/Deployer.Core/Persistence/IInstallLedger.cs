using Deployer.Core.Domain;

namespace Deployer.Core.Persistence;

public interface IInstallLedger
{
    IReadOnlyList<InstallRecord> All();

    InstallRecord? Get(string fingerprint);

    /// <summary>写入或更新（按 Fingerprint 幂等）。</summary>
    void Save(InstallRecord record);

    bool Remove(string fingerprint);

    /// <summary>只改状态，保留其余字段。Fingerprint 不存在时返回 false。</summary>
    bool SetState(string fingerprint, InstallState state);
}
