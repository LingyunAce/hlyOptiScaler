using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace Deployer.Core.Tests;

public class JsonProfileStoreTests
{
    private const string StorePath = @"C:\users\tester\AppData\Roaming\OptiScalerDeployer\profiles.json";

    private static UpscalerProfile Custom(string id) => new()
    {
        Id = id,
        DisplayName = "我的档",
        Description = "desc",
        IsBuiltin = false,
        Edits = new[] { new IniEdit("Upscalers", "Dx12Upscaler", null, "xess") },
    };

    [Fact]
    public void All_returns_builtins_when_store_file_missing()
    {
        var store = new JsonProfileStore(new MockFileSystem(), StorePath);

        Assert.Equal(BuiltinProfiles.All.Count, store.All().Count);
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
    public void User_profile_with_builtin_id_overrides_builtin()
    {
        var fs = new MockFileSystem();
        new JsonProfileStore(fs, StorePath).Save(Custom(BuiltinProfiles.AmdRdna4Id));

        var found = new JsonProfileStore(fs, StorePath).Find(BuiltinProfiles.AmdRdna4Id);

        Assert.NotNull(found);
        Assert.False(found!.IsBuiltin);
        Assert.Equal("xess", found.Edits[0].NewValue);
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
    public void Corrupt_store_file_falls_back_to_builtins_instead_of_throwing()
    {
        var fs = new MockFileSystem();
        fs.AddFile(StorePath, new MockFileData("{ 这不是 JSON"));

        var store = new JsonProfileStore(fs, StorePath);

        Assert.Equal(BuiltinProfiles.All.Count, store.All().Count);
    }
}
