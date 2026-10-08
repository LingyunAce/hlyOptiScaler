using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deployer.Core.Serialization;

public static class DeployerJson
{
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // 这些文件就是要给人打开来读的，默认 encoder 会把所有中文转成 \uXXXX；
        // 输出只落 .json 文件、从不内联进 HTML，所以放宽转义不带来安全损失。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 冻结成只读：Options 是五种落盘文件共用的单例，{ get; } 只防重新赋值，对象本身仍可改，
    // 一句杂散的 WriteIndented = false 就会静默重排所有文件（而且 JsonAssert 两边一起变，测试照样绿）。
    // MakeReadOnly() 返回 void，串不进初始化表达式，只能放在静态构造里；必须传
    // populateMissingResolver: true，否则无参重载会因未指定 TypeInfoResolver 而抛异常。
    // 附带好处：只读 Options 能走 JsonSerializer 的缓存快路径。
    static DeployerJson() => Options.MakeReadOnly(populateMissingResolver: true);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"无法反序列化 {typeof(T).Name}：内容为 null。");
}
