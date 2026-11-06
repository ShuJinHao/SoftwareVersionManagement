using Microsoft.Extensions.DependencyInjection;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Xunit;
namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class PackageCleanupFileTests
{
    private static ServiceProvider Provider(PackageFileOptions options)=>new ServiceCollection().AddSingleton<IUnitOfWork,OutsideWork>()
        .AddSingleton(TimeProvider.System).AddSvmPackageFiles(options).BuildServiceProvider();
    private static PackageWorkAuthority Work()=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
        "Cleanup",1,Guid.NewGuid(),true,"Running","Deleting","",1,Guid.NewGuid(),DateTimeOffset.UtcNow.AddMinutes(1),123,new string('a',64),"node-a",Guid.NewGuid());
    private static string FileAt(PackageFileOptions options,string folder,Guid id,string suffix)
    {var directory=Path.Combine(options.RootPath,folder);Directory.CreateDirectory(directory);var path=Path.Combine(directory,id.ToString("D")+suffix);File.WriteAllText(path,"owned fixture");return path;}
    [Fact] public async Task CleanupRemovesOnlyInternalPackageAndUploadFilesAndCanRepeatAfterDeletion()
    {
        using var fixture=new PackageDirectoryTests.Directories(); await using var provider=Provider(fixture.Options); await using var scope=provider.CreateAsyncScope();
        var files=scope.ServiceProvider.GetRequiredService<IPackageFiles>(); var work=Work(); var owned=new List<string>();var neighbors=new List<string>();
        foreach(var folder in new[]{"replicas","staging","quarantine"})
        {owned.Add(FileAt(fixture.Options,folder,work.PackageId,".bin"));owned.Add(FileAt(fixture.Options,folder,work.UploadId,".partial"));neighbors.Add(FileAt(fixture.Options,folder,Guid.NewGuid(),".bin"));}
        var checks=0; await files.DeleteLocalAsync(work,_=>{checks++;return Task.CompletedTask;},default);
        Assert.True(checks>=owned.Count+2);Assert.All(owned,p=>Assert.False(File.Exists(p)));Assert.All(neighbors,p=>Assert.True(File.Exists(p)));
        await files.DeleteLocalAsync(work,_=>Task.CompletedTask,default);
        Assert.Equal(2,Directory.GetFiles(Path.Combine(fixture.Options.RootPath,"locks")).Length);
    }
    [Fact] public async Task CleanupWaitsForUploadMutexAndCancellationReleasesItsResources()
    {
        using var fixture=new PackageDirectoryTests.Directories();await using var provider=Provider(fixture.Options);await using var scope=provider.CreateAsyncScope();
        var files=scope.ServiceProvider.GetRequiredService<IPackageFiles>();var work=Work();var path=FileAt(fixture.Options,"staging",work.UploadId,".partial");var checkedLease=false;
        await using(var upload=await files.LockUploadAsync(work.UploadId,default))
        {
            using var cancel=new CancellationTokenSource();
            var deleting=files.DeleteLocalAsync(work,_=>{checkedLease=true;return Task.CompletedTask;},cancel.Token);
            Assert.False(deleting.IsCompleted);Assert.False(checkedLease);Assert.True(File.Exists(path));
            cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>deleting);
        }
        await files.DeleteLocalAsync(work,_=>Task.CompletedTask,default);Assert.False(File.Exists(path));
    }
    [Fact] public async Task LostLeaseStopsFurtherDeletesAndFreshLeaseResumesFromActualFiles()
    {
        using var fixture=new PackageDirectoryTests.Directories();await using var provider=Provider(fixture.Options);await using var scope=provider.CreateAsyncScope();
        var files=scope.ServiceProvider.GetRequiredService<IPackageFiles>();var work=Work();
        var paths=new[]{"replicas","staging","quarantine"}.Select(folder=>FileAt(fixture.Options,folder,work.PackageId,".bin")).ToArray();var checks=0;
        await Assert.ThrowsAsync<RequestRejectedException>(()=>files.DeleteLocalAsync(work,_=>{
            if(++checks==3)throw new RequestRejectedException(RequestFailure.InvalidState);return Task.CompletedTask;},default));
        Assert.Equal(2,paths.Count(File.Exists));await files.DeleteLocalAsync(work,_=>Task.CompletedTask,default);Assert.DoesNotContain(paths,File.Exists);
    }
    [Fact] public async Task CleanupNeverFollowsAnOwnedNameSymlinkToAnotherFile()
    {
        using var fixture=new PackageDirectoryTests.Directories();await using var provider=Provider(fixture.Options);await using var scope=provider.CreateAsyncScope();
        var files=scope.ServiceProvider.GetRequiredService<IPackageFiles>();var work=Work();var other=FileAt(fixture.Options,"replicas",Guid.NewGuid(),".bin");
        var link=Path.Combine(fixture.Options.RootPath,"replicas",work.PackageId.ToString("D")+".bin");File.CreateSymbolicLink(link,other);
        await Assert.ThrowsAsync<RequestRejectedException>(()=>files.DeleteLocalAsync(work,_=>Task.CompletedTask,default));Assert.True(File.Exists(other));
    }
    private sealed class OutsideWork:IUnitOfWork
    {public Guid? CurrentOperationId=>null;public Task<T> ExecuteAsync<T>(Guid id,Func<CancellationToken,Task<T>> action,CancellationToken ct)=>throw new NotSupportedException();}
}
