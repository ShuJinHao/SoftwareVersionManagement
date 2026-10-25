using Microsoft.EntityFrameworkCore;
using Svm.Core.Instances;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Catalog;
internal sealed class SiteScopeRepository(SvmDbContext context) : ISiteScopeRepository
{
    public async Task<bool> HasSoftwareAsync(Guid softwareId, bool entireSite, IReadOnlyList<Guid> processes, IReadOnlyList<Guid> devices, CancellationToken token)
    {
        var processIds = processes.ToArray(); var deviceIds = devices.ToArray();
        return await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM ins.device_software_bindings b JOIN ins.devices d ON d."Id"=b."DeviceId"
              WHERE b."IsActive" AND b."SoftwareId"={softwareId}
              AND ({entireSite} OR d."ProcessId"=ANY({processIds}) OR d."Id"=ANY({deviceIds}))) AS "Value"
            """).SingleAsync(token);
    }
}
