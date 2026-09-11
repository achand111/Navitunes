using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Navitunes;

public partial class MainWindow : Window
{
    private const string LibraryPathKey = "library";

    private SubsonicClient? _client;
    private ItunesLibrary? _library;
    private SongIndex? _index;
    private Dictionary<string, NavPlaylist> _existing = new();
    private ImportEngine? _engine;
    private string _xmlPath = "";

    private PlaylistPreview? _playlistPreview;
    private LovedPreview? _lovedPreview;
    private RatingsPreview? _ratingsPreview;

    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
    }

    // ---------- window chrome ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- helpers ----------

    private void Log(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(message + "\n");
            LogBox.ScrollToEnd();
        });
    }

    private void SetProgress(ProgressBar bar, int done, int total)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bar.Value = total > 0 ? done / (double)total : 0;
        });
    }

    private void SetProgress(ProgressBar bar, double value) =>
        Dispatcher.BeginInvoke(() => bar.Value = value);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Dispatcher.BeginInvoke(() =>
        {
            ConnectBtn.IsEnabled = !busy;
            BrowseBtn.IsEnabled = !busy;
            UrlBox.IsEnabled = !busy;
            UserBox.IsEnabled = !busy;
            PassBox.IsEnabled = !busy;
            XmlBox.IsEnabled = !busy;

            var connected = _client != null && !busy;
            PlaylistPreviewBtn.IsEnabled = connected;
            LovedPreviewBtn.IsEnabled = connected;
            RatingsPreviewBtn.IsEnabled = connected;
            RunAllBtn.IsEnabled = connected;
        });
    }

    private void SetStatus(string text, string? colorKey = null)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = text;
            if (colorKey != null)
            {
                StatusDot.Fill = (Brush)FindResource(colorKey);
            }
        });
    }

    private void SetSummary(TextBlock block, string text) =>
        Dispatcher.BeginInvoke(() => block.Text = text);

    // ---------- connect ----------

    private void BrowseXml_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select your exported iTunes Library.xml",
            Filter = "iTunes library (*.xml)|*.xml|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            XmlBox.Text = dialog.FileName;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;
        var xmlPath = XmlBox.Text.Trim();

        if (url.Length == 0 || user.Length == 0 || pass.Length == 0 || xmlPath.Length == 0)
        {
            MessageBox.Show(this, "Fill in the library path, URL, username, and password.",
                "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!File.Exists(xmlPath))
        {
            MessageBox.Show(this, $"Could not find:\n{xmlPath}",
                "File not found", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _xmlPath = xmlPath;

        SetBusy(true);
        SetStatus("Connecting\u2026", "WarnBrush");
        Log($"Testing Navidrome connection\u2026 {url}");

        try
        {
            _client = new SubsonicClient(url, user, pass);
            await _client.PingAsync();
            Log("Connection successful.");

            SetProgress(ConnectProgress, 0.05);
            Log($"Reading iTunes library\u2026 {xmlPath}");

            _library = await Task.Run(() => ItunesLibrary.Load(xmlPath));
            Log($"Found {_library.Tracks.Count} iTunes tracks, {_library.Playlists.Count} playlists.");

            SetProgress(ConnectProgress, 0.2);
            Log("Reading songs from Navidrome\u2026");

            var songs = await _client.GetSongsAsync(Log);
            if (songs.Count == 0)
                throw new InvalidOperationException("Navidrome returned no songs. Check your library is scanned.");

            _index = SongIndex.Build(songs);
            _engine = new ImportEngine(_client, _library, _index, xmlPath);

            SetProgress(ConnectProgress, 0.9);
            var playlists = await _client.GetPlaylistsAsync();
            _existing = playlists.ToDictionary(p => Normalizer.Normalize(p.Name), p => p, StringComparer.Ordinal);

            SetProgress(ConnectProgress, 1);
            SetStatus("Connected", "GoodBrush");
            Log("Ready.");

            SetSummary(LibrarySummary,
                $"{_library.Tracks.Count} iTunes tracks\n" +
                $"{_library.Playlists.Count} iTunes playlists\n" +
                $"{songs.Count} Navidrome songs\n" +
                $"{playlists.Count} existing Navidrome playlists");

            Dispatcher.Invoke(() =>
            {
                PlaylistSummary.Text = "Ready to preview.";
                LovedSummary.Text = "Ready to preview.";
                RatingsSummary.Text = "Ready to preview.";
            });
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            SetStatus("Connection failed", "BadBrush");
            MessageBox.Show(this, ex.Message, "Could not connect",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _client = null;
            _engine = null;
        }
        finally
        {
            SetBusy(false);
            SetProgress(ConnectProgress, 0);
        }
    }

    // ---------- playlists ----------

    private async void PlaylistPreview_Click(object sender, RoutedEventArgs e)
    {
        await RunPreview("Playlists", PlaylistPreviewBtn, () =>
        {
            _playlistPreview = _engine!.PreviewPlaylists();
            var p = _playlistPreview;
            foreach (var pl in p.Prepared.Take(10))
            {
                var action = _existing.ContainsKey(Normalizer.Normalize(pl.Name)) ? "UPDATE" : "CREATE";
                Log($"  [{action}] {pl.Name} ({pl.SongIds.Count}/{pl.OriginalCount} tracks)");
            }
            if (p.Prepared.Count > 10)
                Log($"  \u2026and {p.Prepared.Count - 10} more.");
            return $"Playlists previewed: {p.Prepared.Count} playlists, {p.Matched}/{p.Total} tracks matched";
        });
    }

    private async void PlaylistImport_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmImport("Import playlists",
                "New Navidrome playlists will be created. Existing playlists with the same " +
                "name will have their contents replaced. Nothing else will change.\n\nContinue?",
                _playlistPreview)) return;

        await RunImport(PlaylistImportBtn, PlaylistProgress, () =>
        {
            var result = _engine!.ApplyPlaylistsAsync(_playlistPreview!, _existing, Log,
                (d, t) => SetProgress(PlaylistProgress, d, t)).Result;
            WritePlaylistCsvs();
            var created = _playlistPreview!.Prepared.Count - result.Count;
            return $"Playlists done: created {created} | failed {result.Count}";
        });
    }

    private void WritePlaylistCsvs()
    {
        var p = _playlistPreview!;
        var unmatched = p.Unmatched.Select(x => new Dictionary<string, string>
        {
            ["playlist"] = x.Playlist,
            ["position"] = x.Position.ToString(),
            ["track_id"] = x.TrackId,
            ["name"] = x.Name,
            ["artist"] = x.Artist,
            ["album"] = x.Album,
            ["location"] = x.Location,
        }).ToList();
        _engine!.WriteCsv("itunes_playlist_unmatched.csv",
            new[] { "playlist", "position", "track_id", "name", "artist", "album", "location" }, unmatched);
    }

    // ---------- loved ----------

    private async void LovedPreview_Click(object sender, RoutedEventArgs e)
    {
        await RunPreview("Loved", LovedPreviewBtn, () =>
        {
            _lovedPreview = _engine!.PreviewLoved();
            return $"Loved previewed: {_lovedPreview.Matched.Count} to star | {_lovedPreview.Unmatched.Count} unmatched";
        });
    }

    private async void LovedImport_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmImport("Star loved tracks",
                $"This will mark {_lovedPreview?.Matched.Count ?? 0} matched songs as Favorites in " +
                "Navidrome. It will not change ratings, play counts, or your files.\n\nContinue?",
                _lovedPreview)) return;

        await RunImport(LovedImportBtn, LovedProgress, () =>
        {
            var result = _engine!.ApplyLovedAsync(_lovedPreview!, Log,
                (d, t) => SetProgress(LovedProgress, d, t)).Result;
            WriteLovedCsvs();
            return $"Loved done: starred {_lovedPreview!.Matched.Count - result.Count} | failed {result.Count}";
        });
    }

    private void WriteLovedCsvs()
    {
        var p = _lovedPreview!;
        var unmatched = p.Unmatched.Select(x => new Dictionary<string, string>
        {
            ["track_id"] = x.TrackId.ToString(),
            ["name"] = x.Name,
            ["artist"] = x.Artist,
            ["album_artist"] = x.AlbumArtist,
            ["album"] = x.Album,
            ["location"] = x.Location,
        }).ToList();
        _engine!.WriteCsv("itunes_loved_unmatched.csv",
            new[] { "track_id", "name", "artist", "album_artist", "album", "location" }, unmatched);
    }

    // ---------- ratings ----------

    private async void RatingsPreview_Click(object sender, RoutedEventArgs e)
    {
        await RunPreview("Ratings", RatingsPreviewBtn, () =>
        {
            _ratingsPreview = _engine!.PreviewRatings();
            return $"Ratings previewed: {_ratingsPreview.Matched.Count} to apply | {_ratingsPreview.Unmatched.Count} unmatched";
        });
    }

    private async void RatingsImport_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmImport("Apply ratings",
                $"This will set star ratings for {_ratingsPreview?.Matched.Count ?? 0} matched songs in " +
                "Navidrome, for the account you logged in with. Favorites and play counts are untouched.\n\nContinue?",
                _ratingsPreview)) return;

        await RunImport(RatingsImportBtn, RatingsProgress, () =>
        {
            var result = _engine!.ApplyRatingsAsync(_ratingsPreview!, Log,
                (d, t) => SetProgress(RatingsProgress, d, t)).Result;
            WriteRatingsCsvs();
            return $"Ratings done: applied {_ratingsPreview!.Matched.Count - result.Count} | failed {result.Count}";
        });
    }

    private void WriteRatingsCsvs()
    {
        var p = _ratingsPreview!;
        var unmatched = p.Unmatched.Select(x => new Dictionary<string, string>
        {
            ["track_id"] = x.TrackId.ToString(),
            ["name"] = x.Name,
            ["artist"] = x.Artist,
            ["album_artist"] = x.AlbumArtist,
            ["album"] = x.Album,
            ["raw_rating"] = x.Rating.ToString(),
            ["location"] = x.Location,
        }).ToList();
        _engine!.WriteCsv("itunes_rating_unmatched.csv",
            new[] { "track_id", "name", "artist", "album_artist", "album", "raw_rating", "location" }, unmatched);
    }

    // ---------- run all ----------

    private async void RunAll_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        SetBusy(true);
        try
        {
            await RunPreview("Playlists", PlaylistPreviewBtn,
                () => { _playlistPreview = _engine!.PreviewPlaylists(); return $"Playlists: {_playlistPreview.Prepared.Count}"; });
            await RunPreview("Loved", LovedPreviewBtn,
                () => { _lovedPreview = _engine!.PreviewLoved(); return $"Loved: {_lovedPreview.Matched.Count} to star"; });
            await RunPreview("Ratings", RatingsPreviewBtn,
                () => { _ratingsPreview = _engine!.PreviewRatings(); return $"Ratings: {_ratingsPreview.Matched.Count} to apply"; });
            Log("All three previews done -- use each Import button to apply.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- shared ----------

    private async Task RunPreview(string label, Button btn, Func<string> work)
    {
        if (_engine == null) return;
        Dispatcher.Invoke(() => btn.IsEnabled = false);
        Log($"Matching {label}\u2026");
        try
        {
            var summary = await Task.Run(work);
            Log(summary);
            Dispatcher.Invoke(() =>
            {
                var sb = label switch
                {
                    "Playlists" => PlaylistSummary,
                    "Loved" => LovedSummary,
                    _ => RatingsSummary,
                };
                sb.Text = summary;
                UpdateStats();
            });
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            Dispatcher.Invoke(() => btn.IsEnabled = true);
        }
    }

    private async Task RunImport(Button btn, ProgressBar bar, Func<string> work)
    {
        Dispatcher.Invoke(() =>
        {
            btn.IsEnabled = false;
            bar.Value = 0;
        });
        Log("Importing\u2026");
        try
        {
            var summary = await Task.Run(work);
            Log(summary);
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = summary;
                UpdateStats();
            });
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                btn.IsEnabled = true;
                bar.Value = 1;
            });
        }
    }

    private Task<bool> ConfirmImport(string title, string message, object? preview)
    {
        if (preview == null)
        {
            MessageBox.Show(this, "Run the preview first.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.FromResult(false);
        }
        return Task.FromResult(MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    }

    private void UpdateStats()
    {
        StatsText.Text =
            $"{PlaylistSummary.Text}   |   {LovedSummary.Text}   |   {RatingsSummary.Text}";
    }
}