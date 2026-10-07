using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Releases;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.ReleaseService;

public static class SoftwareCatalogRegistration
{
    public static IServiceCollection AddSvmSoftwareCatalog(this IServiceCollection services) => services.AddScoped<ISoftwareCatalog, SoftwareCatalog>();
}
internal sealed class SoftwareCatalog(ISoftwareCatalogRepository repository, IUnitOfWork unitOfWork, SiteCatalogOptions options) : ISoftwareCatalog
{
    public async Task<bool> ExistsAsync(Guid id, bool protect, CancellationToken token)
    { options.Require(); return await repository.GetAsync(id, protect, token) is not null; }
    public async Task<SoftwareView> GetAsync(Guid id, bool protect, CancellationToken token) => View(await Existing(id, protect, token));
    public async Task<SoftwareView> CreateAsync(string code, string name, string category, string? description, CancellationToken token)
    {
        RequireWrite();
        if (await repository.CodeExistsAsync(code, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
        var value = new SoftwareProduct(new StrongId<SoftwareProduct>(Guid.NewGuid()), code, name, category, description);
        repository.Add(value); return View(value);
    }
    public async Task<SoftwareView> UpdateAsync(Guid id, long revision, string name, string? description, CancellationToken token)
    {
        RequireWrite(); var value = await Existing(id, true, token);
        if (value.Revision != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        value.Update(name, description); return View(value);
    }
    private async Task<SoftwareProduct> Existing(Guid id, bool protect, CancellationToken token)
    { options.Require(); return await repository.GetAsync(id, protect, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
    private void RequireWrite()
    { options.Require(); if (unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
    private static SoftwareView View(SoftwareProduct x) => new(x.Id.Value, x.Code, x.Name, x.Category, x.Description, null, x.Revision);
}
