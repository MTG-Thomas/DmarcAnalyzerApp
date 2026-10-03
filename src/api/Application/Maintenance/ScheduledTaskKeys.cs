namespace DmarcAnalyzer.Api.Application.Maintenance;

/// <summary>
/// The <c>scheduled_task_state</c> keys for the worker's periodic passes. Free
/// text in the schema, but the worker only ever writes these eight — a new key
/// here is the whole change when a new pass needs a durable cadence.
/// </summary>
public static class ScheduledTaskKeys
{
    public const string Alert = "alert";
    public const string Digest = "digest";
    public const string Retention = "retention";
    public const string DnsRefresh = "dns_refresh";
    public const string MtaSts = "mta_sts";
    public const string SpfDrift = "spf_drift";
    public const string BackupOffload = "backup_offload";
    public const string MailboxRetention = "mailbox_retention";
}
