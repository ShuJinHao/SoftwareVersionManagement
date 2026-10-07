using Svm.SharedKernel.Domain;

namespace Svm.Core.Releases;

public sealed class SoftwareProduct(StrongId<SoftwareProduct> id, string code, string name, string category, string? description)
    : AggregateRoot<StrongId<SoftwareProduct>>(id)
{
    public string Code { get; private set; } = Required(code);
    public string Name { get; private set; } = Required(name);
    public string Category { get; private set; } = category is "UpperComputer" or "Vision" ? category : throw new ArgumentException("Invalid software category.");
    public string? Description { get; private set; } = description;
    public long Revision { get; private set; } = 1;
    public void Update(string name, string? description) { Name = Required(name); Description = description; Revision++; }
    private static string Required(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
}

public interface ISoftwareCatalogRepository
{
    Task<SoftwareProduct?> GetAsync(Guid id, bool protect, CancellationToken token);
    Task<bool> CodeExistsAsync(string code, CancellationToken token);
    void Add(SoftwareProduct software);
}
