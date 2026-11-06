using System.Text.Json;
using Svm.EntityFrameworkCore.Migrations;
using Svm.Services.Contracts.Framework;
using Svm.Migration;
using Svm.ServiceDefaults;

if (args.Length == 3 && args[1] == "--directory" && args[0] is "recovery-review" or "recovery-open")
{
    try { await RecoveryApproval.ExecuteAsync(args[0], args[2], CancellationToken.None); Console.WriteLine("RECOVERY_STATE_SAVED; services were not started."); return 0; }
    catch { Console.Error.WriteLine("RECOVERY_REVIEW_REQUIRED; services remain stopped; private details suppressed."); return 3; }
}

if (args.Length == 3 && args[1] == "--config" && args[0] is "backup-seal" or "backup-verify" or "backup-restore")
{
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    try
    {
        var backup = await DeploymentBackup.ExecuteAsync(args[0], args[2], cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "Verified", backup.Platform, backup.Project, backup.ReleaseCommit, backup.CreatedAt, files = backup.Files.Length }));
        return 0;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("BACKUP_CANCELED; incomplete output is retained and never started."); return 130; }
    catch { Console.Error.WriteLine("BACKUP_FAILED; private details suppressed, incomplete output is retained."); return 3; }
}

if (args.Length != 3 || args[1] != "--config" || args[0] is not ("status" or "script" or "apply" or "seed"))
{
    Console.Error.WriteLine("Usage: Svm.Migration status|script|apply|seed --config <private-configuration-file>");
    return 2;
}
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
try
{
    if (args[0] == "seed")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { seeded = await PersonnelSeed.ExecuteAsync(args[2], stopping.Token) }));
        return 0;
    }
    var runner = new MigrationRunner(MigrationConfiguration.Load(args[2]));
    if (args[0] == "script") Console.Write(runner.GenerateScript());
    else Console.WriteLine(JsonSerializer.Serialize(args[0] == "apply"
        ? await runner.ApplyAsync(stopping.Token) : await runner.StatusAsync(stopping.Token)));
    return 0;
}
catch (MigrationBusyException) { Console.Error.WriteLine("MIGRATION_BUSY"); return 4; }
catch (RequestRejectedException error) { Console.Error.WriteLine(error.Code); return error.Failure == RequestFailure.ConfigurationInvalid ? 2 : 3; }
catch (OperationCanceledException) { Console.Error.WriteLine("MIGRATION_CANCELED"); return 130; }
catch (PersistenceException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { Failure = error.Failure.ToString(), error.SqlState }));
    return error.Failure == PersistenceFailure.ConfigurationInvalid ? 2 : 3;
}
catch (Exception) { Console.Error.WriteLine("MIGRATION_FAILED (diagnostic details redacted)"); return 3; }
