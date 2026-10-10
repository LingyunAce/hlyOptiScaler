using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Util;

namespace Deployer.Core.GameDiscovery;

public sealed class ManualFolderStore(IFileSystem fs, AtomicFileWriter writer, string configPath)
{
    private readonly object _sync = new();
    public IReadOnlyList<ManualFolderEntry> Load()
    {
        lock (_sync)
        {
            string text;
            try { text = fs.File.ReadAllText(configPath); }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            try
            {
                var entries = JsonSerializer.Deserialize<List<ManualFolderEntry>>(text)
                    ?? throw new InvalidDataException("手动目录配置为空。");
                if (entries.Any(x => x is null || string.IsNullOrWhiteSpace(x.Title) || string.IsNullOrWhiteSpace(x.Folder)) ||
                    entries.Select(x => GameLibrary.Normalize(x.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
                    throw new InvalidDataException("手动目录配置含空字段或重复目录。");
                return entries;
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            { throw new InvalidDataException($"手动目录配置损坏，原文件已保留：{configPath}", e); }
        }
    }

    public void Add(string title, string folder)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("标题和目录不能为空。");
        lock (_sync)
        {
            var entries = Load().ToList(); var full = fs.Path.GetFullPath(folder);
            if (!entries.Any(x => string.Equals(GameLibrary.Normalize(x.Folder), GameLibrary.Normalize(full), StringComparison.OrdinalIgnoreCase)))
                entries.Add(new(title.Trim(), full));
            Save(entries);
        }
    }

    public void Remove(string folder)
    {
        lock (_sync)
        {
            var full = GameLibrary.Normalize(fs.Path.GetFullPath(folder));
            Save(Load().Where(x => !string.Equals(GameLibrary.Normalize(x.Folder), full, StringComparison.OrdinalIgnoreCase)).ToList());
        }
    }

    private void Save(List<ManualFolderEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        writer.Write(new FileWrite(string.Empty, configPath, Encoding.UTF8.GetByteCount(json), Hashing.Sha256OfString(json), json));
    }
}

public sealed record ManualFolderEntry(string Title, string Folder);
