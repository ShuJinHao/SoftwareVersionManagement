using MediatR;
using FluentValidation;
using Svm.Application.Catalog;
using Svm.Application.Packages;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;

namespace Svm.Application.Releases;

internal sealed class ChangeReleaseSourceHandler(IReleaseImports imports, CatalogCompletion completion)
    : IRequestHandler<ChangeReleaseSourceCommand, OperationResult<ReleaseSourceView>>
{
    public Task<OperationResult<ReleaseSourceView>> Handle(ChangeReleaseSourceCommand x, CancellationToken token) =>
        completion.ExecuteAsync("rel.source.changed", x.Reason, () => imports.ChangeSourceAsync(x.SoftwareId, x.ConnectionId,
            x.ProjectId, x.PackageName, x.Enabled, x.ExpectedRevision, token), view => view.ChangeId!.Value, token);
}
internal sealed class ChangeReleaseSourceAdapter(IReleaseImports imports) : PackageAdapter<ChangeReleaseSourceCommand, ReleaseSourceView>
{
    public override OperationRequestData Describe(ChangeReleaseSourceCommand x) => new(x.Key, OperationValue.Object(G("softwareId", x.SoftwareId)),
        OperationValue.Object(G("connectionId", x.ConnectionId), N("projectId", x.ProjectId), S("packageName", x.PackageName),
            new OperationField("enabled", OperationValue.Boolean(x.Enabled)), N("expectedRevision", x.ExpectedRevision), S("reason", x.Reason)));
    public override async Task<OperationResult<ReleaseSourceView>> RestoreAsync(OperationResultReference r, CancellationToken token) =>
        OperationResult<ReleaseSourceView>.Completed(r.OperationId, await imports.ChangeResultAsync(Id(r), token), Id(r));
}
internal sealed class GetReleaseSourceOptionsHandler(GitLabImportOptions options, IReleaseImportQueries queries) : IRequestHandler<GetReleaseSourceOptionsQuery, ReleaseSourceOptionsView>
{
    public async Task<ReleaseSourceOptionsView> Handle(GetReleaseSourceOptionsQuery x, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = options.Sources.Where(c => c.Enabled).SelectMany(c => c.Projects.SelectMany(p => p.Packages.Select(n =>
            new GitLabSourceOption(c.Id, c.Name, c.BaseUri, p.Id, p.Name, n))))
            .Where(v => string.IsNullOrWhiteSpace(x.Search) || (v.ConnectionName + " " + v.ProjectName + " " + v.PackageName).Contains(x.Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.ConnectionId).ThenBy(v => v.ProjectId).ThenBy(v => v.PackageName, StringComparer.Ordinal)
            .Skip((x.Page - 1) * x.PageSize).Take(x.PageSize + 1).ToArray();
        return new(rows.Take(x.PageSize).ToArray(), rows.Length > x.PageSize ? x.Page + 1 : null, await queries.SourceAsync(x.SoftwareId, token));
    }
}
internal sealed class ChangeReleaseSourceValidator : AbstractValidator<ChangeReleaseSourceCommand>
{
    public ChangeReleaseSourceValidator() => RuleFor(x => x).Must(x => x.Key != Guid.Empty && x.SoftwareId != Guid.Empty && x.ConnectionId != Guid.Empty &&
        x.ProjectId > 0 && GitLabVersions.Segment(x.PackageName) && x.ExpectedRevision >= 0 && !string.IsNullOrWhiteSpace(x.Reason) && x.Reason.Length <= 256)
        .WithErrorCode("VALIDATION_FAILED");
}
internal sealed class GetReleaseSourceOptionsValidator : AbstractValidator<GetReleaseSourceOptionsQuery>
{
    public GetReleaseSourceOptionsValidator() => RuleFor(x => x).Must(x => x.SoftwareId != Guid.Empty && x.Search is null or { Length: <= 200 } &&
        x.Page is >= 1 and <= 10000 && x.PageSize is >= 1 and <= 200).WithErrorCode("VALIDATION_FAILED");
}
