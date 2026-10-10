using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using Xunit;

namespace Deployer.Core.Tests;

public class JsonProfileStoreTests
{
    private const string StorePath = @"C:\users\tester\AppData\Roaming\OptiScalerDeployer\profiles.json";
    private const string CorruptPath = StorePath + JsonProfileStore.CorruptSuffix;

    /// <summary>故意声明成 IsBuiltin = true：Save 必须把它拉回 false，写成 false 那条断言就恒真了。</summary>
    private static UpscalerProfile Custom(string id, string displayName = "我的档") => new()
    {
        Id = id,
        DisplayName = displayName,
        Description = "desc",
        IsBuiltin = true,
        Edits = new[] { new IniEdit("Upscalers", "Dx12Upscaler", null, "xess") },
    };

    private static string ReadStore(MockFileSystem fs) => fs.File.ReadAllText(StorePath);

    private class ReadFailingProxy : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public string Path { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_File" && Target is IFileSystem fileSystem)
            {
                var file = DispatchProxy.Create<IFile, ReadFailingProxy>();
                ((ReadFailingProxy)(object)file).Target = fileSystem.File;
                ((ReadFailingProxy)(object)file).Path = Path;
                return file;
            }

            if (Target is IFile && method.Name == nameof(IFile.ReadAllText)
                && args?[0] is string path && path == Path)
            {
                throw new IOException("simulated read failure");
            }

            return method.Invoke(Target, args);
        }
    }

    private static IFileSystem FailReadsAt(MockFileSystem fs, string path)
    {
        var proxy = DispatchProxy.Create<IFileSystem, ReadFailingProxy>();
        ((ReadFailingProxy)(object)proxy).Target = fs;
        ((ReadFailingProxy)(object)proxy).Path = path;
        return proxy;
    }

    [Fact]
    public void All_returns_builtins_when_store_file_missing()
    {
        var store = new JsonProfileStore(new MockFileSystem(), StorePath);

        // 字面量 3，不是 BuiltinProfiles.All.Count：拿被测对象自己的输出当期望值等于没测。
        Assert.Equal(3, store.All().Count);
        Assert.Null(store.LoadError);
    }

    [Fact]
    public void Save_then_Find_roundtrips()
    {
        var fs = new MockFileSystem();
        new JsonProfileStore(fs, StorePath).Save(Custom("mine"));

        var reloaded = new JsonProfileStore(fs, StorePath).Find("mine");

        Assert.NotNull(reloaded);
        Assert.Equal("我的档", reloaded!.DisplayName);
        Assert.Equal("xess", reloaded.Edits[0].NewValue);
    }

    [Fact]
    public void Save_forces_isBuiltin_false_even_when_caller_claims_otherwise()
    {
        var fs = new MockFileSystem();
        new JsonProfileStore(fs, StorePath).Save(Custom("mine"));

        Assert.False(new JsonProfileStore(fs, StorePath).Find("mine")!.IsBuiltin);
    }

    [Fact]
    public void Save_twice_with_same_id_replaces_instead_of_accumulating()
    {
        var fs = new MockFileSystem();
        var store = new JsonProfileStore(fs, StorePath);
        store.Save(Custom("mine", "第一版"));
        store.Save(Custom("mine", "第二版"));

        Assert.Equal(4, store.All().Count);
        Assert.Equal("第二版", store.Find("mine")!.DisplayName);
        Assert.Equal(1, ReadStore(fs).Split("第二版").Length - 1);
    }

    [Fact]
    public void User_profile_with_builtin_id_overrides_builtin_without_duplicating_it()
    {
        var fs = new MockFileSystem();
        new JsonProfileStore(fs, StorePath).Save(Custom(BuiltinProfiles.AmdRdna4Id));

        var store = new JsonProfileStore(fs, StorePath);

        // 3 而不是 4：被顶掉的内置档不能再出现在 UI 的下拉框里。
        Assert.Equal(3, store.All().Count);
        var found = store.Find(BuiltinProfiles.AmdRdna4Id);
        Assert.False(found!.IsBuiltin);
        Assert.Equal("xess", found.Edits[0].NewValue);
    }

    [Fact]
    public void Find_and_Save_ignore_id_case()
    {
        var fs = new MockFileSystem();
        new JsonProfileStore(fs, StorePath).Save(Custom("Mine"));
        var store = new JsonProfileStore(fs, StorePath);

        Assert.Equal("我的档", store.Find("MINE")!.DisplayName);

        // 只有大小写不同的同一个 Id 不能算两个档。
        store.Save(Custom("MINE", "第二版"));
        Assert.Equal(4, store.All().Count);
        Assert.Equal("第二版", store.Find("mine")!.DisplayName);
    }

    [Fact]
    public void Delete_removes_user_profile_but_refuses_builtin()
    {
        var fs = new MockFileSystem();
        var store = new JsonProfileStore(fs, StorePath);
        store.Save(Custom("mine"));

        Assert.True(store.Delete("mine"));
        Assert.Null(new JsonProfileStore(fs, StorePath).Find("mine"));
        Assert.False(new JsonProfileStore(fs, StorePath).Delete(BuiltinProfiles.NvidiaRtxId));
    }

    [Fact]
    public void Delete_of_unknown_id_does_not_touch_the_store_file()
    {
        var fs = new MockFileSystem();

        Assert.False(new JsonProfileStore(fs, StorePath).Delete("nope"));

        // 什么都没删就不该写文件。
        Assert.False(fs.File.Exists(StorePath));
    }

    [Fact]
    public void Save_rejects_blank_id_instead_of_losing_it_on_next_restart()
    {
        var store = new JsonProfileStore(new MockFileSystem(), StorePath);

        // 空白 Id 存得进内存、当次 All() 里看得见、重启后又被 Load 丢掉——那是静默丢数据。
        var ex = Assert.Throws<ArgumentException>(() => store.Save(Custom("  ")));
        Assert.Contains("id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_rejects_profile_with_incomplete_edit()
    {
        var store = new JsonProfileStore(new MockFileSystem(), StorePath);
        var broken = Custom("mine") with
        {
            Edits = new[] { new IniEdit("Upscalers", "", null, "xess") },
        };

        Assert.Throws<ArgumentException>(() => store.Save(broken));
    }

    [Fact]
    public void Save_rejects_null_profile()
    {
        var store = new JsonProfileStore(new MockFileSystem(), StorePath);

        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
    }

    [Fact]
    public void Failed_Save_leaves_previous_state_intact()
    {
        var fs = new MockFileSystem();
        var store = new JsonProfileStore(fs, StorePath);
        store.Save(Custom("mine", "第一版"));
        var before = ReadStore(fs);

        Assert.Throws<ArgumentException>(() => store.Save(Custom("mine") with { DisplayName = " " }));

        Assert.Equal(before, ReadStore(fs));
        Assert.Equal("第一版", store.Find("mine")!.DisplayName);
    }

    [Fact]
    public void Corrupt_store_file_falls_back_to_builtins_and_keeps_a_copy()
    {
        var fs = new MockFileSystem();
        fs.AddFile(StorePath, new MockFileData("{ 这不是 JSON"));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.Equal(3, store.All().Count);
        // 不许一声不响：用户得知道自己的档出事了。
        Assert.NotNull(store.LoadError);
        Assert.Contains(StorePath, store.LoadError!);
        // 也不许让下一次 Save 把原始内容覆盖销毁。
        Assert.Equal("{ 这不是 JSON", fs.File.ReadAllText(CorruptPath));
    }

    [Fact]
    public void Corrupt_store_file_does_not_stop_a_later_Save()
    {
        var fs = new MockFileSystem();
        fs.AddFile(StorePath, new MockFileData("{ 这不是 JSON"));
        var store = new JsonProfileStore(fs, StorePath);

        store.Save(Custom("mine"));

        Assert.Equal("我的档", new JsonProfileStore(fs, StorePath).Find("mine")!.DisplayName);
    }

    [Fact]
    public void Hand_written_pascal_case_json_is_reported_not_silently_dropped()
    {
        var fs = new MockFileSystem();
        // DeployerJson 没开 PropertyNameCaseInsensitive，PascalCase 键名绑不上，
        // required 成员缺失 -> JsonException。整份内容必须被报告，不能当成空表。
        fs.AddFile(StorePath, new MockFileData(
            @"[{ ""Id"":""mine"", ""DisplayName"":""我的档"", ""Description"":""d"", ""IsBuiltin"":false,"
            + @" ""Edits"":[{ ""Section"":""Upscalers"", ""Key"":""Dx12Upscaler"", ""NewValue"":""xess"" }] }]"));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.Equal(3, store.All().Count);
        Assert.NotNull(store.LoadError);
        Assert.True(fs.File.Exists(CorruptPath));
    }

    [Fact]
    public void Loaded_entries_with_missing_fields_are_rejected_and_reported()
    {
        var fs = new MockFileSystem();
        fs.AddFile(StorePath, new MockFileData(
            @"[{ ""id"":"""", ""displayName"":""没有 id"", ""description"":""d"", ""isBuiltin"":false,"
            + @" ""edits"":[{ ""section"":""Upscalers"", ""key"":""Dx12Upscaler"", ""newValue"":""xess"" }] },"
            + @" { ""id"":""nulledits"", ""displayName"":""edits 是 null"", ""description"":""d"", ""isBuiltin"":false,"
            + @" ""edits"":null }, null]"));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.Equal(3, store.All().Count);
        Assert.Null(store.Find("nulledits"));
        // System.Text.Json 不对显式 null 做非空校验，edits:null 能造出 Edits == null 的 record，
        // 不拦住的话 NRE 会推迟到 Task 9 的 planner 里炸。
        Assert.Contains("3 条", store.LoadError!);
        Assert.True(fs.File.Exists(CorruptPath));
    }

    [Fact]
    public void Loaded_null_edit_is_rejected_and_reported()
    {
        var fs = new MockFileSystem();
        fs.AddFile(StorePath, new MockFileData(
            @"[{ ""id"":""mine"", ""displayName"":""我的档"", ""description"":""d"", ""isBuiltin"":false,"
            + @" ""edits"":[null] } ]"));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.Null(store.Find("mine"));
        Assert.Contains("edit 是 null", store.LoadError!);
        Assert.Equal(fs.File.ReadAllText(StorePath), fs.File.ReadAllText(CorruptPath));
    }

    [Fact]
    public void Failed_quarantine_prevents_save_from_overwriting_the_only_copy()
    {
        var fs = new MockFileSystem();
        const string original = "{ 这不是 JSON";
        fs.AddFile(StorePath, new MockFileData(original));
        fs.AddDirectory(CorruptPath);
        var store = new JsonProfileStore(fs, StorePath);

        Assert.Contains("无法另存", store.LoadError!);
        Assert.Throws<IOException>(() => store.Save(Custom("mine")));

        Assert.Equal(original, fs.File.ReadAllText(StorePath));
    }

    [Fact]
    public void Failed_quarantine_prevents_delete_of_otherwise_valid_records()
    {
        var fs = new MockFileSystem();
        const string original = "[{ \"id\":\"mine\", \"displayName\":\"我的档\", \"description\":\"d\", \"isBuiltin\":false, \"edits\":[{\"section\":\"Upscalers\",\"key\":\"Dx12Upscaler\",\"newValue\":\"xess\"}] }, { \"id\":\"bad\", \"displayName\":\"\", \"description\":\"d\", \"isBuiltin\":false, \"edits\":[] }]";
        fs.AddFile(StorePath, new MockFileData(original));
        fs.AddDirectory(CorruptPath);
        var store = new JsonProfileStore(fs, StorePath);

        Assert.NotNull(store.Find("mine"));
        Assert.Throws<IOException>(() => store.Delete("mine"));

        Assert.Equal(original, fs.File.ReadAllText(StorePath));
    }

    [Fact]
    public void Failed_read_prevents_save_from_overwriting_user_profiles()
    {
        var fs = new MockFileSystem();
        const string original = "original user profile data";
        fs.AddFile(StorePath, new MockFileData(original));
        var store = new JsonProfileStore(FailReadsAt(fs, StorePath), StorePath);

        Assert.Contains("读取", store.LoadError!);
        Assert.Throws<IOException>(() => store.Save(Custom("mine")));

        Assert.Equal(original, fs.File.ReadAllText(StorePath));
    }

    [Fact]
    public void Quarantine_preserves_an_existing_recovery_copy()
    {
        var fs = new MockFileSystem();
        const string oldCopy = "earlier corrupt data";
        fs.AddFile(StorePath, new MockFileData("{ 这次损坏"));
        fs.AddFile(CorruptPath, new MockFileData(oldCopy));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.Equal(oldCopy, fs.File.ReadAllText(CorruptPath));
        Assert.Equal("{ 这次损坏", fs.File.ReadAllText(CorruptPath + ".1"));
        Assert.Contains(CorruptPath + ".1", store.LoadError!);
    }

    [Fact]
    public void Hand_written_isBuiltin_true_is_normalized_to_false()
    {
        var fs = new MockFileSystem();
        fs.AddFile(StorePath, new MockFileData(
            @"[{ ""id"":""mine"", ""displayName"":""我的档"", ""description"":""d"", ""isBuiltin"":true,"
            + @" ""edits"":[{ ""section"":""Upscalers"", ""key"":""Dx12Upscaler"", ""newValue"":""xess"" }] }]"));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.False(store.Find("mine")!.IsBuiltin);
        Assert.Null(store.LoadError);
        // 冒充内置档的用户档必须还删得掉。
        Assert.True(store.Delete("mine"));
    }
}
