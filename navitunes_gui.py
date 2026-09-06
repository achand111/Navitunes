#!/usr/bin/env python3
# Navitunes -- import iTunes playlists, favorites and ratings into Navidrome.
# Stdlib only, needs Python 3.9+ (tkinter ships with Python).

import csv
import hashlib
import json
import plistlib
import queue
import re
import secrets
import threading
import time
import tkinter as tk
import unicodedata
import urllib.parse
import urllib.request
from collections import defaultdict
from pathlib import Path
from tkinter import ttk, filedialog, messagebox, scrolledtext

# settings

API_VERSION = "1.16.1"
CLIENT_NAME = "Navitunes-Importer"
PAGE_SIZE = 500
REQUEST_DELAY = 0.03

# apple star ratings -> 1-5
RATING_MAP = {20: 1, 40: 2, 60: 3, 80: 4, 100: 5}


def normalize(value):
    # makes iTunes and Navidrome strings match more easily
    if value is None:
        return ""
    value = unicodedata.normalize("NFKC", str(value)).casefold()
    value = value.replace("&", "and")
    value = re.sub(r"[\u2018\u2019\u201a\u0060]", "'", value)
    value = re.sub(r"[\u201c\u201d]", '"', value)
    value = re.sub(r"[\u2013\u2014]", "-", value)
    value = re.sub(r"\s+", " ", value).strip()
    return value


# navidrome (subsonic) api

def api_call(base_url, username, password, endpoint, extra=None):
    salt = secrets.token_hex(8)
    token = hashlib.md5((password + salt).encode("utf-8")).hexdigest()

    params = {
        "u": username,
        "t": token,
        "s": salt,
        "v": API_VERSION,
        "c": CLIENT_NAME,
        "f": "json",
    }
    if extra:
        params.update(extra)

    url = (
        base_url.rstrip("/")
        + "/rest/"
        + endpoint
        + "?"
        + urllib.parse.urlencode(params, doseq=True)
    )

    request = urllib.request.Request(url, headers={"User-Agent": CLIENT_NAME})

    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            data = json.loads(response.read().decode("utf-8"))
    except Exception as e:
        raise RuntimeError(f"Connection failed: {e}")

    root = data.get("subsonic-response", {})
    if root.get("status") != "ok":
        error = root.get("error", {})
        raise RuntimeError(
            f"Navidrome API error {error.get('code', '?')}: "
            f"{error.get('message', 'Unknown error')}"
        )
    return root


def fetch_navidrome_albums(base_url, username, password, log):
    log("Getting Navidrome album list...")
    albums = []
    offset = 0
    while True:
        root = api_call(
            base_url, username, password, "getAlbumList2",
            {"type": "alphabeticalByName", "size": PAGE_SIZE, "offset": offset},
        )
        batch = root.get("albumList2", {}).get("album", [])
        if not batch:
            break
        albums.extend(batch)
        if len(batch) < PAGE_SIZE:
            break
        offset += len(batch)
    log(f"Found {len(albums)} albums.")
    return albums


def fetch_navidrome_songs(base_url, username, password, log):
    albums = fetch_navidrome_albums(base_url, username, password, log)
    log("Reading songs from Navidrome (this can take a while on large libraries)...")
    songs = []
    for number, album in enumerate(albums, 1):
        album_id = album.get("id")
        if not album_id:
            continue
        try:
            root = api_call(base_url, username, password, "getAlbum", {"id": album_id})
            songs.extend(root.get("album", {}).get("song", []))
        except Exception as e:
            log(f"  WARNING: could not read '{album.get('name', '?')}': {e}")
        if number % 50 == 0 or number == len(albums):
            log(f"  Read {number}/{len(albums)} albums...")
        time.sleep(REQUEST_DELAY)
    log(f"Found {len(songs)} Navidrome songs.")
    return songs


def build_song_index(songs):
    index = defaultdict(list)
    for song in songs:
        key = (normalize(song.get("artist")), normalize(song.get("album")), normalize(song.get("title")))
        index[key].append(song)
    return index


def match_track(track, song_index):
    # match on artist+album+title first, then album-artist+album+title
    key = (normalize(track.get("artist")), normalize(track.get("album")), normalize(track.get("name")))
    candidates = song_index.get(key)
    if candidates:
        return candidates[0], "artist+album+title"

    album_artist = track.get("album_artist")
    if album_artist:
        key = (normalize(album_artist), normalize(track.get("album")), normalize(track.get("name")))
        candidates = song_index.get(key)
        if candidates:
            return candidates[0], "album-artist+album+title"

    return None, None


# itunes reading

def load_plist(xml_path):
    with xml_path.open("rb") as f:
        return plistlib.load(f)


def extract_tracks(plist):
    tracks = {}
    for track_id, track in plist.get("Tracks", {}).items():
        try:
            track_id = int(track_id)
        except (TypeError, ValueError):
            continue
        tracks[track_id] = {
            "track_id": track_id,
            "name": str(track.get("Name", "")),
            "artist": str(track.get("Artist", "")),
            "album_artist": str(track.get("Album Artist", "")),
            "album": str(track.get("Album", "")),
            "location": str(track.get("Location", "")),
            "raw_rating": track.get("Rating"),
            "loved": track.get("Loved", False) is True,
        }
    return tracks


def build_folder_map(playlists):
    folders = {}
    for playlist in playlists:
        if playlist.get("Folder") is True:
            pid = playlist.get("Playlist Persistent ID")
            name = str(playlist.get("Name", "")).strip()
            if pid and name:
                folders[pid] = {"name": name, "parent": playlist.get("Parent Persistent ID")}

    cache = {}

    def resolve(pid, seen=None):
        if not pid:
            return ""
        if pid in cache:
            return cache[pid]
        seen = seen or set()
        if pid in seen or pid not in folders:
            return ""
        seen.add(pid)
        folder = folders[pid]
        parent = resolve(folder["parent"], seen)
        result = f"{parent} / {folder['name']}" if parent else folder["name"]
        cache[pid] = result
        return result

    return {pid: resolve(pid) for pid in folders}


def is_system_playlist(playlist):
    if playlist.get("Master") is True or playlist.get("Folder") is True:
        return True
    for key in ("Music", "Movies", "TV Shows", "Podcasts", "Audiobooks", "Books"):
        if playlist.get(key) is True:
            return True
    if "Smart Info" in playlist:
        return True
    if playlist.get("Distinguished Kind") in {2, 3, 4, 5, 6, 65, 66, 67}:
        return True
    return False


def extract_playlists(plist):
    raw_playlists = plist.get("Playlists", [])
    folder_map = build_folder_map(raw_playlists)
    result = []
    for playlist in raw_playlists:
        if is_system_playlist(playlist):
            continue
        name = str(playlist.get("Name", "")).strip()
        if not name:
            continue
        items = playlist.get("Playlist Items", [])
        if not isinstance(items, list):
            continue
        folder = folder_map.get(playlist.get("Parent Persistent ID"), "")
        full_name = f"{folder} / {name}" if folder else name
        result.append({"name": full_name, "items": items})
    return result


def extract_loved(tracks):
    return [t for t in tracks.values() if t["loved"]]


def extract_rated(tracks):
    rated = []
    for t in tracks.values():
        raw = t["raw_rating"]
        try:
            raw = int(raw)
        except (TypeError, ValueError):
            continue
        if raw not in RATING_MAP:
            continue
        rated.append({**t, "stars": RATING_MAP[raw]})
    return rated


# playlist helpers

def fetch_existing_playlists(base_url, username, password):
    root = api_call(base_url, username, password, "getPlaylists")
    playlists = root.get("playlists", {}).get("playlist", [])
    return {normalize(p["name"]): p for p in playlists if p.get("name")}


def create_playlist(base_url, username, password, name, song_ids):
    return api_call(base_url, username, password, "createPlaylist", {"name": name, "songId": song_ids})


def update_playlist(base_url, username, password, playlist_id, song_ids):
    return api_call(
        base_url, username, password, "updatePlaylist",
        {"playlistId": playlist_id, "songId": song_ids},
    )


def star_song(base_url, username, password, song_id):
    api_call(base_url, username, password, "star", {"id": song_id})


def set_rating(base_url, username, password, song_id, rating):
    api_call(base_url, username, password, "setRating", {"id": song_id, "rating": rating})


def save_csv(path, rows, fieldnames):
    with path.open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


# gui

class ImporterApp:
    def __init__(self, root):
        self.root = root
        root.title("Navitunes — iTunes to Navidrome importer")
        root.geometry("760x680")
        root.minsize(680, 560)

        self.ui_queue = queue.Queue()

        # set once the connect step succeeds
        self.plist = None
        self.tracks = None
        self.nav_songs = None
        self.nav_index = None
        self.existing_playlists = None

        # set by each card's Preview step
        self.prepared_playlists = None
        self.prepared_loved = None
        self.prepared_ratings = None

        self._build_widgets()
        self.root.after(75, self._poll_queue)

    def _build_widgets(self):
        pad = {"padx": 8, "pady": 4}

        conn = ttk.LabelFrame(self.root, text="Library and Navidrome connection")
        conn.pack(fill="x", padx=10, pady=(10, 6))

        ttk.Label(conn, text="iTunes Library.xml:").grid(row=0, column=0, sticky="w", **pad)
        self.xml_var = tk.StringVar()
        ttk.Entry(conn, textvariable=self.xml_var).grid(row=0, column=1, columnspan=3, sticky="ew", **pad)
        ttk.Button(conn, text="Browse...", command=self.pick_xml).grid(row=0, column=4, **pad)

        ttk.Label(conn, text="Navidrome URL:").grid(row=1, column=0, sticky="w", **pad)
        self.url_var = tk.StringVar(value="http://")
        ttk.Entry(conn, textvariable=self.url_var).grid(row=1, column=1, sticky="ew", **pad)

        ttk.Label(conn, text="Username:").grid(row=1, column=2, sticky="w", **pad)
        self.user_var = tk.StringVar()
        ttk.Entry(conn, textvariable=self.user_var, width=16).grid(row=1, column=3, sticky="ew", **pad)

        ttk.Label(conn, text="Password:").grid(row=2, column=2, sticky="w", **pad)
        self.pass_var = tk.StringVar()
        ttk.Entry(conn, textvariable=self.pass_var, show="*", width=16).grid(row=2, column=3, sticky="ew", **pad)

        self.connect_btn = ttk.Button(conn, text="Connect and load library", command=self.start_connect)
        self.connect_btn.grid(row=2, column=0, sticky="w", **pad)
        self.status_var = tk.StringVar(value="Not connected")
        ttk.Label(conn, textvariable=self.status_var).grid(row=2, column=1, sticky="w", **pad)

        conn.columnconfigure(1, weight=1)
        conn.columnconfigure(3, weight=1)

        cards = ttk.Frame(self.root)
        cards.pack(fill="x", padx=10, pady=6)
        cards.columnconfigure(0, weight=1)
        cards.columnconfigure(1, weight=1)
        cards.columnconfigure(2, weight=1)

        self.playlists_ui = self._build_card(
            cards, 0, "Playlists",
            "Creates or updates matching Navidrome playlists.",
            self.start_preview_playlists, self.start_import_playlists,
        )
        self.loved_ui = self._build_card(
            cards, 1, "Loved tracks",
            "Stars matching songs as Favorites.",
            self.start_preview_loved, self.start_import_loved,
        )
        self.ratings_ui = self._build_card(
            cards, 2, "Ratings",
            "Applies your iTunes star ratings.",
            self.start_preview_ratings, self.start_import_ratings,
        )

        self.run_all_btn = ttk.Button(
            self.root, text="Preview and import all three", command=self.start_run_all, state="disabled"
        )
        self.run_all_btn.pack(padx=10, pady=(0, 6), anchor="w")

        log_frame = ttk.LabelFrame(self.root, text="Log")
        log_frame.pack(fill="both", expand=True, padx=10, pady=(0, 10))
        self.log_box = scrolledtext.ScrolledText(log_frame, height=16, font=("Consolas", 9), state="disabled")
        self.log_box.pack(fill="both", expand=True, padx=6, pady=6)

    def _build_card(self, parent, col, title, description, on_preview, on_import):
        card = ttk.LabelFrame(parent, text=title)
        card.grid(row=0, column=col, sticky="nsew", padx=4)
        ttk.Label(card, text=description, wraplength=180, justify="left").pack(anchor="w", padx=8, pady=(6, 8))
        preview_btn = ttk.Button(card, text="Preview", command=on_preview, state="disabled")
        preview_btn.pack(fill="x", padx=8, pady=2)
        import_btn = ttk.Button(card, text="Import", command=on_import, state="disabled")
        import_btn.pack(fill="x", padx=8, pady=(2, 6))
        status_var = tk.StringVar(value="Waiting to connect")
        ttk.Label(card, textvariable=status_var, wraplength=180, justify="left").pack(anchor="w", padx=8, pady=(0, 8))
        return {"preview_btn": preview_btn, "import_btn": import_btn, "status_var": status_var}

    def log(self, message):
        self.ui_queue.put(lambda: self._append_log(message))

    def _append_log(self, message):
        self.log_box.configure(state="normal")
        self.log_box.insert("end", message + "\n")
        self.log_box.see("end")
        self.log_box.configure(state="disabled")

    def dispatch(self, fn):
        self.ui_queue.put(fn)

    def _poll_queue(self):
        try:
            while True:
                fn = self.ui_queue.get_nowait()
                fn()
        except queue.Empty:
            pass
        self.root.after(75, self._poll_queue)

    def _run_in_thread(self, target):
        threading.Thread(target=target, daemon=True).start()

    def credentials(self):
        return self.url_var.get().strip(), self.user_var.get().strip(), self.pass_var.get()

    def pick_xml(self):
        path = filedialog.askopenfilename(
            title="Select your exported iTunes Library.xml",
            filetypes=[("iTunes library", "*.xml"), ("All files", "*.*")],
        )
        if path:
            self.xml_var.set(path)

    def start_connect(self):
        xml_path_str = self.xml_var.get().strip()
        base_url, username, password = self.credentials()

        if not xml_path_str or not base_url or not username or not password:
            messagebox.showerror("Missing information", "Fill in the library path, URL, username, and password.")
            return
        xml_path = Path(xml_path_str)
        if not xml_path.exists():
            messagebox.showerror("File not found", f"Could not find:\n{xml_path}")
            return

        self.connect_btn.configure(state="disabled")
        self.status_var.set("Connecting...")
        self._run_in_thread(lambda: self._connect_worker(xml_path, base_url, username, password))

    def _connect_worker(self, xml_path, base_url, username, password):
        try:
            self.log("Testing Navidrome connection...")
            api_call(base_url, username, password, "ping")
            self.log("Connection successful.")

            self.log(f"Reading iTunes library: {xml_path}")
            plist = load_plist(xml_path)
            tracks = extract_tracks(plist)
            self.log(f"Found {len(tracks)} iTunes tracks.")

            nav_songs = fetch_navidrome_songs(base_url, username, password, self.log)
            if not nav_songs:
                raise RuntimeError("Navidrome returned no songs. Check your library is scanned.")
            nav_index = build_song_index(nav_songs)

            self.log("Checking existing Navidrome playlists...")
            existing = fetch_existing_playlists(base_url, username, password)
            self.log(f"Found {len(existing)} existing Navidrome playlists.")

        except Exception as e:
            self.log(f"ERROR: {e}")
            self.dispatch(lambda: self._connect_failed(str(e)))
            return

        self.dispatch(lambda: self._connect_succeeded(plist, tracks, nav_songs, nav_index, existing))

    def _connect_failed(self, message):
        self.status_var.set("Connection failed")
        self.connect_btn.configure(state="normal")
        messagebox.showerror("Could not connect", message)

    def _connect_succeeded(self, plist, tracks, nav_songs, nav_index, existing):
        self.plist = plist
        self.tracks = tracks
        self.nav_songs = nav_songs
        self.nav_index = nav_index
        self.existing_playlists = existing

        self.status_var.set(f"Connected -- {len(tracks)} iTunes tracks, {len(nav_songs)} Navidrome songs")
        self.connect_btn.configure(state="normal")
        self.run_all_btn.configure(state="normal")

        for ui in (self.playlists_ui, self.loved_ui, self.ratings_ui):
            ui["preview_btn"].configure(state="normal")
            ui["status_var"].set("Ready to preview")

    def start_preview_playlists(self):
        self._run_in_thread(self._preview_playlists_worker)

    def _preview_playlists_worker(self):
        self.log("Matching playlist tracks...")
        raw_playlists = extract_playlists(self.plist)
        prepared, unmatched, total_entries, total_matched = [], [], 0, 0

        for playlist in raw_playlists:
            song_ids = []
            for position, item in enumerate(playlist["items"], 1):
                total_entries += 1
                try:
                    track_id = int(item.get("Track ID"))
                except (TypeError, ValueError):
                    track_id = None
                track = self.tracks.get(track_id) if track_id is not None else None
                if track is None:
                    unmatched.append({"playlist": playlist["name"], "position": position, "track_id": track_id or "",
                                       "name": "", "artist": "", "album": "", "location": ""})
                    continue
                song, _ = match_track(track, self.nav_index)
                if song is None:
                    unmatched.append({"playlist": playlist["name"], "position": position, "track_id": track["track_id"],
                                       "name": track["name"], "artist": track["artist"], "album": track["album"],
                                       "location": track["location"]})
                    continue
                song_ids.append(song["id"])
                total_matched += 1
            prepared.append({"name": playlist["name"], "song_ids": song_ids, "original_count": len(playlist["items"])})

        self.log(f"Playlists: {len(prepared)} | entries: {total_entries} | matched: {total_matched} | unmatched: {len(unmatched)}")
        for p in prepared[:10]:
            action = "UPDATE" if normalize(p["name"]) in self.existing_playlists else "CREATE"
            self.log(f"  [{action}] {p['name']} ({len(p['song_ids'])}/{p['original_count']} tracks)")
        if len(prepared) > 10:
            self.log(f"  ...and {len(prepared) - 10} more.")

        self.prepared_playlists = {"prepared": prepared, "unmatched": unmatched}
        summary = f"{len(prepared)} playlists ready\n{total_matched}/{total_entries} tracks matched"
        self.dispatch(lambda: self._preview_done(self.playlists_ui, summary))

    def start_import_playlists(self):
        if not self._confirm(
            "Import playlists",
            "New Navidrome playlists will be created. Existing playlists with the "
            "same name will have their contents replaced. Nothing else will change.\n\n"
            "Continue?"
        ):
            return
        self._set_running(self.playlists_ui, "Importing...")
        self._run_in_thread(self._import_playlists_worker)

    def _import_playlists_worker(self):
        base_url, username, password = self.credentials()
        prepared = self.prepared_playlists["prepared"]
        unmatched = self.prepared_playlists["unmatched"]
        xml_path = Path(self.xml_var.get().strip())
        created = updated = failed = 0
        failures = []

        self.log("Importing playlists...")
        for number, playlist in enumerate(prepared, 1):
            name, song_ids = playlist["name"], playlist["song_ids"]
            try:
                existing = self.existing_playlists.get(normalize(name))
                if existing:
                    update_playlist(base_url, username, password, existing["id"], song_ids)
                    updated += 1
                    action = "UPDATED"
                else:
                    create_playlist(base_url, username, password, name, song_ids)
                    created += 1
                    action = "CREATED"
            except Exception as e:
                failed += 1
                action = "FAILED"
                failures.append({"playlist": name, "error": str(e)})
            self.log(f"  [{number}/{len(prepared)}] {action}: {name}")
            time.sleep(REQUEST_DELAY)

        if unmatched:
            path = xml_path.parent / "itunes_playlist_unmatched.csv"
            save_csv(path, unmatched, ["playlist", "position", "track_id", "name", "artist", "album", "location"])
            self.log(f"Unmatched tracks saved to: {path}")
        if failures:
            path = xml_path.parent / "itunes_playlist_failures.csv"
            save_csv(path, failures, ["playlist", "error"])
            self.log(f"Failures saved to: {path}")

        self.log(f"Playlists done. Created {created}, updated {updated}, failed {failed}.")
        summary = f"Created {created}, updated {updated}, failed {failed}"
        self.dispatch(lambda: self._import_done(self.playlists_ui, summary))

    def start_preview_loved(self):
        self._run_in_thread(self._preview_loved_worker)

    def _preview_loved_worker(self):
        self.log("Matching Loved tracks...")
        loved_tracks = extract_loved(self.tracks)
        matched, unmatched = [], []
        for track in loved_tracks:
            song, method = match_track(track, self.nav_index)
            if song is None:
                unmatched.append(track)
            else:
                matched.append((track, song, method))
        self.log(f"iTunes Loved tracks: {len(loved_tracks)} | matched: {len(matched)} | unmatched: {len(unmatched)}")

        self.prepared_loved = {"matched": matched, "unmatched": unmatched}
        summary = f"{len(matched)} songs to star\n{len(unmatched)} unmatched"
        self.dispatch(lambda: self._preview_done(self.loved_ui, summary))

    def start_import_loved(self):
        matched = self.prepared_loved["matched"]
        if not self._confirm(
            "Star loved tracks",
            f"This will mark {len(matched)} matched songs as Favorites in Navidrome. "
            "It will not change ratings, play counts, or your files.\n\nContinue?"
        ):
            return
        self._set_running(self.loved_ui, "Starring...")
        self._run_in_thread(self._import_loved_worker)

    def _import_loved_worker(self):
        base_url, username, password = self.credentials()
        matched = self.prepared_loved["matched"]
        unmatched = self.prepared_loved["unmatched"]
        xml_path = Path(self.xml_var.get().strip())
        success = failed = 0
        failed_rows = []

        self.log("Applying Loved/Favorite status...")
        for number, (track, song, method) in enumerate(matched, 1):
            try:
                star_song(base_url, username, password, song["id"])
                success += 1
            except Exception as e:
                failed += 1
                failed_rows.append({"name": track["name"], "artist": track["artist"], "album": track["album"],
                                     "navidrome_id": song.get("id", ""), "error": str(e)})
            if number % 25 == 0 or number == len(matched):
                self.log(f"  {number}/{len(matched)} processed...")
            time.sleep(REQUEST_DELAY)

        if unmatched:
            path = xml_path.parent / "itunes_loved_unmatched.csv"
            save_csv(path, unmatched, ["track_id", "name", "artist", "album_artist", "album", "location"])
            self.log(f"Unmatched tracks saved to: {path}")
        if failed_rows:
            path = xml_path.parent / "itunes_loved_failures.csv"
            save_csv(path, failed_rows, ["name", "artist", "album", "navidrome_id", "error"])
            self.log(f"Failures saved to: {path}")

        self.log(f"Loved tracks done. Starred {success}, failed {failed}.")
        summary = f"Starred {success}, failed {failed}"
        self.dispatch(lambda: self._import_done(self.loved_ui, summary))

    def start_preview_ratings(self):
        self._run_in_thread(self._preview_ratings_worker)

    def _preview_ratings_worker(self):
        self.log("Matching rated tracks...")
        rated_tracks = extract_rated(self.tracks)
        matched, unmatched = [], []
        for track in rated_tracks:
            song, method = match_track(track, self.nav_index)
            if song is None:
                unmatched.append(track)
            else:
                matched.append((track, song, method))
        self.log(f"iTunes rated tracks: {len(rated_tracks)} | matched: {len(matched)} | unmatched: {len(unmatched)}")

        self.prepared_ratings = {"matched": matched, "unmatched": unmatched}
        summary = f"{len(matched)} ratings to apply\n{len(unmatched)} unmatched"
        self.dispatch(lambda: self._preview_done(self.ratings_ui, summary))

    def start_import_ratings(self):
        matched = self.prepared_ratings["matched"]
        if not self._confirm(
            "Apply ratings",
            f"This will set star ratings for {len(matched)} matched songs in Navidrome, "
            "for the account you logged in with. Favorites and play counts are untouched.\n\nContinue?"
        ):
            return
        self._set_running(self.ratings_ui, "Applying...")
        self._run_in_thread(self._import_ratings_worker)

    def _import_ratings_worker(self):
        base_url, username, password = self.credentials()
        matched = self.prepared_ratings["matched"]
        unmatched = self.prepared_ratings["unmatched"]
        xml_path = Path(self.xml_var.get().strip())
        success = failed = 0
        failed_rows = []

        self.log("Applying ratings...")
        for number, (track, song, method) in enumerate(matched, 1):
            try:
                set_rating(base_url, username, password, song["id"], track["stars"])
                success += 1
            except Exception as e:
                failed += 1
                failed_rows.append({"name": track["name"], "artist": track["artist"], "album": track["album"],
                                     "rating": track["stars"], "navidrome_id": song.get("id", ""), "error": str(e)})
            if number % 25 == 0 or number == len(matched):
                self.log(f"  {number}/{len(matched)} processed...")
            time.sleep(REQUEST_DELAY)

        if unmatched:
            path = xml_path.parent / "itunes_rating_unmatched.csv"
            save_csv(path, unmatched, ["track_id", "name", "artist", "album_artist", "album", "raw_rating", "location"])
            self.log(f"Unmatched tracks saved to: {path}")
        if failed_rows:
            path = xml_path.parent / "itunes_rating_failures.csv"
            save_csv(path, failed_rows, ["name", "artist", "album", "rating", "navidrome_id", "error"])
            self.log(f"Failures saved to: {path}")

        self.log(f"Ratings done. Applied {success}, failed {failed}.")
        summary = f"Applied {success}, failed {failed}"
        self.dispatch(lambda: self._import_done(self.ratings_ui, summary))

    def start_run_all(self):
        if not messagebox.askyesno(
            "Preview and import all three",
            "This will preview and then import playlists, loved tracks, and ratings, "
            "one after another. You'll still be asked to confirm before anything changes "
            "in Navidrome.\n\nContinue?"
        ):
            return
        self.run_all_btn.configure(state="disabled")
        self._run_in_thread(self._run_all_worker)

    def _run_all_worker(self):
        self._preview_playlists_worker()
        self._preview_loved_worker()
        self._preview_ratings_worker()
        self.dispatch(lambda: self.run_all_btn.configure(state="normal"))
        self.dispatch(lambda: messagebox.showinfo(
            "Preview complete",
            "All three previews are done -- check the log, then use each card's "
            "Import button to apply the ones you want."
        ))

    def _preview_done(self, ui, summary):
        ui["status_var"].set(summary)
        ui["preview_btn"].configure(state="normal")
        ui["import_btn"].configure(state="normal")

    def _set_running(self, ui, message):
        ui["preview_btn"].configure(state="disabled")
        ui["import_btn"].configure(state="disabled")
        ui["status_var"].set(message)

    def _import_done(self, ui, summary):
        ui["preview_btn"].configure(state="normal")
        ui["import_btn"].configure(state="disabled")
        ui["status_var"].set(summary)

    def _confirm(self, title, message):
        return messagebox.askyesno(title, message)


def main():
    root = tk.Tk()
    ImporterApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()