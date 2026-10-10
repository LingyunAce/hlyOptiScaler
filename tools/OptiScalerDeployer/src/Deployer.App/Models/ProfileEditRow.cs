using Deployer.Core.Domain;

namespace Deployer.App.Models;

public sealed class ProfileEditRow(IniEdit edit)
{
    public string Section { get; set; } = edit.Section;
    public string Key { get; set; } = edit.Key;
    public string NewValue { get; set; } = edit.NewValue;
    public IniEdit ToIniEdit() => new(Section, Key, edit.OldValue, NewValue);
    private readonly IniEdit edit = edit;
}
