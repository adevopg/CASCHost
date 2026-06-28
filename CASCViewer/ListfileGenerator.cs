using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CASCEdit.Helpers;

namespace CASCViewer
{
	/// <summary>
	/// Reconstructs a FileDataID-&gt;path listfile straight from a client by harvesting candidate
	/// path strings (from the client binaries and from the contents of the game files themselves),
	/// hashing them with the WoW Jenkins96 and matching against the root's name hashes.
	/// </summary>
	public class ListfileGenerator
	{
		private readonly CascReader _reader;

		// nameHash -> FileDataID (only entries that carry a non-zero name hash can ever be recovered)
		private readonly Dictionary<ulong, uint> _hashToId = new Dictionary<ulong, uint>();
		private readonly ConcurrentDictionary<uint, string> _found = new ConcurrentDictionary<uint, string>();

		public int NameableCount { get; private set; }   // root entries with a non-zero name hash
		public int FoundCount => _found.Count;

		// Path-like token: needs at least one separator and a known-ish extension.
		private static readonly Regex PathRx = new Regex(
			@"[A-Za-z0-9_\-][A-Za-z0-9_\- ]*(?:[\\/][A-Za-z0-9_\- ]+)+\.[A-Za-z0-9]{2,5}",
			RegexOptions.Compiled);

		private static readonly HashSet<string> Exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"blp","m2","mdx","wmo","adt","wdt","wdl","dbc","db2","lua","xml","toc","skin","anim",
			"bone","phys","skel","tex","bls","wav","mp3","ogg","ttf","otf","sig","wfx","def","m3",
			"html","css","tga","wlw","wlq","lit","wlm","zmp","xsd","gif","jpg","jpeg","png","wdb",
			"trs","pd4","mp4","avi","ini","txt","cfg","sbt","wtf","mpq"
		};

		// File magics that never contain useful path strings - skip after a cheap header peek.
		private static bool IsBlob(byte[] h)
		{
			if (h.Length >= 4)
			{
				string m = Encoding.ASCII.GetString(h, 0, 4);
				if (m == "BLP2" || m == "OggS" || m == "RIFF" || m == "DDS " || m == "OTTO" || m == "GIF8") return true;
				if (h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return true;
			}
			if (h.Length >= 3 && h[0] == 'I' && h[1] == 'D' && h[2] == '3') return true;            // mp3
			if (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0) return true;                // mp3 frame
			return false;
		}

		public ListfileGenerator(CascReader reader)
		{
			_reader = reader;
			foreach (var f in reader.Files)
			{
				if (f.NameHash == 0) continue;
				_hashToId[f.NameHash] = f.Id;            // last wins; collisions are vanishingly rare
				// seed any names we already have (e.g. from a previously loaded listfile)
				if (!string.IsNullOrEmpty(f.Path)) _found.TryAdd(f.Id, f.Path);
			}
			NameableCount = _hashToId.Count;
		}

		/// <summary>Hashes a candidate string and records it if it matches a root name hash.</summary>
		private void Offer(string candidate, HashSet<string> seen)
		{
			candidate = candidate.Replace('/', '\\').Trim().Trim('\\');
			if (candidate.Length < 3) return;

			int dot = candidate.LastIndexOf('.');
			if (dot < 0 || dot == candidate.Length - 1) return;
			if (!Exts.Contains(candidate.Substring(dot + 1))) return;

			string norm = candidate.ToUpperInvariant();
			if (!seen.Add(norm)) return;                 // already hashed within this file

			ulong h = new Jenkins96().ComputeHash(norm, normalized: false);
			if (_hashToId.TryGetValue(h, out uint id))
				_found[id] = candidate.Replace('\\', '/');
		}

		// Per-file dedup keeps memory bounded (binary data interpreted as text yields huge junk).
		private void ScanText(string text, HashSet<string> seen)
		{
			foreach (Match m in PathRx.Matches(text))
				Offer(m.Value, seen);
		}

		/// <summary>Pass 1 - the client executables/DLLs on disk (ASCII + UTF-16).</summary>
		public void ScanBinaries(string clientDir, Action<string> log)
		{
			IEnumerable<string> bins;
			try
			{
				bins = Directory.EnumerateFiles(clientDir, "*.exe", SearchOption.AllDirectories)
					.Concat(Directory.EnumerateFiles(clientDir, "*.dll", SearchOption.AllDirectories))
					.Where(p => new FileInfo(p).Length < 200L * 1024 * 1024);
			}
			catch { return; }

			foreach (var bin in bins)
			{
				try
				{
					byte[] data = File.ReadAllBytes(bin);
					var seen = new HashSet<string>(StringComparer.Ordinal);
					ScanText(Latin1(data), seen);                 // ASCII strings
					ScanText(Encoding.Unicode.GetString(data), seen); // wide strings
					log?.Invoke($"bin {Path.GetFileName(bin)}: {_found.Count} encontrados");
				}
				catch { }
			}
		}

		/// <summary>Pass 2 - the contents of the game files (ADT/WMO/M2/DB2/interface contain path lists).</summary>
		public void ScanGameData(long maxBytes, Action<int, int> progress, CancellationToken ct)
		{
			var files = _reader.Files;
			int total = files.Count;
			int done = 0;

			var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) };
			try
			{
				Parallel.ForEach(files, po, f =>
				{
					try
					{
						if (f.Size > 0 && maxBytes > 0 && f.Size > maxBytes) return;

						using (var st = _reader.OpenStream(f))
						{
							byte[] head = new byte[16];
							int n = ReadFull(st, head, 0, head.Length);
							if (n >= 3 && IsBlob(head)) return;

							using (var ms = new MemoryStream())
							{
								ms.Write(head, 0, n);
								st.CopyTo(ms);
								var seen = new HashSet<string>(StringComparer.Ordinal);
								ScanText(Latin1(ms.GetBuffer().AsSpan(0, (int)ms.Length)), seen);
							}
						}
					}
					catch { /* CDN-only or unreadable - ignore */ }
					finally
					{
						int d = Interlocked.Increment(ref done);
						if ((d & 0x3FF) == 0) progress?.Invoke(d, total);
					}
				});
			}
			catch (OperationCanceledException) { }
			progress?.Invoke(done, total);
		}

		/// <summary>
		/// Imports a community "id;path" listfile, but trusts only the PATH: each path is hashed and
		/// matched against this client's root name hashes, so the resulting FileDataID is the client's
		/// own and entries that don't actually exist here are dropped. Returns total names now known.
		/// </summary>
		public int ImportCommunityListfile(string csvPath, Action<long, long> progress, CancellationToken ct)
		{
			long size = new FileInfo(csvPath).Length;
			long read = 0;
			long tick = 0;

			foreach (var line in File.ReadLines(csvPath))
			{
				ct.ThrowIfCancellationRequested();
				read += line.Length + 1;

				if (line.Length == 0) continue;
				int sep = line.IndexOf(';');
				if (sep < 0) sep = line.IndexOf(',');
				if (sep <= 0 || sep >= line.Length - 1) continue;

				string path = line.Substring(sep + 1).Trim();
				if (path.Length == 0) continue;

				string norm = path.Replace('/', '\\').ToUpperInvariant();
				ulong h = new Jenkins96().ComputeHash(norm, normalized: false);
				if (_hashToId.TryGetValue(h, out uint id))
					_found[id] = path.Replace('\\', '/');

				if ((++tick & 0x3FFFF) == 0) progress?.Invoke(read, size);
			}
			progress?.Invoke(size, size);
			return _found.Count;
		}

		public int Save(string outPath)
		{
			var lines = _found.OrderBy(kv => kv.Key).Select(kv => kv.Key + ";" + kv.Value);
			File.WriteAllLines(outPath, lines, new UTF8Encoding(false));
			return _found.Count;
		}

		private static int ReadFull(Stream s, byte[] buf, int off, int count)
		{
			int total = 0, r;
			while (total < count && (r = s.Read(buf, off + total, count - total)) > 0) total += r;
			return total;
		}

		private static string Latin1(ReadOnlySpan<byte> data) => Encoding.Latin1.GetString(data);
		private static string Latin1(byte[] data) => Encoding.Latin1.GetString(data);
	}
}
