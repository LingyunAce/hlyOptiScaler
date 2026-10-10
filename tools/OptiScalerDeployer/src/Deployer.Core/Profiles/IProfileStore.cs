using Deployer.Core.Domain;

namespace Deployer.Core.Profiles;

/// <summary>Profile 的持久化。内置档永远存在且不可删；用户档存 profiles.json。</summary>
public interface IProfileStore
{
    /// <summary>内置档 + 用户档。用户档 Id 与内置档冲突时，用户档胜出（允许覆盖内置行为）。</summary>
    IReadOnlyList<UpscalerProfile> All();

    /// <summary>按 Id 查找（大小写不敏感），找不到返回 null（调用方决定怎么报错）。</summary>
    UpscalerProfile? Find(string id);

    /// <summary>保存用户档；同 Id 覆盖。字段缺失时抛 ArgumentException——静默存进去、重启后又消失等于丢数据。</summary>
    void Save(UpscalerProfile profile);

    /// <summary>删除用户档。内置档或不存在的 Id 返回 false，且不改动文件。</summary>
    bool Delete(string id);

    /// <summary>
    /// 读用户配置时发现的问题（文件读不出、JSON 坏了、某些记录字段缺失）。null 表示一切正常。
    /// UI 必须把它显示出来：静默退回内置档等于把用户的档弄丢了还不吭声。
    /// </summary>
    string? LoadError { get; }
}
