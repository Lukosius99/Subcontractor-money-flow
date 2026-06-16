# Test Data Cleanup

Contract import now matches rows by this normalized logical key:

`ProjectCode + SubcontractorName + ObjectName`

Normalization trims values, collapses repeated whitespace and line breaks to one space, and compares case-insensitively. The import updates an existing logical row when it finds one, but it does not delete older test rows automatically.

To inspect duplicate logical contract rows in SQLite:

```sql
SELECT
  upper(trim(ProjectCode)) AS ProjectCodeKey,
  upper(trim(replace(replace(SubcontractorName, char(13), ' '), char(10), ' '))) AS SubcontractorKey,
  upper(trim(replace(replace(ObjectName, char(13), ' '), char(10), ' '))) AS ObjectKey,
  COUNT(*) AS DuplicateCount,
  group_concat(Id, ', ') AS ContractIds
FROM SubcontractorContracts
GROUP BY ProjectCodeKey, SubcontractorKey, ObjectKey
HAVING COUNT(*) > 1;
```

If a test database contains obsolete short-name/full-name duplicates, review the rows first:

```sql
SELECT Id, ProjectCode, SubcontractorName, ObjectName, ContractedAmount, RowKey, CreatedAt, UpdatedAt
FROM SubcontractorContracts
WHERE ProjectCode = 'P1730-01'
ORDER BY SubcontractorName, ObjectName, UpdatedAt DESC;
```

Then delete only the confirmed obsolete test row by `Id`:

```sql
DELETE FROM SubcontractorContracts
WHERE Id = '<obsolete-test-row-id>';
```
