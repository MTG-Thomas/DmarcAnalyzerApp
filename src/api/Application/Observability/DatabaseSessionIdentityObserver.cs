namespace DmarcAnalyzer.Api.Application.Observability;

/// <summary>One sanitized reused-physical-connection sample per API process.</summary>
public sealed class DatabaseSessionIdentityObserver(ILogger<DatabaseSessionIdentityObserver> logger)
{
    private const int MaximumTrackedConnections = 128;
    private readonly object _gate = new();
    private readonly Dictionary<(string? Host, int Port, int BackendId), (long Generation, bool Observed)> _observed = new();
    private long _nextGeneration;
    private bool _reported;

    /// <summary>Called by Npgsql only when it opens a new physical connection.</summary>
    public void PhysicalConnectionOpened((string? Host, int Port, int BackendId) key)
    {
        lock (_gate)
        {
            if (_reported || string.IsNullOrWhiteSpace(key.Host) || key.BackendId <= 0) return;
            if (_observed.ContainsKey(key) || _observed.Count < MaximumTrackedConnections)
                _observed[key] = (++_nextGeneration, false);
        }
    }

    /// <summary>Captures the physical generation before the identity query.</summary>
    public long? CaptureGeneration((string? Host, int Port, int BackendId) key)
    {
        lock (_gate)
            return !_reported && _observed.TryGetValue(key, out var state) ? state.Generation : null;
    }

    /// <summary>Reports only after an earlier sample on this physical generation.</summary>
    public void Observe((string? Host, int Port, int BackendId) key, long? generation,
        string sessionUser, string currentUser, string database)
    {
        lock (_gate)
        {
            if (_reported || !_observed.TryGetValue(key, out var state) || generation != state.Generation) return;
            _observed[key] = (state.Generation, true);
            if (!state.Observed) return;
            _reported = true;
            _observed.Clear();
        }
        if (!IsSafe(sessionUser) || !IsSafe(currentUser) || !IsSafe(database))
        {
            logger.LogWarning("Runtime database identity sample unavailable: unsafe_identity_result");
            return;
        }
        logger.LogInformation(
            "Runtime database session identity: session_user={SessionUser} current_user={CurrentUser} current_database={Database} physical_connection_previously_observed=true",
            sessionUser, currentUser, database);
    }

    private static bool IsSafe(string value)
        => value.Length is > 0 and <= 63
            && (char.IsAsciiLetter(value[0]) || value[0] == '_')
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}
