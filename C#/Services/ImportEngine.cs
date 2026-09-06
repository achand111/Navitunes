using System.Text;
using System.IO;

namespace Navitunes;

public class ImportEngine
{
    private readonly SubsonicClient _client;
    private readonly ItunesLibrary _library;
    private readonly SongIndex _index;
    private readonly string _xmlDir;

    public ImportEngine(SubsonicClient client, ItunesLibrary library, SongIndex index, string xmlPath)
    {
        _client = client;
        _library = library;
        _index = index;
        _xmlDir = Path.GetDirectoryName(xmlPath) ?? ".";
    }

    public ItunesTrack? ResolveAny(ItunesTrack track) => track;

    public NavSong? Match(ItunesTrack track)
    {
        return _index.ResolveNormalized(track.Artist, track.Album, track.Name, track.AlbumArtist);
    }

    // ---------------- playlists ----------------

    public PlaylistPreview PreviewPlaylists()
    {
        var prepared = new List<PreparedPlaylist>();
        var unmatched = new List<UnmatchedPlaylistItem>();
        var total = 0;
        var matched = 0;

        foreach (var playlist in _library.Playlists)
        {
            var songIds = new List<string>();
            foreach (var item in playlist.Items)
            {
                total++;
                var track = item.TrackId != null && _library.Tracks.TryGetValue(item.TrackId.Value, out var t) ? t : null;
                if (track == null)
                {
                    unmatched.Add(new UnmatchedPlaylistItem
                    {
                        Playlist = playlist.Name,
                        Position = item.Position,
                        TrackId = item.TrackId?.ToString() ?? "",
                        Name = "", Artist = "", Album = "", Location = "",
                    });
                    continue;
                }
                var song = Match(track);
                if (song == null)
                {
                    unmatched.Add(new UnmatchedPlaylistItem
                    {
                        Playlist = playlist.Name,
                        Position = item.Position,
                        TrackId = track.TrackId.ToString(),
                        Name = track.Name, Artist = track.Artist, Album = track.Album, Location = track.Location,
                    });
                    continue;
                }
                songIds.Add(song.Id);
                matched++;
            }
            prepared.Add(new PreparedPlaylist
            {
                Name = playlist.Name,
                SongIds = songIds,
                OriginalCount = playlist.Items.Count,
            });
        }

        return new PlaylistPreview { Prepared = prepared, Unmatched = unmatched, Total = total, Matched = matched };
    }

    public async Task<List<FailedPlaylist>> ApplyPlaylistsAsync(PlaylistPreview preview,
        IReadOnlyDictionary<string, NavPlaylist> existing, Action<string>? log, Action<int, int>? progress)
    {
        var failures = new List<FailedPlaylist>();
        var done = 0;
        foreach (var playlist in preview.Prepared)
        {
            var key = Normalizer.Normalize(playlist.Name);
            try
            {
                if (existing.TryGetValue(key, out var existingPl))
                    await _client.UpdatePlaylistAsync(existingPl.Id, playlist.SongIds);
                else
                    await _client.CreatePlaylistAsync(playlist.Name, playlist.SongIds);
            }
            catch (Exception e)
            {
                failures.Add(new FailedPlaylist { Playlist = playlist.Name, Error = e.Message });
            }
            done++;
            log?.Invoke($"  [{done}/{preview.Prepared.Count}] {playlist.Name}");
            progress?.Invoke(done, preview.Prepared.Count);
            await Task.Delay(30);
        }
        return failures;
    }

    // ---------------- loved ----------------

    public LovedPreview PreviewLoved()
    {
        var matched = new List<MissionMatch>();
        var unmatched = new List<ItunesTrack>();
        foreach (var track in _library.Tracks.Values.Where(t => t.Loved))
        {
            var song = Match(track);
            if (song == null) unmatched.Add(track);
            else matched.Add(new MissionMatch(track, song));
        }
        return new LovedPreview { Matched = matched, Unmatched = unmatched };
    }

    public async Task<List<FailedRow>> ApplyLovedAsync(LovedPreview preview, Action<string>? log,
        Action<int, int>? progress)
    {
        var failures = new List<FailedRow>();
        var done = 0;
        foreach (var m in preview.Matched)
        {
            try
            {
                await _client.StarAsync(m.Song.Id);
            }
            catch (Exception e)
            {
                failures.Add(new FailedRow
                {
                    Name = m.Track.Name, Artist = m.Track.Artist, Album = m.Track.Album,
                    NavidromeId = m.Song.Id, Error = e.Message,
                });
            }
            done++;
            if (done % 25 == 0 || done == preview.Matched.Count)
                log?.Invoke($"  {done}/{preview.Matched.Count} processed...");
            progress?.Invoke(done, preview.Matched.Count);
            await Task.Delay(30);
        }
        return failures;
    }

    // ---------------- ratings ----------------

    public RatingsPreview PreviewRatings()
    {
        var matched = new List<MissionMatch>();
        var unmatched = new List<ItunesTrack>();
        foreach (var track in _library.Tracks.Values.Where(t => t.Stars > 0))
        {
            var song = Match(track);
            if (song == null) unmatched.Add(track);
            else matched.Add(new MissionMatch(track, song));
        }
        return new RatingsPreview { Matched = matched, Unmatched = unmatched };
    }

    public async Task<List<FailedRow>> ApplyRatingsAsync(RatingsPreview preview, Action<string>? log,
        Action<int, int>? progress)
    {
        var failures = new List<FailedRow>();
        var done = 0;
        foreach (var m in preview.Matched)
        {
            try
            {
                await _client.SetRatingAsync(m.Song.Id, m.Track.Stars);
            }
            catch (Exception e)
            {
                failures.Add(new FailedRow
                {
                    Name = m.Track.Name, Artist = m.Track.Artist, Album = m.Track.Album,
                    NavidromeId = m.Song.Id, Error = e.Message,
                });
            }
            done++;
            if (done % 25 == 0 || done == preview.Matched.Count)
                log?.Invoke($"  {done}/{preview.Matched.Count} processed...");
            progress?.Invoke(done, preview.Matched.Count);
            await Task.Delay(30);
        }
        return failures;
    }

    // ---------------- csv ----------------

    public void WriteCsv(string filename, string[] headers, List<Dictionary<string, string>> rows)
    {
        var path = Path.Combine(_xmlDir, filename);
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        sw.WriteLine(string.Join(",", headers.Select(Quote)));
        foreach (var row in rows)
        {
            sw.WriteLine(string.Join(",", headers.Select(h => Quote(row.GetValueOrDefault(h, "")))));
        }
    }

    private static string Quote(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}

public class PlaylistPreview
{
    public List<PreparedPlaylist> Prepared { get; set; } = new();
    public List<UnmatchedPlaylistItem> Unmatched { get; set; } = new();
    public int Total { get; set; }
    public int Matched { get; set; }
}

public class LovedPreview
{
    public List<MissionMatch> Matched { get; set; } = new();
    public List<ItunesTrack> Unmatched { get; set; } = new();
}

public class RatingsPreview
{
    public List<MissionMatch> Matched { get; set; } = new();
    public List<ItunesTrack> Unmatched { get; set; } = new();
}