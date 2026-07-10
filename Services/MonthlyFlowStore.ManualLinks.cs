using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PADS.MoneyFlow.Api.Dtos;
using PADS.MoneyFlow.Api.Models;
using PADS.MoneyFlow.Api.Persistence;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PADS.MoneyFlow.Api.Services;

public sealed partial class MonthlyFlowStore
{
    public async Task<ManualContractLinkResult> CreateManualContractLinkAsync(
        string projectCode,
        string? targetContractRowKey,
        string? sourceSubcontractorName,
        string? sourceObjectNumber,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(targetContractRowKey))
            {
                return new ManualContractLinkResult(false, "Nepasirinkta tikslinė sutarties eilutė.", null);
            }

            var contract = await db.SubcontractorContracts
                .AsNoTracking()
                .FirstOrDefaultAsync(existing => existing.RowKey == targetContractRowKey, cancellationToken);
            if (contract is null)
            {
                return new ManualContractLinkResult(false, "Tikslinė sutarties eilutė nerasta.", null);
            }

            var requestedParent = ParseProjectObjectCode(projectCode).ParentProjectCode;
            var contractParent = ParentProjectCodeFor(contract.ProjectCode, contract.ObjectNumber);
            if (!string.Equals(NormalizeKeyPart(requestedParent), NormalizeKeyPart(contractParent), StringComparison.Ordinal))
            {
                return new ManualContractLinkResult(false, "Tikslinė sutartis priklauso kitam projektui.", null);
            }

            // Rows may only be connected within one object scope: an invoice row
            // on P1677-05 must never merge into a contract on P1677-06.
            if (string.IsNullOrWhiteSpace(sourceObjectNumber))
            {
                return new ManualContractLinkResult(false, "Reikalingas pradinis objekto numeris.", null);
            }

            var contractObjectScope = NormalizeKeyPart(EffectiveObjectNumber(contract.ProjectCode, contract.ObjectNumber));
            var sourceObjectScope = NormalizeKeyPart(EffectiveObjectNumber(sourceObjectNumber, sourceObjectNumber));
            if (!string.Equals(sourceObjectScope, contractObjectScope, StringComparison.Ordinal))
            {
                return new ManualContractLinkResult(
                    false,
                    $"Rows belong to different objects ({sourceObjectScope} vs {contractObjectScope}). Only rows on the same object can be connected.",
                    null);
            }

            // Resolve both sides exactly like read-time matching does (alias map
            // included) so the stored keys always hit the same dictionary slots.
            var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
            var sourceIdentity = SubcontractorNormalizer.NormalizeSubcontractorIdentity(sourceSubcontractorName);
            var sourceKey = SubcontractorMatchKey(sourceSubcontractorName, aliasMap);
            var targetKey = SubcontractorMatchKey(contract.SubcontractorName, aliasMap);
            if (string.IsNullOrWhiteSpace(sourceIdentity.BaseName) || string.IsNullOrWhiteSpace(sourceKey))
            {
                return new ManualContractLinkResult(false, "Reikalingas pradinis subrangovo pavadinimas.", null);
            }

            if (string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
            {
                return new ManualContractLinkResult(false, "Šios eilutės jau atitinka pagal pavadinimą; nėra ką susieti.", null);
            }

            var scopeProjectCode = NormalizeKeyPart(contractParent);
            var scopeObjectNumber = NormalizeKeyPart(contract.ObjectNumber);
            var link = await db.ManualContractLinks
                .FirstOrDefaultAsync(
                    existing => existing.ProjectCode == scopeProjectCode
                        && existing.ObjectNumber == scopeObjectNumber
                        && existing.SourceSubcontractorKey == sourceKey,
                    cancellationToken);
            if (link is null)
            {
                link = new ManualContractLink
                {
                    ProjectCode = scopeProjectCode,
                    ObjectNumber = scopeObjectNumber,
                    SourceSubcontractorKey = sourceKey,
                    TargetSubcontractorKey = targetKey,
                    SourceSubcontractorName = sourceIdentity.CanonicalDisplayName,
                    TargetSubcontractorName = SubcontractorNormalizer.CanonicalSubcontractorName(contract.SubcontractorName)
                };
                db.ManualContractLinks.Add(link);
            }
            else
            {
                link.TargetSubcontractorKey = targetKey;
                link.SourceSubcontractorName = sourceIdentity.CanonicalDisplayName;
                link.TargetSubcontractorName = SubcontractorNormalizer.CanonicalSubcontractorName(contract.SubcontractorName);
            }

            await db.SaveChangesAsync(cancellationToken);
            return new ManualContractLinkResult(true, null, link);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteManualContractLinkAsync(
        string projectCode,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var requestedParent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
            var link = await db.ManualContractLinks
                .FirstOrDefaultAsync(
                    existing => existing.Id == linkId && existing.ProjectCode == requestedParent,
                    cancellationToken);
            if (link is null)
            {
                return false;
            }

            db.ManualContractLinks.Remove(link);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ObjectAssignmentResult> CreateObjectAssignmentAsync(
        string projectCode,
        string? subcontractorName,
        string? sourceObjectNumber,
        string? targetObjectNumber,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(sourceObjectNumber))
            {
                return new ObjectAssignmentResult(false, "Reikalingas pradinis objekto numeris.", null);
            }

            if (string.IsNullOrWhiteSpace(targetObjectNumber))
            {
                return new ObjectAssignmentResult(false, "Reikalingas naujas objekto numeris.", null);
            }

            // Resolve the subcontractor key exactly like read-time matching so the
            // stored key hits the same dictionary slot in ApplyObjectAssignments.
            var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
            var identity = SubcontractorNormalizer.NormalizeSubcontractorIdentity(subcontractorName);
            var subcontractorKey = SubcontractorMatchKey(subcontractorName, aliasMap);
            if (string.IsNullOrWhiteSpace(identity.BaseName) || string.IsNullOrWhiteSpace(subcontractorKey))
            {
                return new ObjectAssignmentResult(false, "Reikalingas subrangovo pavadinimas.", null);
            }

            var parent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
            var source = NormalizeKeyPart(sourceObjectNumber);
            var target = NormalizeKeyPart(targetObjectNumber);
            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                return new ObjectAssignmentResult(false, "Naujas objektas sutampa su esamu.", null);
            }

            var assignment = await db.ManualObjectAssignments
                .FirstOrDefaultAsync(
                    existing => existing.ProjectCode == parent
                        && existing.SourceObjectNumber == source
                        && existing.SubcontractorKey == subcontractorKey,
                    cancellationToken);
            if (assignment is null)
            {
                assignment = new ManualObjectAssignment
                {
                    ProjectCode = parent,
                    SourceObjectNumber = source,
                    SubcontractorKey = subcontractorKey,
                    SubcontractorName = identity.CanonicalDisplayName,
                    TargetObjectNumber = target
                };
                db.ManualObjectAssignments.Add(assignment);
            }
            else
            {
                assignment.TargetObjectNumber = target;
                assignment.SubcontractorName = identity.CanonicalDisplayName;
            }

            await db.SaveChangesAsync(cancellationToken);
            return new ObjectAssignmentResult(true, null, assignment);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteObjectAssignmentAsync(
        string projectCode,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var parent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
            var assignment = await db.ManualObjectAssignments
                .FirstOrDefaultAsync(
                    existing => existing.Id == assignmentId && existing.ProjectCode == parent,
                    cancellationToken);
            if (assignment is null)
            {
                return false;
            }

            db.ManualObjectAssignments.Remove(assignment);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

}
