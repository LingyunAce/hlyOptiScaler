using Deployer.Core.Elevation;

if (args.Length != 4 || args[0] != "--job-dir" || args[2] != "--sha256") return 2;
var jobDir = Path.GetFullPath(args[1]);
var expected = args[3].ToLowerInvariant();
var jobIdText = Path.GetFileName(jobDir);
if (!Guid.TryParse(jobIdText, out var jobId)) return 2;
var planPath = Path.Combine(jobDir, "plan.json");
try
{
    var job = new ElevationJob(jobId, planPath, expected, jobDir);
    var result = await ElevationWorker.ExecuteAsync(job);
    return result.Success ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
