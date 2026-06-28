using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CASCEdit.Helpers;
using CASCEdit.Logging;
using CASCEdit.Structs;

namespace CASCHostGui
{
	/// <summary>
	/// File-based replacement for CASCHost's MySQL cache. Remembers the FileDataID assigned to each
	/// custom path so rebuilds keep stable IDs - no database required.
	/// </summary>
	public class JsonCache : ICache
	{
		private readonly string _path;
		private Dictionary<string, CacheEntry> _entries = new Dictionary<string, CacheEntry>();

		public string Version { get; private set; }
		public HashSet<string> ToPurge { get; private set; } = new HashSet<string>();

		public JsonCache(string path, string version) { _path = path; Version = version; Load(); }

		public IReadOnlyCollection<CacheEntry> Entries => _entries.Values.ToList();
		public uint MaxId => _entries.Count == 0 ? 0 : _entries.Values.Max(x => x.FileDataId);
		public bool HasFiles => _entries.Count > 0;
		public bool HasId(uint fileid) => _entries.Values.Any(x => x.FileDataId == fileid);

		public void AddOrUpdate(CacheEntry item)
		{
			if (!string.IsNullOrEmpty(item.Path)) _entries[item.Path] = item;
		}

		public void Remove(string file) => _entries.Remove(file);
		public void Clean() { }

		public void Load()
		{
			_entries = new Dictionary<string, CacheEntry>();
			if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
			try
			{
				var dtos = JsonSerializer.Deserialize<List<Dto>>(File.ReadAllText(_path));
				foreach (var dd in dtos ?? new List<Dto>())
				{
					if (string.IsNullOrEmpty(dd.Path)) continue;
					_entries[dd.Path] = new CacheEntry
					{
						Path = dd.Path,
						FileDataId = dd.FileDataId,
						NameHash = dd.NameHash,
						CEKey = new MD5Hash(Hex(dd.CEKey)),
						EKey = new MD5Hash(Hex(dd.EKey)),
					};
				}
			}
			catch { }
		}

		public void Save()
		{
			if (string.IsNullOrEmpty(_path)) return;
			var dtos = _entries.Values.Select(e => new Dto
			{
				Path = e.Path,
				FileDataId = e.FileDataId,
				NameHash = e.NameHash,
				CEKey = e.CEKey?.ToString() ?? new string('0', 32),
				EKey = e.EKey?.ToString() ?? new string('0', 32),
			}).ToList();
			Directory.CreateDirectory(Path.GetDirectoryName(_path));
			File.WriteAllText(_path, JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true }));
		}

		private static byte[] Hex(string s)
		{
			if (string.IsNullOrEmpty(s) || s.Length < 32) return new byte[16];
			var b = new byte[16];
			for (int i = 0; i < 16; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
			return b;
		}

		private class Dto
		{
			public string Path { get; set; }
			public uint FileDataId { get; set; }
			public ulong NameHash { get; set; }
			public string CEKey { get; set; }
			public string EKey { get; set; }
		}
	}

	/// <summary>Routes CASCEdit logs to the GUI; throws on hard (LogAndThrow) errors.</summary>
	public class GuiLogger : ICASCLog
	{
		private readonly Action<string> _sink;
		public GuiLogger(Action<string> sink) { _sink = sink; }
		private string F(string m, object[] a) => (a == null || a.Length == 0) ? m : string.Format(m, a);
		public void LogInformation(string m, params object[] a) => _sink?.Invoke("· " + F(m, a));
		public void LogDebug(string m, params object[] a) { }
		public void LogWarning(string m, params object[] a) => _sink?.Invoke("! " + F(m, a));
		public void LogError(string m, params object[] a) => _sink?.Invoke("ERR " + F(m, a));
		public void LogCritical(string m, params object[] a) => _sink?.Invoke("CRIT " + F(m, a));
		public void LogAndThrow(LogType type, string m, params object[] a)
		{
			var s = F(m, a);
			_sink?.Invoke("CRIT " + s);
			throw new Exception(s);
		}
	}
}
