using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using CASCEdit;
using CASCEdit.IO;
using CASCEdit.Logging;
using CASCEdit.Helpers;
using CASCEdit.Structs;

namespace CASCViewer
{
	/// <summary>A single resolvable file in the CASC storage, keyed by FileDataID.</summary>
	public class GameFile
	{
		public uint Id;
		public MD5Hash CKey;
		public ulong NameHash;
		public string Path;          // from a listfile; null when unknown
		public LocaleFlags Locale;
		public long Size;            // decompressed size (bytes)

		public string DisplayName => string.IsNullOrEmpty(Path) ? ("FileDataID/" + Id) : Path;
		public string Extension => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
	}

	/// <summary>Routes CASCEdit log output to a callback; still throws on hard errors.</summary>
	public class CallbackLogger : ICASCLog
	{
		private readonly Action<string> _sink;
		public CallbackLogger(Action<string> sink) { _sink = sink; }
		private string Fmt(string m, object[] a) => (a == null || a.Length == 0) ? m : string.Format(m, a);
		public void LogInformation(string m, params object[] a) => _sink?.Invoke(Fmt(m, a));
		public void LogDebug(string m, params object[] a) { }
		public void LogWarning(string m, params object[] a) => _sink?.Invoke(Fmt(m, a));
		public void LogError(string m, params object[] a) => _sink?.Invoke(Fmt(m, a));
		public void LogCritical(string m, params object[] a) => _sink?.Invoke(Fmt(m, a));
		public void LogAndThrow(LogType type, string m, params object[] a)
		{
			var s = Fmt(m, a);
			_sink?.Invoke(s);
			throw new CascException(s);
		}
	}

	public class CascException : Exception
	{
		public CascException(string message) : base(message) { }
	}

	/// <summary>
	/// Opens a WoW Classic 3.4.3 client, enumerates its root, maps FileDataIDs to paths via an
	/// optional listfile, and extracts (BLTE-decoded) file contents from the local archives.
	/// </summary>
	public class CascReader : IDisposable
	{
		public string ClientPath { get; private set; }
		public string Version { get; private set; }
		public List<GameFile> Files { get; private set; } = new List<GameFile>();
		public int NamedCount { get; private set; }

		private readonly Dictionary<uint, string> _listfile = new Dictionary<uint, string>();
		private bool _open;

		// CDN fallback for content not present in the local archives (e.g. esES locale data).
		private string[] _cdnBases = Array.Empty<string>();
		private HttpClient _http;
		public bool CdnEnabled { get; set; } = true;

		// EKey -> (archive hash, offset, size) built from the local CDN archive indices (Data/indices).
		private Dictionary<string, (string arch, uint off, uint size)> _cdnIndex;
		private readonly object _cdnIndexLock = new object();

		public bool IsLoaded => _open && Files.Count > 0;

		public void Load(string clientPath, Action<string> log = null)
		{
			if (string.IsNullOrWhiteSpace(clientPath) || !File.Exists(Path.Combine(clientPath, ".build.info")))
				throw new CascException("No se encontró .build.info en:\n" + clientPath +
					"\n\nSelecciona la carpeta raíz del cliente (la que contiene .build.info y Data\\).");

			Close();

			var settings = new CASSettings
			{
				BasePath = clientPath,
				Basic = true,
				Locale = LocaleFlags.enUS,
				Logger = new CallbackLogger(log),
			};

			CASContainer.Open(settings);
			CASContainer.OpenLocalIndices();
			CASContainer.OpenEncoding();
			CASContainer.OpenRoot(settings.Locale);
			try { CASContainer.OpenCdnIndices(true); } catch { } // for CDN range-downloads of archived content
			_cdnIndex = null;
			_open = true;

			ClientPath = clientPath;
			Version = CASContainer.BuildInfo?["Version"] ?? "?";

			// CDN bases from .build.info, for downloading content missing from the local archives.
			try
			{
				string path = CASContainer.BuildInfo["CDN Path"];
				_cdnBases = CASContainer.BuildInfo["CDN Hosts"].Split(' ', StringSplitOptions.RemoveEmptyEntries)
					.Select(h => $"http://{h}/{path}").ToArray();
				_http = _http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
			}
			catch { _cdnBases = Array.Empty<string>(); }

			// Collapse the per-locale root blocks into one entry per FileDataID.
			var dict = new Dictionary<uint, GameFile>(400000);
			foreach (var chunk in CASContainer.RootHandler.Chunks)
			{
				foreach (var e in chunk.Entries)
				{
					if (dict.TryGetValue(e.FileDataId, out var existing))
					{
						existing.Locale |= chunk.LocaleFlags;
						continue;
					}

					var gf = new GameFile
					{
						Id = e.FileDataId,
						CKey = e.CEKey,
						NameHash = e.NameHash,
						Locale = chunk.LocaleFlags,
					};
					if (CASContainer.EncodingHandler.CEKeys.TryGetValue(e.CEKey, out var enc))
						gf.Size = enc.DecompressedSize;

					dict[e.FileDataId] = gf;
				}
			}

			Files = dict.Values.OrderBy(f => f.Id).ToList();
			ApplyListfile();
		}

		/// <summary>Loads a "id;path" (or "id,path") listfile and reapplies paths. Returns matched count.</summary>
		public int LoadListfile(string path)
		{
			_listfile.Clear();
			foreach (var raw in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(raw)) continue;

				int sep = raw.IndexOf(';');
				if (sep < 0) sep = raw.IndexOf(',');
				if (sep <= 0) continue;

				if (!uint.TryParse(raw.AsSpan(0, sep).Trim(), out uint id)) continue;

				var p = raw.Substring(sep + 1).Trim().Replace('\\', '/').TrimStart('/');
				if (p.Length == 0) continue;

				_listfile[id] = p;
			}

			ApplyListfile();
			return NamedCount;
		}

		public bool HasListfile => _listfile.Count > 0;

		private void ApplyListfile()
		{
			int named = 0;
			foreach (var f in Files)
			{
				f.Path = _listfile.TryGetValue(f.Id, out var p) ? p : null;
				if (f.Path != null) named++;
			}
			NamedCount = named;
		}

		/// <summary>Opens a BLTE-decoding stream for a file in the local archives (lazy block decode).</summary>
		public Stream OpenStream(GameFile f)
		{
			if (!_open)
				throw new CascException("No hay ningún cliente cargado.");
			if (!CASContainer.EncodingHandler.CEKeys.TryGetValue(f.CKey, out var enc) || enc.EKeys.Count == 0)
				throw new CascException($"FileDataID {f.Id}: sin entrada en encoding.");

			var ekey = enc.EKeys[0];
			var idx = CASContainer.LocalIndexHandler.GetIndexInfo(ekey);
			if (idx == null)
				throw new CascException($"FileDataID {f.Id}: no está en los archivos locales (requiere descarga de CDN).");

			string archive = Path.Combine(CASContainer.BasePath, "Data", "data", string.Format("data.{0:D3}", idx.Archive));
			return DataHandler.Read(archive, idx);
		}

		/// <summary>Returns the BLTE-decoded bytes of a file from the local archives.</summary>
		public byte[] Extract(GameFile f)
		{
			using (var blte = OpenStream(f))
			using (var ms = new MemoryStream(f.Size > 0 ? (int)Math.Min(f.Size, int.MaxValue) : 0))
			{
				blte.CopyTo(ms);
				return ms.ToArray();
			}
		}

		/// <summary>
		/// Extracts a file by content key, decoding BLTE. Reads the local archives when present,
		/// otherwise downloads the encoded file from the CDN (used for locale data not installed
		/// locally, e.g. esES). <paramref name="fromCdn"/> reports where the bytes came from.
		/// </summary>
		public byte[] ExtractByCKey(MD5Hash ckey, out bool fromCdn)
		{
			fromCdn = false;
			if (!_open)
				throw new CascException("No hay ningún cliente cargado.");
			if (!CASContainer.EncodingHandler.CEKeys.TryGetValue(ckey, out var enc) || enc.EKeys.Count == 0)
				throw new CascException($"CKey {ckey}: sin entrada en encoding.");

			var ekey = enc.EKeys[0];
			var idx = CASContainer.LocalIndexHandler.GetIndexInfo(ekey);
			if (idx != null)
			{
				string archive = Path.Combine(CASContainer.BasePath, "Data", "data", string.Format("data.{0:D3}", idx.Archive));
				using (var blte = DataHandler.Read(archive, idx))
				using (var ms = new MemoryStream())
				{
					blte.CopyTo(ms);
					return ms.ToArray();
				}
			}

			if (!CdnEnabled || _cdnBases.Length == 0)
				throw new CascException($"CKey {ckey}: no está local y la descarga por CDN está deshabilitada.");

			byte[] encoded = DownloadFromCdn(ekey);
			fromCdn = true;
			using (var blte = new BLTEStream(new MemoryStream(encoded)))
			using (var ms = new MemoryStream())
			{
				blte.CopyTo(ms);
				return ms.ToArray();
			}
		}

		private byte[] DownloadFromCdn(MD5Hash ekey)
		{
			string ek = ekey.ToString();
			EnsureCdnIndex();

			// Most content lives inside CDN archives - look up the archive + byte range for this EKey.
			if (_cdnIndex.TryGetValue(ek, out var loc))
			{
				string rel = $"data/{loc.arch.Substring(0, 2)}/{loc.arch.Substring(2, 2)}/{loc.arch}";
				Exception lastA = null;
				foreach (var b in _cdnBases)
				{
					try
					{
						var req = new HttpRequestMessage(HttpMethod.Get, $"{b}/{rel}");
						req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(loc.off, loc.off + loc.size - 1);
						var resp = _http.Send(req);
						resp.EnsureSuccessStatusCode();
						using (var s = resp.Content.ReadAsStream())
						using (var ms = new MemoryStream((int)loc.size))
						{
							s.CopyTo(ms);
							return ms.ToArray();
						}
					}
					catch (Exception ex) { lastA = ex; }
				}
				throw new CascException($"No se pudo descargar {ek} del archivo CDN: {lastA?.Message}");
			}

			// Otherwise try the unarchived (loose) standalone file.
			string looseRel = $"data/{ek.Substring(0, 2)}/{ek.Substring(2, 2)}/{ek}";
			Exception last = null;
			foreach (var b in _cdnBases)
			{
				try { return _http.GetByteArrayAsync($"{b}/{looseRel}").GetAwaiter().GetResult(); }
				catch (Exception ex) { last = ex; }
			}
			throw new CascException($"No se pudo descargar {ek} del CDN (ni archivo ni suelto): {last?.Message}");
		}

		private void EnsureCdnIndex()
		{
			if (_cdnIndex != null) return;
			lock (_cdnIndexLock)
			{
				if (_cdnIndex != null) return;
				var map = new Dictionary<string, (string, uint, uint)>(StringComparer.Ordinal);
				var cdn = CASContainer.CDNIndexHandler;
				if (cdn != null)
				{
					foreach (var archive in cdn.Archives)
					{
						string archName = Path.GetFileNameWithoutExtension(archive.BaseFile);
						foreach (var e in archive.Entries)
							map[e.EKey.ToString()] = (archName, e.Offset, e.Size);
					}
				}
				_cdnIndex = map;
			}
		}

		/// <summary>
		/// For each FileDataID flagged with <paramref name="locale"/>, the most specific block's
		/// content key (fewest locale bits wins) - i.e. that locale's actual variant of the file.
		/// </summary>
		private Dictionary<uint, GameFile> MostSpecific(LocaleFlags locale)
		{
			var best = new Dictionary<uint, (int bits, GameFile gf)>();
			foreach (var chunk in CASContainer.RootHandler.Chunks)
			{
				if (((uint)chunk.LocaleFlags & (uint)locale) == 0) continue;
				int bits = PopCount((uint)chunk.LocaleFlags);
				foreach (var e in chunk.Entries)
				{
					if (best.TryGetValue(e.FileDataId, out var cur) && cur.bits <= bits) continue;
					var gf = new GameFile { Id = e.FileDataId, CKey = e.CEKey, NameHash = e.NameHash, Locale = chunk.LocaleFlags };
					if (CASContainer.EncodingHandler.CEKeys.TryGetValue(e.CEKey, out var en)) gf.Size = en.DecompressedSize;
					gf.Path = _listfile.TryGetValue(e.FileDataId, out var p) ? p : null;
					best[e.FileDataId] = (bits, gf);
				}
			}
			return best.ToDictionary(k => k.Key, v => v.Value.gf);
		}

		/// <summary>All entries that carry the locale flag (includes shared/universal files).</summary>
		public List<GameFile> EnumerateLocale(LocaleFlags locale)
			=> MostSpecific(locale).Values.OrderBy(g => g.Id).ToList();

		/// <summary>
		/// Only the files genuinely localized for <paramref name="locale"/> - i.e. whose content key
		/// differs from the reference locale (enUS) variant, or that have no reference variant.
		/// This is the real "esES content" (Spanish text/lua/audio), not the whole shared game.
		/// </summary>
		public List<GameFile> EnumerateLocaleSpecific(LocaleFlags locale, LocaleFlags reference = LocaleFlags.enUS)
		{
			var loc = MostSpecific(locale);
			var refd = MostSpecific(reference);
			var res = new List<GameFile>();
			foreach (var kv in loc)
				if (!refd.TryGetValue(kv.Key, out var r) || r.CKey != kv.Value.CKey)
					res.Add(kv.Value);
			return res.OrderBy(g => g.Id).ToList();
		}

		public HashSet<uint> LocaleSpecificIds(LocaleFlags locale, LocaleFlags reference = LocaleFlags.enUS)
			=> new HashSet<uint>(EnumerateLocaleSpecific(locale, reference).Select(g => g.Id));

		public bool IsLocal(MD5Hash ckey)
		{
			return CASContainer.EncodingHandler.CEKeys.TryGetValue(ckey, out var enc) && enc.EKeys.Count > 0
				&& CASContainer.LocalIndexHandler.GetIndexInfo(enc.EKeys[0]) != null;
		}

		private static int PopCount(uint x) { int c = 0; while (x != 0) { x &= x - 1; c++; } return c; }

		/// <summary>Best-effort extension from file magic, for files we have no listfile name for.</summary>
		public static string SniffExtension(byte[] d)
		{
			if (d == null || d.Length < 4) return "bin";
			string m = Encoding.ASCII.GetString(d, 0, 4);
			switch (m)
			{
				case "BLP2": return "blp";
				case "MD20":
				case "MD21": return "m2";
				case "SKIN": return "skin";
				case "WDC3":
				case "WDC4":
				case "WDC5":
				case "WDB5":
				case "WDB6": return "db2";
				case "RVXT": return "tex";
				case "OggS": return "ogg";
				case "HSXG": return "bls";
				case "PNG": return "png";
			}
			if (d.Length >= 2 && d[0] == 0x1F && d[1] == 0x8B) return "gz";
			if (m == "RIFF") return "riff";
			return "bin";
		}

		public void Dispose() => Close();

		public void Close()
		{
			if (_open || CASContainer.Settings != null)
			{
				try { CASContainer.Close(); } catch { }
			}
			_open = false;
		}
	}
}
