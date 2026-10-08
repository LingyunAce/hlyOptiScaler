using Deployer.Core.Domain;

namespace Deployer.Core.Ini;

public sealed record IniPatchResult(
    string Content,
    IReadOnlyList<IniEdit> AppliedEdits,
    IReadOnlyList<IniEdit> AppendedEdits,
    string DetectedNewLine);

public interface IIniPatcher
{
    /// <summary>
    /// 对 <paramref name="originalContent"/> 应用 edits，返回改写后的全文。
    /// 保证：所有以 ; 或 # 开头的行原样保留；输出行数 >= 输入行数。
    /// </summary>
    IniPatchResult Patch(string originalContent, IReadOnlyList<IniEdit> edits);
}
