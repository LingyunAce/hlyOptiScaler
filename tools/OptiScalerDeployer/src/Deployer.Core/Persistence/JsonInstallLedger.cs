using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Serialization;
using Deployer.Core.Util;

namespace Deployer.Core.Persistence;

public sealed class JsonInstallLedger : IInstallLedger
{
    private readonly IFileSystem _fs;
    private readonly string _path;
    private string? _loadError;

    public JsonInstallLedger(IFileSystem fs, string path)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fs = fs;
        _path = path;
    }

    /// <summary>默认位置：%LOCALAPPDATA%\OptiScalerDeployer\ledger.json</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OptiScalerDeployer", "ledger.json");

    /// <summary>台账不可读时说明原因；此时写操作会失败，原文件保持不动。</summary>
    public string? LoadError => _loadError;

    public IReadOnlyList<InstallRecord> All() => ReadCurrent().ToArray();

    public InstallRecord? Get(string fingerprint) =>
        ReadCurrent().FirstOrDefault(record => Matches(record, fingerprint));

    public void Save(InstallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (RecordProblem(record) is { } problem)
        {
            throw new ArgumentException(problem, nameof(record));
        }

        var current = ReadCurrent();
        var next = current.Where(existing => !Matches(existing, record.Fingerprint)).ToList();
        next.Add(record);
        Persist(next);
    }

    public bool Remove(string fingerprint)
    {
        var current = ReadCurrent();
        var next = current.Where(record => !Matches(record, fingerprint)).ToList();
        if (next.Count == current.Count)
        {
            return false;
        }

        Persist(next);
        return true;
    }

    public bool SetState(string fingerprint, InstallState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        var current = ReadCurrent();
        var index = current.FindIndex(record => Matches(record, fingerprint));
        if (index < 0)
        {
            return false;
        }

        var next = new List<InstallRecord>(current)
        {
            [index] = current[index] with { State = state },
        };
        Persist(next);
        return true;
    }

    private static bool Matches(InstallRecord record, string? fingerprint) =>
        string.Equals(record.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);

    private List<InstallRecord> ReadCurrent()
    {
        _loadError = null;
        if (!_fs.File.Exists(_path))
        {
            return new List<InstallRecord>();
        }

        string json;
        try
        {
            json = _fs.File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadError = $"读取安装台账 {_path} 失败：{ex.Message}。原文件未修改，写入已禁用。";
            throw new InvalidDataException(_loadError, ex);
        }

        try
        {
            var records = DeployerJson.Deserialize<List<InstallRecord>>(json);
            if (records.Any(record => record is null || string.IsNullOrWhiteSpace(record.Fingerprint)))
            {
                throw new InvalidDataException("记录为空或 Fingerprint 为空。");
            }

            var duplicate = records
                .GroupBy(record => record.Fingerprint, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidDataException($"Fingerprint {duplicate.Key} 出现多次。");
            }

            foreach (var record in records)
            {
                var problem = RecordProblem(record);
                if (problem is not null)
                {
                    throw new InvalidDataException(problem);
                }
            }

            return records;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
        {
            // ledger 是已安装内容的索引；坏数据不能被当成空账本后再覆盖。
            _loadError = $"安装台账 {_path} 无效：{ex.Message}。原文件已保留，修复前写入已禁用。";
            throw new InvalidDataException(_loadError, ex);
        }
    }

    private static string? RecordProblem(InstallRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Fingerprint)
            || string.IsNullOrWhiteSpace(record.GameTitle)
            || string.IsNullOrWhiteSpace(record.DeployDirectory)
            || string.IsNullOrWhiteSpace(record.BackupRoot)
            || string.IsNullOrWhiteSpace(record.PayloadVersion)
            || string.IsNullOrWhiteSpace(record.IdentityFileName)
            || record.LatestPlanId == Guid.Empty
            || !Enum.IsDefined(record.State))
        {
            return "记录缺少必需字段或包含无效状态。";
        }

        return null;
    }

    private void Persist(List<InstallRecord> records)
    {
        var json = DeployerJson.Serialize(records);
        new AtomicFileWriter(_fs).Write(new FileWrite(
            string.Empty,
            _path,
            Encoding.UTF8.GetByteCount(json),
            Hashing.Sha256OfString(json),
            json));
    }
}
