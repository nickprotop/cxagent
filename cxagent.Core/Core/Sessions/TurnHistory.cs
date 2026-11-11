using System.Text.Json;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Sessions;

/// <summary>One lead model response, as the side panel's sparklines draw it.</summary>
/// <param name="At">When the response ended.</param>
/// <param name="Tokens">The lead's tokens for it, in and out.</param>
/// <param name="WorkerTokens">What the workers spent since the previous point.</param>
/// <param name="Cache">Its share of input served from cache, or null when its provider reported none — a gap, not zero.</param>
/// <param name="Context">The input it sent: the context it carried.</param>
/// <param name="Window">The context window then, or null when unknown — a model switch can change it.</param>
public sealed record TurnPoint(DateTimeOffset At, int Tokens, int WorkerTokens, double? Cache, int Context, int? Window);

/// <summary>
/// A session's per-turn history: a point for each lead model response, measured as what the ledger moved since the last
/// one, appended to a file beside the session's logs and read back when the session is resumed.
///
/// <para>MEASURED AS A DIFFERENCE, so nothing new is counted: the ledger already holds every token, and a point is two
/// readings of it subtracted. <see cref="Start"/> sets where the next difference is taken from, whenever a ledger is
/// wired in — a resumed one is seeded with the session's earlier totals, which are not this turn's.</para>
///
/// <para>A LINE IT CANNOT READ IS SKIPPED: the history is a picture of the session, and a damaged line costs one point,
/// never the session.</para>
/// </summary>
public sealed class TurnHistory
{
    /// <summary>The file's name in a session's log folder.</summary>
    public const string FileName = "turns.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Where it is kept, or null for memory only — compared to tell a re-wire of this session from another.</summary>
    public string? KeptAt => _file;
    private readonly string? _file;
    private readonly List<TurnPoint> _points = [];
    private LedgerReading _last = LedgerReading.Zero;

    /// <param name="file">Where the history is kept, or null to keep it in memory only.</param>
    public TurnHistory(string? file)
    {
        _file = file;
        if (file is null || !File.Exists(file)) return;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    if (JsonSerializer.Deserialize<TurnPoint>(line, Json) is { } point) _points.Add(point);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Carries a resumed session's history into the new session's file, before it is opened: a resume starts a new agent
    /// with logs of its own. Only into a file that does not exist yet — the new session's own points are never replaced.
    /// </summary>
    public static void Carry(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal) || !File.Exists(from) || File.Exists(to)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
        }
        // BEST EFFORT, as keeping it is: a resume without its picture is still the resume.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The points so far, oldest first.</summary>
    public IReadOnlyList<TurnPoint> Points
    {
        get { lock (_points) return [.. _points]; }
    }

    /// <summary>A point was added.</summary>
    public event Action? Changed;

    /// <summary>Takes the next point's difference from <paramref name="reading"/> — a ledger was just wired in.</summary>
    public void Start(LedgerReading reading)
    {
        lock (_points) _last = reading;
    }

    /// <summary>A lead response ended: the point is what the ledger moved since the last one.</summary>
    /// <param name="now">The ledger's reading now.</param>
    /// <param name="window">The context window now, or null when unknown.</param>
    public void Note(LedgerReading now, int? window)
    {
        TurnPoint point;
        lock (_points)
        {
            var input = (int)(now.OwnInput - _last.OwnInput);
            var cached = now.OwnCached - _last.OwnCached;
            var reported = now.OwnCacheReports > _last.OwnCacheReports;
            point = new TurnPoint(DateTimeOffset.UtcNow, input + (int)(now.OwnOutput - _last.OwnOutput),
                (int)(now.WorkerTokens - _last.WorkerTokens), reported && input > 0 ? (double)cached / input : null, input, window);
            _last = now;
            _points.Add(point);
        }
        Append(point);
        Changed?.Invoke();
    }

    private void Append(TurnPoint point)
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllText(_file, JsonSerializer.Serialize(point, Json) + "\n");
        }
        // KEEPING IT IS BEST EFFORT: a full disk loses the picture of a turn, never the turn.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
