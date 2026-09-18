namespace DmarcAnalyzer.Api.Workers;

/// <summary>The <c>Worker:*</c> settings. Every property here must have a row in docs/ops/configuration.md — a test enforces it.</summary>
public sealed class WorkerOptions
{
    /// <summary>Seconds between worker loop iterations.</summary>
    public int ScheduleIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Messages fetched per batch. Not a cap on the run: a pass keeps drawing batches
    /// until the mailbox is drained or <see cref="MailboxDrainBudgetMinutes"/> runs
    /// out, checkpointing between them. It bounds how much work a single checkpoint
    /// covers, which is what a crash mid-drain costs in re-fetching.
    /// </summary>
    public int MaxMessagesPerSync { get; set; } = 10;

    /// <summary>
    /// How long one source may keep drawing batches before the pass moves on, so a
    /// large backlog cannot starve the other sources or the periodic passes sharing
    /// this loop. At least one batch always runs regardless.
    /// <para>
    /// Clamped below <see cref="SyncRunTimeoutMinutes"/>: the timeout cancels the run
    /// and records it as <c>partial</c>, whereas the budget is meant to stop it
    /// gracefully first, so it can never be the larger of the two.
    /// </para>
    /// </summary>
    public int MailboxDrainBudgetMinutes { get; set; } = 20;

    /// <summary>
    /// Extra days on top of a client's retention window before report mail is deleted from
    /// the mailbox.
    /// <para>
    /// Deliberately generous. Mailbox deletion is the one thing here that removes data the
    /// app does not own, and the margin is what stops a clock skew, a paused worker or a
    /// mid-incident retention change from destroying mail the database has not re-read yet.
    /// </para>
    /// </summary>
    public int MailboxRetentionGraceDays { get; set; } = 30;

    /// <summary>How often the mailbox retention pass runs. It is measured in months, so daily is plenty.</summary>
    public int MailboxRetentionIntervalHours { get; set; } = 24;

    /// <summary>How many times the scheduled loop attempts a failing sync before giving up until next interval.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>First retry delay between those attempts; doubles per attempt.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>Age at which a run still marked running is presumed dead and closed as failed.</summary>
    public int StaleRunTimeoutMinutes { get; set; } = 30;

    /// <summary>Hard cancellation for one sync run; the drain budget should stop it gracefully first.</summary>
    public int SyncRunTimeoutMinutes { get; set; } = 10;

    /// <summary>Master switch for retention purging.</summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>How often the retention purge runs. Daily is plenty — retention is measured in months.</summary>
    public int RetentionIntervalHours { get; set; } = 24;

    /// <summary>
    /// Refuse to start when another worker already holds the ingestion lock.
    /// <para>
    /// On by default. Two ingestion loops duplicate every sync pass and can send
    /// duplicate alert and digest email — see <see cref="WorkerSingleInstanceLock"/>
    /// for what exactly goes wrong. Turn it off only if you have a reason and know
    /// the consequences.
    /// </para>
    /// </summary>
    public bool EnforceSingleInstance { get; set; } = true;

    /// <summary>Reports deleted per transaction, so a large backlog doesn't hold locks across the table.</summary>
    public int RetentionBatchSize { get; set; } = 500;
}
