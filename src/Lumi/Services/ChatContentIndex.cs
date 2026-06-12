using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using StrataSearch;

namespace Lumi.Services;

/// <summary>
/// Maintains a bounded, normalized content blob for every chat so that full-text chat
/// search covers the entire history cheaply — not just the handful of most-recent chats.
///
/// Memory is bounded by capping the indexed text per chat. The index is warmed in the
/// background, can be persisted to disk so restarts don't re-read the whole history, and
/// is kept fresh for the open chat (which always carries its messages in memory).
/// </summary>
public sealed class ChatContentIndex
{
    private const int MaxIndexedChars = 12_000;
    private const string PersistMagic = "LCI2";
    private const int MaxPersistedEntries = 5_000_000;

    private readonly Func<Chat, ChatSearchSnapshot> _snapshotProvider;
    private readonly Action<Guid>? _releaseSnapshot;
    private readonly Func<Guid, DateTimeOffset?>? _fileTimestampProvider;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    public ChatContentIndex(
        Func<Chat, ChatSearchSnapshot> snapshotProvider,
        Action<Guid>? releaseSnapshot = null,
        Func<Guid, DateTimeOffset?>? fileTimestampProvider = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _releaseSnapshot = releaseSnapshot;
        _fileTimestampProvider = fileTimestampProvider;
    }

    /// <summary>Number of chats currently held in the index.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    /// <summary>True when a content entry is already cached for the chat.</summary>
    public bool IsCached(Guid chatId)
    {
        lock (_sync)
            return _entries.ContainsKey(chatId);
    }

    /// <summary>
    /// Returns the content entry for a chat, building it from the snapshot provider when allowed.
    /// Loaded chats (messages already in memory) are always refreshed cheaply; cold chats are
    /// only read from the provider when <paramref name="allowBuild"/> is true.
    /// </summary>
    public Entry? GetEntry(Chat chat, bool allowBuild)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var isLoaded = chat.Messages.Count > 0;
        if (isLoaded)
            return BuildEntry(chat, releaseAfter: false);

        lock (_sync)
        {
            if (_entries.TryGetValue(chat.Id, out var cached))
                return cached;
        }

        if (!allowBuild)
            return null;

        return BuildEntry(chat, releaseAfter: true);
    }

    /// <summary>Removes a chat's entry, forcing a rebuild on next access/warm (e.g., after a save).</summary>
    public void Invalidate(Guid chatId)
    {
        lock (_sync)
            _entries.Remove(chatId);
    }

    /// <summary>Removes a chat's entry entirely (e.g., after deletion).</summary>
    public void Remove(Guid chatId) => Invalidate(chatId);

    /// <summary>
    /// Builds entries for any cold chats that are not yet cached, throttled to avoid hammering
    /// the disk. Cached entries restored from a persisted index are revalidated against the
    /// chat file's current timestamp and rebuilt if stale (e.g., edited by another instance or
    /// after an ungraceful shutdown). Intended to run on a background thread after startup.
    /// </summary>
    public async Task WarmAsync(IReadOnlyList<Chat> chats, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chats);

        // Warm most-recent chats first so the freshest history becomes searchable soonest.
        var ordered = chats
            .Where(static chat => chat.Messages.Count == 0)
            .OrderByDescending(static chat => chat.UpdatedAt)
            .ToArray();

        var processed = 0;
        foreach (var chat in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsCached(chat.Id) || IsStale(chat.Id))
                BuildEntry(chat, releaseAfter: true);

            if (++processed % 32 == 0)
                await Task.Delay(12, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Drops entries for chats that no longer exist so the index doesn't leak deleted chats.</summary>
    public void Prune(IEnumerable<Guid> liveChatIds)
    {
        var live = liveChatIds as HashSet<Guid> ?? [.. liveChatIds];
        lock (_sync)
        {
            var stale = _entries.Keys.Where(id => !live.Contains(id)).ToArray();
            foreach (var id in stale)
                _entries.Remove(id);
        }
    }

    /// <summary>
    /// True when a cached entry's source chat file has a different last-write time than when the
    /// entry was built (e.g. edited by another Lumi instance or after an ungraceful shutdown).
    /// Always false when no timestamp provider is configured, preserving cache-only behavior.
    /// </summary>
    private bool IsStale(Guid chatId)
    {
        if (_fileTimestampProvider is null)
            return false;

        long cachedTicks;
        lock (_sync)
        {
            if (!_entries.TryGetValue(chatId, out var entry))
                return true;
            cachedTicks = entry.SourceTicks;
        }

        var currentTicks = _fileTimestampProvider(chatId)?.UtcTicks ?? 0L;
        return currentTicks != cachedTicks;
    }

    private Entry BuildEntry(Chat chat, bool releaseAfter)
    {
        // Capture the source timestamp BEFORE reading content so that any change racing the read
        // leaves the entry tagged with an older time, forcing a rebuild on the next warm.
        var sourceTicks = _fileTimestampProvider?.Invoke(chat.Id)?.UtcTicks ?? 0L;
        var snapshot = _snapshotProvider(chat);
        var entry = CreateEntry(snapshot, sourceTicks);

        lock (_sync)
            _entries[chat.Id] = entry;

        if (releaseAfter)
            _releaseSnapshot?.Invoke(chat.Id);

        return entry;
    }

    private static Entry CreateEntry(ChatSearchSnapshot snapshot, long sourceTicks)
    {
        var raw = ConcatenateCapped(snapshot.Messages, MaxIndexedChars);
        var prepared = SearchText.Create(raw);
        return new Entry(snapshot.Version, prepared.Normalized, prepared.Compact, sourceTicks);
    }

    private static string ConcatenateCapped(IReadOnlyList<ChatSearchMessage> messages, int maxChars)
    {
        if (messages.Count == 0)
            return "";

        var builder = new StringBuilder(Math.Min(maxChars, 4096));
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
                continue;

            if (builder.Length > 0)
                builder.Append('\n');

            var remaining = maxChars - builder.Length;
            if (remaining <= 0)
                break;

            if (message.Text.Length <= remaining)
                builder.Append(message.Text);
            else
                builder.Append(message.Text, 0, remaining);
        }

        return builder.ToString();
    }

    /// <summary>
    /// A single chat's bounded, normalized content. <see cref="Normalized"/> keeps token
    /// boundaries (single-spaced); <see cref="Compact"/> removes them for cross-separator matching.
    /// </summary>
    public sealed class Entry
    {
        public Entry(string version, string normalized, string compact, long sourceTicks = 0)
        {
            Version = version;
            Normalized = normalized;
            Compact = compact;
            SourceTicks = sourceTicks;
        }

        public string Version { get; }
        public string Normalized { get; }
        public string Compact { get; }

        /// <summary>UTC ticks of the source chat file's last-write time when this entry was built (0 if unknown).</summary>
        public long SourceTicks { get; }
        public bool IsEmpty => Normalized.Length == 0;

        /// <summary>Cheap gate: true when any query term appears in the content (substring or compact).</summary>
        public bool MayMatch(SearchQuery query)
        {
            if (IsEmpty || query.IsEmpty)
                return false;

            foreach (var term in query.Terms)
            {
                if (term.Normalized.Length > 0 && Normalized.Contains(term.Normalized, StringComparison.Ordinal))
                    return true;
                if (term.Compact.Length > 0 && Compact.Contains(term.Compact, StringComparison.Ordinal))
                    return true;
            }

            // Single-token queries have one term equal to the whole text; the loop above covers them.
            return false;
        }

        /// <summary>Builds a content search field for full scoring (only done for candidates).</summary>
        public PreparedSearchField ToContentField(double weight)
            => new(Normalized, weight, SearchFieldKind.Content);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>Serializes the index to disk so a restart can skip re-reading the full history.</summary>
    public void Save(string path)
    {
        KeyValuePair<Guid, Entry>[] snapshot;
        lock (_sync)
            snapshot = [.. _entries];

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A unique temp name keeps concurrent saves (e.g. background warm completion vs. shutdown)
        // from colliding on a shared ".tmp" file and losing a write.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PersistMagic);
                writer.Write(snapshot.Length);
                foreach (var (id, entry) in snapshot)
                {
                    writer.Write(id.ToByteArray());
                    writer.Write(entry.Version);
                    writer.Write(entry.SourceTicks);
                    writer.Write(entry.Normalized);
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of the temp file.
                }
            }
        }
    }

    /// <summary>Loads a previously persisted index. Returns the number of entries loaded.</summary>
    public int Load(string path)
    {
        if (!File.Exists(path))
            return 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadString() != PersistMagic)
                return 0;

            var count = reader.ReadInt32();
            if (count < 0 || count > MaxPersistedEntries)
                return 0;

            var loaded = new Dictionary<Guid, Entry>(Math.Min(count, 4096));
            for (var i = 0; i < count; i++)
            {
                var id = new Guid(reader.ReadBytes(16));
                var version = reader.ReadString();
                var sourceTicks = reader.ReadInt64();
                var normalized = reader.ReadString();
                loaded[id] = new Entry(version, normalized, StripSpaces(normalized), sourceTicks);
            }

            lock (_sync)
            {
                foreach (var (id, entry) in loaded)
                    _entries[id] = entry;
            }

            return loaded.Count;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException
                                     or ArgumentException or OverflowException)
        {
            // A corrupt, truncated, or partial index just means we re-warm from scratch.
            return 0;
        }
    }

    private static string StripSpaces(string normalized)
    {
        if (normalized.IndexOf(' ') < 0)
            return normalized;

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character != ' ')
                builder.Append(character);
        }

        return builder.ToString();
    }
}
