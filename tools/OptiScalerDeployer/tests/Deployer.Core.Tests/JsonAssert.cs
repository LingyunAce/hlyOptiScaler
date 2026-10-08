using Deployer.Core.Serialization;
using Xunit;

namespace Deployer.Core.Tests;

/// <summary>
/// 含 <see cref="System.Collections.Generic.IReadOnlyList{T}"/> 成员的 record 不能用
/// <c>Assert.Equal</c> 直接比较，改比序列化结果。
/// </summary>
/// <remarks>
/// 原因有两层：
/// ① record 合成的 <c>Equals</c> 对每个字段走 <c>EqualityComparer&lt;TField&gt;.Default</c>，
///    而 <c>IReadOnlyList&lt;T&gt;</c> 不实现 <c>IEquatable&lt;IReadOnlyList&lt;T&gt;&gt;</c>，
///    退化为引用相等——数组与 <c>List&lt;T&gt;</c> 是两个实例，永远不等。
/// ② xUnit 的 <c>AssertEqualityComparer&lt;T&gt;</c> 会**先**检查 <c>IEquatable&lt;T&gt;</c>（record 都实现），
///    所以直接委派给 record 自己的 <c>Equals</c>，根本走不到 xUnit 的集合结构化比较分支。
/// 失败时 xUnit 会把所有标量字段展成相等、只有集合的运行时类型不同，极易误判为「元素不等」。
///
/// 改成比序列化结果反而更贴合这些断言真正要守的性质：plan 与 manifest 本来就要
/// 以 JSON 形态跳进程（主进程 plan → 提权 Worker）与落盘（manifest.json）。
///
/// 注意失效方向：将来若有人对这类 record 直接写 <c>Assert.Equal(a, b)</c>，测试会**变红**，
/// 而不是静默通过。这是故意选的：相比手写 <c>Equals</c>/<c>GetHashCode</c> 样板（漏同步一个字段
/// 就是静默假绿），响亮的失败安全得多。红的修法就是改用本辅助类。
/// 不含集合成员的 record（如 <see cref="Deployer.Core.Domain.GameTarget"/>）record 相等本来就对，
/// 继续用 <c>Assert.Equal</c> 即可。
/// </remarks>
public static class JsonAssert
{
    public static void Equivalent<T>(T expected, T actual) =>
        Assert.Equal(DeployerJson.Serialize(expected), DeployerJson.Serialize(actual));
}
