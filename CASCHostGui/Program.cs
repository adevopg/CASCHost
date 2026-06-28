using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CASCEdit;
using CASCEdit.Structs;

namespace CASCHostGui
{
	internal static class Program
	{
		[STAThread]
		static void Main(string[] args)
		{
			if (args != null && args.Length >= 3 && args[0] == "--selftest")
			{
				SelfTest(args[1], args[2]);
				return;
			}

			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
			Application.ThreadException += (s, e) => Fatal(e.Exception);
			AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);

			try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			Application.Run(new MainForm());
		}

		static void Fatal(Exception ex)
		{
			try { MessageBox.Show(ex?.ToString() ?? "Error", "CASCHost GUI - error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
			catch { }
		}

		// Headless end-to-end check: extract system files, inject a test file, build Output.
		static void SelfTest(string client, string working)
		{
			void L(string s) => Console.WriteLine(s);
			Directory.CreateDirectory(working);
			string sysFiles = Path.Combine(working, "SystemFiles");

			if (!File.Exists(Path.Combine(sysFiles, ".build.info")))
			{
				L("Extrayendo system files...");
				var s0 = new CASSettings { BasePath = client, Basic = true, Logger = new GuiLogger(m => { }) };
				CASContainer.Open(s0);
				if (!CASContainer.ExtractSystemFiles(sysFiles)) { L("FALLO extracción"); return; }
				CASContainer.Close();
			}
			string version = "?";
			try { version = File.ReadLines(Path.Combine(sysFiles, ".build.info")).Skip(1).First().Split('|').Last(); } catch { }

			string testFile = Path.Combine(working, "caschost_test.lua");
			File.WriteAllText(testFile, "-- CASCHost GUI test " + Guid.NewGuid().ToString("N"));

			L("Construyendo (StaticMode)...");
			var settings = new CASSettings
			{
				BasePath = working,
				SystemFilesPath = "SystemFiles",
				OutputPath = "Output",
				Locale = LocaleFlags.enUS,
				StaticMode = true,
				Basic = true,
				Logger = new GuiLogger(L),
				Cache = new JsonCache(Path.Combine(working, "cache.json"), version),
				CDNs = new System.Collections.Generic.HashSet<string>(),
			};
			CASContainer.Open(settings);
			Directory.CreateDirectory(settings.OutputPath);
			CASContainer.OpenCdnIndices(false);
			CASContainer.OpenEncoding();
			CASContainer.OpenRoot(LocaleFlags.enUS, 6000000);
			CASContainer.RootHandler.AddFile(testFile, "Interface/Custom/caschost_test.lua");
			CASContainer.Save();

			string outDir = Path.Combine(working, "Output");
			int files = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length : 0;
			L($"\nOutput: {files} archivos generados en {outDir}");
			L("cache.json: " + (File.Exists(Path.Combine(working, "cache.json")) ? "OK" : "no"));
			L(files > 0 ? "SELFTEST OK" : "SELFTEST FALLO (sin salida)");
		}
	}
}
