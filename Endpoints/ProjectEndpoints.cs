using PADS.MoneyFlow.Api.Services;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects", async (
            MonthlyFlowStore store,
            CancellationToken cancellationToken) =>
        {
            var projects = await store.GetProjectSummariesAsync(cancellationToken);
            static bool IsMainActiveProject(ProjectSummary project) =>
                project.IsActiveContractedProject
                && project.IsInLatestContractedImport
                && (string.IsNullOrWhiteSpace(project.ProjectStatus)
                    || string.Equals(project.ProjectStatus, "Statybos", StringComparison.OrdinalIgnoreCase));

            var activeProjects = projects
                .Where(IsMainActiveProject)
                .ToList();
            var inactiveProjects = projects
                .Where(project => !IsMainActiveProject(project))
                .ToList();
            var projectCodes = activeProjects.Select(project => project.ProjectCode).ToList();

            static object ProjectPayload(ProjectSummary project) => new
            {
                projectCode = project.ProjectCode,
                projectName = project.ProjectName,
                responsible = project.Responsible,
                engineer = project.Engineer,
                projectStatus = project.ProjectStatus,
                isActiveContractedProject = project.IsActiveContractedProject,
                isInLatestContractedImport = project.IsInLatestContractedImport,
                lastSeenContractImportAt = project.LastSeenContractImportAt,
                becameInactiveAt = project.BecameInactiveAt,
                objectCount = project.ObjectCount,
                amountWithoutVat = project.AmountWithoutVat,
                contractedAmount = project.ContractedAmount,
                projectValue = project.ProjectValue,
                clientInvoiced = project.ClientInvoiced,
                remaining = project.Remaining,
                rowCount = project.RowCount,
                subcontractorCount = project.SubcontractorCount,
                warningsCount = project.WarningsCount,
                status = project.Status,
                objects = project.Objects.Select(projectObject => new
                {
                    objectNumber = projectObject.ObjectNumber,
                    objectCode = projectObject.ObjectCode,
                    objectPrintCode = projectObject.ObjectPrintCode,
                    departmentCode = projectObject.DepartmentCode,
                    contractedAmount = projectObject.ContractedAmount,
                    amountWithoutVat = projectObject.AmountWithoutVat,
                    projectValue = projectObject.ProjectValue,
                    clientInvoiced = projectObject.ClientInvoiced,
                    remaining = projectObject.Remaining,
                    subcontractorCount = projectObject.SubcontractorCount,
                    warningsCount = projectObject.WarningsCount,
                    status = projectObject.Status,
                    objectName = projectObject.ObjectName,
                    responsibles = projectObject.Responsibles,
                    engineers = projectObject.Engineers
                })
            };

            return Results.Ok(new
            {
                projectCodes,
                inactiveProjectCodes = inactiveProjects.Select(project => project.ProjectCode).ToList(),
                allProjectCodes = projects.Select(project => project.ProjectCode).ToList(),
                projects = activeProjects.Select(ProjectPayload),
                activeProjects = activeProjects.Select(ProjectPayload),
                inactiveProjects = inactiveProjects.Select(ProjectPayload)
            });
        });

        app.MapGet("/api/projects/{projectCode}/monthly-flow", async (
            string projectCode,
            HttpResponse response,
            HttpRequest request,
            MonthlyFlowStore store,
            CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
            response.Headers.Expires = "0";

            var selectedObjectNumber = request.Query["objectNumber"].FirstOrDefault();
            var summaries = await store.GetProjectSummariesAsync(cancellationToken);
            var parentProjectCode = MonthlyFlowStore.ParseProjectObjectCode(projectCode).ParentProjectCode;
            var parentSummary = summaries.FirstOrDefault(summary =>
                string.Equals(summary.ProjectCode, parentProjectCode, StringComparison.OrdinalIgnoreCase));
            var rows = await store.GetRowsForProjectScopeAsync(projectCode, selectedObjectNumber, cancellationToken);
            var detail = await store.GetProjectDetailAsync(projectCode, selectedObjectNumber, cancellationToken);
            var subcontractorRows = rows
                .Where(row => !MonthlyFlowStore.IsClientMonthlyValueRow(row))
                .ToList();

            var groupedRows = rows
                .GroupBy(row => new { row.Year, row.Month })
                .OrderBy(group => group.Key.Year)
                .ThenBy(group => group.Key.Month)
                .Select(group => new
                {
                    year = group.Key.Year,
                    month = group.Key.Month,
                    rows = group
                        .OrderBy(row => row.SourceSheet)
                        .ThenBy(row => row.SourceRow)
                });

            var totalsByMonth = subcontractorRows
                .GroupBy(row => new { row.Year, row.Month })
                .OrderBy(group => group.Key.Year)
                .ThenBy(group => group.Key.Month)
                .Select(group => new
                {
                    year = group.Key.Year,
                    month = group.Key.Month,
                    amountWithoutVat = group.Sum(row => row.AmountWithoutVat)
                });

            var totalsBySubcontractor = subcontractorRows
                .GroupBy(row => string.IsNullOrWhiteSpace(row.SubcontractorName)
                    ? "(Be subrangovo)"
                    : row.SubcontractorName)
                .OrderByDescending(group => group.Sum(row => row.AmountWithoutVat))
                .Select(group => new
                {
                    subcontractorName = group.Key,
                    amountWithoutVat = group.Sum(row => row.AmountWithoutVat)
                });

            return Results.Ok(new
            {
                projectCode,
                parentProjectCode,
                selectedObjectNumber,
                isAllObjects = string.IsNullOrWhiteSpace(selectedObjectNumber)
                    && string.Equals(projectCode, parentProjectCode, StringComparison.OrdinalIgnoreCase),
                objects = parentSummary?.Objects.Select(projectObject => new
                {
                    objectNumber = projectObject.ObjectNumber,
                    objectCode = projectObject.ObjectCode,
                    objectPrintCode = projectObject.ObjectPrintCode,
                    departmentCode = projectObject.DepartmentCode,
                    contractedAmount = projectObject.ContractedAmount,
                    amountWithoutVat = projectObject.AmountWithoutVat,
                    projectValue = projectObject.ProjectValue,
                    clientInvoiced = projectObject.ClientInvoiced,
                    remaining = projectObject.Remaining,
                    subcontractorCount = projectObject.SubcontractorCount,
                    warningsCount = projectObject.WarningsCount,
                    status = projectObject.Status
                }) ?? [],
                projectName = detail.ProjectName,
                responsible = detail.Responsible,
                engineer = detail.Engineer,
                contractCount = detail.ContractCount,
                totals = new
                {
                    amountWithoutVat = subcontractorRows.Sum(row => row.AmountWithoutVat),
                    byMonth = totalsByMonth,
                    bySubcontractor = totalsBySubcontractor
                },
                groups = groupedRows,
                contractRows = detail.ContractRows,
                projectObjectValues = detail.ProjectObjectValues,
                smdCustomerRows = detail.SmdCustomerRows,
                objectAssignments = detail.ObjectAssignments
            });
        });
    }
}
