using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed record ReportSourceContext(Guid SourceId, Guid DefaultClientId);

public enum DmarcIngestOutcome
{
    Inserted,
    Duplicate,
    Rejected,
}

public interface IDmarcReportIngestor
{
    Task<DmarcIngestOutcome> IngestParsedAsync(
        ReportSourceContext source,
        DmarcReportParseResult report,
        CancellationToken ct);
}

public sealed class DmarcReportIngestor(
    DmarcAnalyzerDbContext db,
    IDomainIngestResolver domainResolver) : IDmarcReportIngestor
{
    public async Task<DmarcIngestOutcome> IngestParsedAsync(
        ReportSourceContext source,
        DmarcReportParseResult report,
        CancellationToken ct)
    {
        var policyDomain = report.PolicyDomain.Trim().ToLowerInvariant();
        var reportId = report.ReportId.Trim();
        var organizationName = report.OrganizationName.Trim();

        if (source.SourceId == Guid.Empty
            || source.DefaultClientId == Guid.Empty
            || string.IsNullOrEmpty(policyDomain)
            || string.IsNullOrEmpty(reportId)
            || string.IsNullOrEmpty(organizationName)
            || report.RangeEndUtc < report.RangeBeginUtc
            || report.RecordCount <= 0
            || report.RecordCount != report.Records.Count)
        {
            return DmarcIngestOutcome.Rejected;
        }

        // Domains are shared independently of any one report transaction. The
        // returned client is authoritative when the domain already exists.
        var domain = await domainResolver.ResolveOrCreateAsync(
            source.DefaultClientId, policyDomain, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var reportEntityId = await TryInsertReportAsync(
            domain.DomainId,
            source.SourceId,
            organizationName,
            reportId,
            report,
            ct);

        if (!reportEntityId.HasValue)
        {
            return DmarcIngestOutcome.Duplicate;
        }

        await InsertRecordsAsync(reportEntityId.Value, report, ct);
        await InsertLedgerAsync(
            domain.ClientId,
            source.SourceId,
            policyDomain,
            reportId,
            organizationName,
            report,
            ct);

        await transaction.CommitAsync(ct);
        return DmarcIngestOutcome.Inserted;
    }

    private async Task<Guid?> TryInsertReportAsync(
        Guid domainId,
        Guid sourceId,
        string organizationName,
        string reportId,
        DmarcReportParseResult report,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO dmarc_report
                (""Id"", ""DomainId"", ""MailboxSourceId"", ""OrganizationName"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"", ""RecordCount"", ""IngestedAtUtc"", ""PublishedPolicy"", ""SubdomainPolicy"", ""PublishedPct"", ""DkimAlignment"", ""SpfAlignment"")
            VALUES
                ({id}, {domainId}, {sourceId}, {organizationName}, {reportId}, {report.RangeBeginUtc}, {report.RangeEndUtc}, {report.RecordCount}, {DateTime.UtcNow}, {report.PublishedPolicy}, {report.SubdomainPolicy}, {report.PublishedPct}, {report.DkimAlignment}, {report.SpfAlignment})
            ON CONFLICT (""DomainId"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"") DO NOTHING;
            ", ct);

        return rows > 0 ? id : null;
    }

    private async Task InsertRecordsAsync(
        Guid reportId,
        DmarcReportParseResult report,
        CancellationToken ct)
    {
        foreach (var record in report.Records)
        {
            var recordId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO dmarc_report_record
                    (""Id"", ""DmarcReportId"", ""SourceIp"", ""MessageCount"", ""Disposition"", ""DkimResult"", ""SpfResult"", ""HeaderFrom"", ""EnvelopeFrom"", ""EnvelopeTo"", ""ReportRangeBeginUtc"")
                VALUES
                    ({recordId}, {reportId}, {record.SourceIp}, {record.MessageCount}, {record.Disposition}, {record.DkimResult}, {record.SpfResult}, {record.HeaderFrom}, {record.EnvelopeFrom}, {record.EnvelopeTo}, {report.RangeBeginUtc});
                ", ct);

            foreach (var dkim in record.DkimAuthResults)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dmarc_report_record_dkim_auth_result
                        (""Id"", ""DmarcReportRecordId"", ""Domain"", ""Selector"", ""Result"", ""HumanResult"")
                    VALUES
                        ({Guid.NewGuid()}, {recordId}, {dkim.Domain}, {dkim.Selector}, {dkim.Result}, {dkim.HumanResult});
                    ", ct);
            }

            foreach (var spf in record.SpfAuthResults)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dmarc_report_record_spf_auth_result
                        (""Id"", ""DmarcReportRecordId"", ""Domain"", ""Scope"", ""Result"", ""HumanResult"")
                    VALUES
                        ({Guid.NewGuid()}, {recordId}, {spf.Domain}, {spf.Scope}, {spf.Result}, {spf.HumanResult});
                    ", ct);
            }
        }
    }

    private Task<int> InsertLedgerAsync(
        Guid clientId,
        Guid sourceId,
        string policyDomain,
        string reportId,
        string organizationName,
        DmarcReportParseResult report,
        CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO dmarc_report_ingest
                (""Id"", ""ClientId"", ""MailboxSourceId"", ""PolicyDomain"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"", ""OrganizationName"", ""RecordCount"", ""IngestedAtUtc"")
            VALUES
                ({Guid.NewGuid()}, {clientId}, {sourceId}, {policyDomain}, {reportId}, {report.RangeBeginUtc}, {report.RangeEndUtc}, {organizationName}, {report.RecordCount}, {DateTime.UtcNow})
            ON CONFLICT (""ClientId"", ""PolicyDomain"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"") DO NOTHING;
            ", ct);
}
