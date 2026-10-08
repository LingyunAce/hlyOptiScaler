using Deployer.Core.Domain;

namespace Deployer.Core.Profiles;

/// <summary>Profile 的持久化。内置档永远存在且不可删；用户档存 profiles.json。</summary>
public interface IProfileStore
{
    /// <summary>内置档 + 用户档。用户档 Id 与内置档冲突时，用户档胜出（允许覆盖内置行为）。</summary>
    IReadOnlyList<UpscalerProfile> All();

    /// <summary>按 Id 查找，找不到返回 null（调用方决定怎么报错）。</summary>
    UpscalerProfile? Find(string id);

    void Save(UpscalerProfile profile);

    /// <summary>删除用户档。内置档返回 false。</summary>
    bool Delete(string id);
}
