using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CASCViewer
{
	internal static class Program
	{
		[STAThread]
		static void Main(string[] args)
		{
			if (args != null && args.Length >= 2 && args[0] == "--selftest")
			{
				SelfTest(args[1]);
				return;
			}

			if (args != null && args.Length >= 3 && args[0] == "--genlistfile")
			{
				int maxMB = args.Length >= 4 && int.TryParse(args[3], out var mb) ? mb : 32;
				GenerateListfile(args[1], args[2], maxMB);
				return;
			}

			if (args != null && args.Length >= 4 && args[0] == "--applylistfile")
			{
				ApplyCommunityListfile(args[1], args[2], args[3]);
				return;
			}

			if (args != null && args.Length >= 2 && args[0] == "--localestats")
			{
				LocaleStats(args[1]);
				return;
			}

			if (args != null && args.Length >= 3 && args[0] == "--fileinfo")
			{
				FileInfo2(args[1], uint.Parse(args[2]));
				return;
			}

			if (args != null && args.Length >= 4 && args[0] == "--exportlocale")
			{
				// --exportlocale <locale> <client> <outDir> [extCsv] [maxFiles]
				string exts = args.Length >= 5 ? args[4] : null;
				int max = args.Length >= 6 && int.TryParse(args[5], out var mx) ? mx : 0;
				ExportLocale(args[1], args[2], args[3], exts, max);
				return;
			}

			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
			Application.ThreadException += (s, e) => ShowFatal(e.Exception);
			AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);

			try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);

			string initial = args != null && args.Length > 0 ? args[0] : null;
			Application.Run(new MainForm(initial));
		}

		static void ShowFatal(Exception ex)
		{
			try
			{
				string msg = ex?.ToString() ?? "Error desconocido.";
				try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "cascviewer-error.log"), msg); } catch { }
				MessageBox.Show(msg, "CASCViewer - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			catch { }
		}

		// Headless verification of the engine (load + enumerate + extract) against a real client.
		static void SelfTest(string clientPath)
		{
			var reader = new CascReader();
			reader.Load(clientPath, m => { });
			Console.WriteLine($"Loaded client {reader.Version}: {reader.Files.Count:N0} files");

			// Extract the first few locally-present files and report.
			int ok = 0, fail = 0; long bytes = 0;
			foreach (var f in reader.Files)
			{
				if (ok >= 5) break;
				try { var d = reader.Extract(f); bytes += d.Length; ok++; Console.WriteLine($"  OK  id={f.Id} size={d.Length} magic={Magic(d)}"); }
				catch (Exception ex) { fail++; if (fail <= 3) Console.WriteLine($"  ERR id={f.Id}: {ex.Message}"); }
			}
			Console.WriteLine($"Extracted {ok} files, {bytes:N0} bytes, {fail} early failures sampled.");
			reader.Dispose();
		}

		static void GenerateListfile(string clientPath, string outPath, int maxMB)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var reader = new CascReader();
			Console.WriteLine("Cargando cliente...");
			reader.Load(clientPath, m => { });
			Console.WriteLine($"Cliente {reader.Version}: {reader.Files.Count:N0} archivos");

			var gen = new ListfileGenerator(reader);
			Console.WriteLine($"Con nameHash (recuperables): {gen.NameableCount:N0}");

			Console.WriteLine("Pass 1: binarios (.exe/.dll)...");
			gen.ScanBinaries(clientPath, null);
			Console.WriteLine($"  tras binarios: {gen.FoundCount:N0} nombres");

			if (maxMB > 0)
			{
				Console.WriteLine($"Pass 2: contenido de los archivos (cap {maxMB} MB, en paralelo)...");
				gen.ScanGameData((long)maxMB * 1024 * 1024, (d, t) =>
				{
					Console.Write($"\r  {d:N0}/{t:N0}  ({gen.FoundCount:N0} nombres)   ");
				}, System.Threading.CancellationToken.None);
				Console.WriteLine();
			}

			int n = gen.Save(outPath);
			sw.Stop();
			double pct = gen.NameableCount > 0 ? 100.0 * n / gen.NameableCount : 0;
			Console.WriteLine($"Listfile: {n:N0} de {gen.NameableCount:N0} recuperables ({pct:F1}%) en {sw.Elapsed.TotalSeconds:F0}s");
			Console.WriteLine($"Guardado: {outPath}");
			reader.Dispose();
		}

		static void ExportLocale(string localeName, string clientPath, string outDir, string extCsv, int maxFiles)
		{
			if (!Enum.TryParse<CASCEdit.Structs.LocaleFlags>(localeName, true, out var locale))
			{
				Console.WriteLine($"Locale desconocida: {localeName}. Ej: esES, enUS, ruRU...");
				return;
			}

			var exts = string.IsNullOrWhiteSpace(extCsv)
				? null
				: new System.Collections.Generic.HashSet<string>(
					extCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim().TrimStart('.').ToLowerInvariant()),
					StringComparer.OrdinalIgnoreCase);

			var reader = new CascReader();
			Console.WriteLine("Cargando cliente...");
			reader.Load(clientPath, m => { });
			string lf = Path.Combine(clientPath, "listfile.csv");
			if (File.Exists(lf)) { reader.LoadListfile(lf); Console.WriteLine($"listfile.csv aplicado."); }

			bool missingOnly = string.Equals(extCsv, "missing", StringComparison.OrdinalIgnoreCase);
			bool includeShared = string.Equals(extCsv, "all", StringComparison.OrdinalIgnoreCase);
			if (missingOnly || includeShared) exts = null;

			// Default: only files genuinely localized for this locale (Spanish text/lua/audio),
			// NOT every shared file that merely carries the locale flag. Pass "all" to include shared.
			var list = includeShared ? reader.EnumerateLocale(locale) : reader.EnumerateLocaleSpecific(locale);
			if (missingOnly) list = list.Where(f => !reader.IsLocal(f.CKey)).ToList();
			if (exts != null) list = list.Where(f => exts.Contains(f.Extension)).ToList();
			if (maxFiles > 0) list = list.Take(maxFiles).ToList();
			Console.WriteLine($"{locale} ({(includeShared ? "todo flag" : "específico")}): {list.Count:N0} archivos a exportar -> {outDir}");

			Directory.CreateDirectory(outDir);
			int ok = 0, cdn = 0, fail = 0, done = 0;
			var errors = new System.Collections.Generic.List<string>();
			object gate = new object();

			System.Threading.Tasks.Parallel.ForEach(list,
				new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 }, f =>
			{
				try
				{
					// Resume: skip files already exported (only meaningful when we know the path).
					if (!string.IsNullOrEmpty(f.Path))
					{
						string existing = Path.Combine(outDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
						if (File.Exists(existing)) { lock (gate) { ok++; } goto progress; }
					}

					byte[] data = reader.ExtractByCKey(f.CKey, out bool wasCdn);
					string rel = !string.IsNullOrEmpty(f.Path) ? f.Path : $"FileDataID/{f.Id}.{CascReader.SniffExtension(data)}";
					string full = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
					Directory.CreateDirectory(Path.GetDirectoryName(full));
					File.WriteAllBytes(full, data);
					lock (gate) { ok++; if (wasCdn) cdn++; }
				}
				catch (Exception ex) { lock (gate) { fail++; if (errors.Count < 15) errors.Add($"{f.Id}: {ex.Message}"); } }
			progress:
				int d = System.Threading.Interlocked.Increment(ref done);
				if ((d & 0x3F) == 0 || d == list.Count) Console.Write($"\r  {d:N0}/{list.Count:N0}  ok={ok} cdn={cdn} fail={fail}   ");
			});
			Console.WriteLine();
			Console.WriteLine($"Hecho: {ok:N0} extraídos ({cdn:N0} desde CDN), {fail:N0} fallidos.");
			foreach (var e in errors) Console.WriteLine("  " + e);
			reader.Dispose();
		}

		static void FileInfo2(string clientPath, uint fileId)
		{
			var reader = new CascReader();
			reader.Load(clientPath, m => { });
			var rh = CASCEdit.CASContainer.RootHandler;
			Console.WriteLine($"FileDataID {fileId}: bloques en el root:");
			var seenCk = new System.Collections.Generic.HashSet<string>();
			foreach (var c in rh.Chunks)
			{
				foreach (var e in c.Entries)
				{
					if (e.FileDataId != fileId) continue;
					bool local = reader.IsLocal(e.CEKey);
					Console.WriteLine($"  locale={(CASCEdit.Structs.LocaleFlags)c.LocaleFlags,-45} CKey={e.CEKey} {(local ? "[LOCAL]" : "[cdn]")}");
					seenCk.Add(e.CEKey.ToString());
				}
			}
			Console.WriteLine($"CKeys distintas para este id: {seenCk.Count}  (1 = mismo contenido todas las locales)");

			// Try to read each distinct variant and show a language sample.
			const uint esES = 0x80, esMX = 0x1000, enUS = 0x2;
			foreach (var c in rh.Chunks)
			{
				if (((uint)c.LocaleFlags & (esES | esMX | enUS)) == 0) continue;
				var e = c.Entries.FirstOrDefault(x => x.FileDataId == fileId);
				if (e == null) continue;
				try
				{
					byte[] data = reader.ExtractByCKey(e.CEKey, out bool cdn);
					string txt = System.Text.Encoding.Latin1.GetString(data);
					var words = System.Text.RegularExpressions.Regex.Matches(txt, "[A-Za-zÁÉÍÓÚáéíóúÑñ¡¿]{4,}")
						.Select(m => m.Value).Distinct().Skip(20).Take(25).ToArray();
					Console.WriteLine($"\n[{(CASCEdit.Structs.LocaleFlags)c.LocaleFlags}] ({(cdn ? "CDN" : "local")}, {data.Length} bytes):");
					Console.WriteLine("  " + string.Join(" ", words));
				}
				catch (Exception ex) { Console.WriteLine($"\n[{(CASCEdit.Structs.LocaleFlags)c.LocaleFlags}] no disponible: {ex.Message}"); }
			}
			reader.Dispose();
		}

		static void LocaleStats(string clientPath)
		{
			var reader = new CascReader();
			reader.Load(clientPath, m => { });
			var rh = CASCEdit.CASContainer.RootHandler;
			var enc = CASCEdit.CASContainer.EncodingHandler;
			var lih = CASCEdit.CASContainer.LocalIndexHandler;
			Console.WriteLine($"Cliente {reader.Version}\n");

			// distinct locale masks
			var maskCounts = new System.Collections.Generic.Dictionary<uint, int>();
			foreach (var c in rh.Chunks)
			{
				uint m = (uint)c.LocaleFlags;
				maskCounts.TryGetValue(m, out int v);
				maskCounts[m] = v + c.Entries.Count;
			}
			Console.WriteLine("Mascaras de locale en el root (entradas):");
			foreach (var kv in maskCounts.OrderByDescending(k => k.Value))
				Console.WriteLine($"  0x{kv.Key:X6}  {(CASCEdit.Structs.LocaleFlags)kv.Key,-40}  {kv.Value:N0}");

			// esES-specific content: CKeys in esES blocks vs enUS blocks
			const uint esES = 0x80, enUS = 0x2;
			var esKeys = new System.Collections.Generic.HashSet<string>();
			var enKeys = new System.Collections.Generic.HashSet<string>();
			foreach (var c in rh.Chunks)
			{
				bool hasEs = ((uint)c.LocaleFlags & esES) != 0;
				bool hasEn = ((uint)c.LocaleFlags & enUS) != 0;
				if (!hasEs && !hasEn) continue;
				foreach (var e in c.Entries)
				{
					string k = e.CEKey.ToString();
					if (hasEs) esKeys.Add(k);
					if (hasEn) enKeys.Add(k);
				}
			}
			var esOnly = esKeys.Where(k => !enKeys.Contains(k)).ToList();

			// extractability of esES-only content keys
			int present = 0;
			foreach (var k in esOnly)
			{
				if (enc.CEKeys.TryGetValue(new CASCEdit.Helpers.MD5Hash(FromHex(k)), out var ce) && ce.EKeys.Count > 0
					&& lih.GetIndexInfo(ce.EKeys[0]) != null)
					present++;
			}

			Console.WriteLine($"\nContenido especifico de esES (CKeys en bloques esES y NO en enUS): {esOnly.Count:N0}");
			Console.WriteLine($"  de los cuales presentes en archivos locales: {present:N0}");

			// Print a few CDN URLs for esES content that is NOT local, to test CDN availability.
			string cdnHost = CASCEdit.CASContainer.BuildInfo["CDN Hosts"].Split(' ').FirstOrDefault();
			string cdnPath = CASCEdit.CASContainer.BuildInfo["CDN Path"];
			Console.WriteLine($"\nCDN host={cdnHost} path={cdnPath}");
			int shown = 0;
			foreach (var k in esOnly)
			{
				if (shown >= 3) break;
				if (!enc.CEKeys.TryGetValue(new CASCEdit.Helpers.MD5Hash(FromHex(k)), out var ce) || ce.EKeys.Count == 0) continue;
				if (lih.GetIndexInfo(ce.EKeys[0]) != null) continue; // skip local ones
				string ek = ce.EKeys[0].ToString();
				Console.WriteLine($"  http://{cdnHost}/{cdnPath}/data/{ek.Substring(0,2)}/{ek.Substring(2,2)}/{ek}");
				shown++;
			}
			Console.WriteLine(present == 0
				? "  => El cliente NO tiene contenido esES descargado (solo referencias; haría falta CDN)."
				: "  => Hay contenido esES disponible localmente.");
			reader.Dispose();
		}

		static byte[] FromHex(string s)
		{
			var b = new byte[s.Length / 2];
			for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
			return b;
		}

		static void ApplyCommunityListfile(string clientPath, string communityCsv, string outPath)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var reader = new CascReader();
			Console.WriteLine("Cargando cliente...");
			reader.Load(clientPath, m => { });
			Console.WriteLine($"Cliente {reader.Version}: {reader.Files.Count:N0} archivos");

			var gen = new ListfileGenerator(reader);
			Console.WriteLine($"Validando '{Path.GetFileName(communityCsv)}' contra el cliente (por hash)...");
			int n = gen.ImportCommunityListfile(communityCsv, (r, t) =>
			{
				Console.Write($"\r  {100.0 * r / t:F0}%  ({gen.FoundCount:N0} coincidencias)   ");
			}, System.Threading.CancellationToken.None);
			Console.WriteLine();

			gen.Save(outPath);
			sw.Stop();
			double pct = gen.NameableCount > 0 ? 100.0 * n / gen.NameableCount : 0;
			Console.WriteLine($"Listfile validado: {n:N0} de {gen.NameableCount:N0} con nombre ({pct:F1}%) en {sw.Elapsed.TotalSeconds:F0}s");
			Console.WriteLine($"Guardado: {outPath}");
			reader.Dispose();
		}

		static string Magic(byte[] d)
		{
			if (d == null || d.Length < 4) return "?";
			var sb = new System.Text.StringBuilder();
			for (int i = 0; i < 4; i++) sb.Append(d[i] >= 32 && d[i] < 127 ? (char)d[i] : '.');
			return sb.ToString();
		}
	}
}
