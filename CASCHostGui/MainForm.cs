using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CASCEdit;
using CASCEdit.Structs;

namespace CASCHostGui
{
	public class MainForm : Form
	{
		private static readonly (string name, LocaleFlags flag)[] Locales =
		{
			("enUS", LocaleFlags.enUS), ("esES", LocaleFlags.esES), ("esMX", LocaleFlags.esMX),
			("deDE", LocaleFlags.deDE), ("frFR", LocaleFlags.frFR), ("ruRU", LocaleFlags.ruRU),
			("koKR", LocaleFlags.koKR), ("zhCN", LocaleFlags.zhCN), ("zhTW", LocaleFlags.zhTW),
			("ptBR", LocaleFlags.ptBR), ("itIT", LocaleFlags.itIT),
		};

		private TextBox txtClient, txtWorking, txtPatchUrl, txtHost, txtLog;
		private ComboBox cboLocale;
		private NumericUpDown numMinId;
		private CheckBox chkStatic, chkBnet;
		private DataGridView grid;
		private Button btnClient, btnWorking, btnAddFiles, btnAddFolder, btnRemove, btnClear,
			btnExtract, btnBuild, btnOpenOutput, btnServe;
		private ToolStripStatusLabel status;
		private ToolStripProgressBar progress;
		private StatusStrip statusBar;

		private HttpListener _server;
		private bool _busy;

		public MainForm()
		{
			BuildUi();
			// sensible defaults
			string guess = @"C:\Users\Administrator\Desktop\World of Warcraft 3.4.3.54261";
			if (File.Exists(Path.Combine(guess, ".build.info"))) txtClient.Text = guess;
			txtWorking.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CASCHostWork");
		}

		#region UI
		private void BuildUi()
		{
			Text = "CASCHost GUI - WoW Classic 3.4.3";
			Width = 1040; Height = 760; StartPosition = FormStartPosition.CenterScreen;

			var settings = new GroupBox { Text = "Configuración", Dock = DockStyle.Top, Height = 210 };
			int y = 22, lblW = 130, fldX = 140, fldW = 640;

			settings.Controls.Add(new Label { Text = "Cliente (.build.info):", Left = 10, Top = y + 3, Width = lblW });
			txtClient = new TextBox { Left = fldX, Top = y, Width = fldW }; settings.Controls.Add(txtClient);
			btnClient = new Button { Text = "...", Left = fldX + fldW + 6, Top = y - 1, Width = 34 };
			btnClient.Click += (s, e) => PickFolder(txtClient); settings.Controls.Add(btnClient);

			y += 30;
			settings.Controls.Add(new Label { Text = "Carpeta de trabajo:", Left = 10, Top = y + 3, Width = lblW });
			txtWorking = new TextBox { Left = fldX, Top = y, Width = fldW }; settings.Controls.Add(txtWorking);
			btnWorking = new Button { Text = "...", Left = fldX + fldW + 6, Top = y - 1, Width = 34 };
			btnWorking.Click += (s, e) => PickFolder(txtWorking); settings.Controls.Add(btnWorking);

			y += 30;
			settings.Controls.Add(new Label { Text = "Locale:", Left = 10, Top = y + 3, Width = lblW });
			cboLocale = new ComboBox { Left = fldX, Top = y, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
			foreach (var l in Locales) cboLocale.Items.Add(l.name); cboLocale.SelectedIndex = 0;
			settings.Controls.Add(cboLocale);
			settings.Controls.Add(new Label { Text = "MinimumFileDataID:", Left = 250, Top = y + 3, Width = 120 });
			numMinId = new NumericUpDown { Left = 375, Top = y, Width = 110, Minimum = 0, Maximum = 100000000, Value = 6000000 };
			settings.Controls.Add(numMinId);

			y += 30;
			chkStatic = new CheckBox { Text = "Generar estructura CDN (StaticMode)", Left = fldX, Top = y, Width = 280, Checked = true };
			settings.Controls.Add(chkStatic);
			chkBnet = new CheckBox { Text = "Reescribir download/install (launcher)", Left = fldX + 290, Top = y, Width = 300 };
			settings.Controls.Add(chkBnet);

			y += 28;
			settings.Controls.Add(new Label { Text = "PatchUrl (opcional):", Left = 10, Top = y + 3, Width = lblW });
			txtPatchUrl = new TextBox { Left = fldX, Top = y, Width = 300, PlaceholderText = "vacío = offline" };
			settings.Controls.Add(txtPatchUrl);
			settings.Controls.Add(new Label { Text = "Host CDN:", Left = 460, Top = y + 3, Width = 70 });
			txtHost = new TextBox { Left = 535, Top = y, Width = 245, PlaceholderText = "localhost:8000" };
			settings.Controls.Add(txtHost);

			Controls.Add(settings);

			// file action bar
			var bar = new Panel { Dock = DockStyle.Top, Height = 36 };
			btnAddFiles = MakeBtn("Añadir archivos", 6); btnAddFiles.Click += (s, e) => AddFiles();
			btnAddFolder = MakeBtn("Añadir carpeta", 140); btnAddFolder.Click += (s, e) => AddFolder();
			btnRemove = MakeBtn("Quitar", 274); btnRemove.Click += (s, e) => RemoveSelected();
			btnClear = MakeBtn("Limpiar", 360); btnClear.Click += (s, e) => grid.Rows.Clear();
			bar.Controls.AddRange(new Control[] { btnAddFiles, btnAddFolder, btnRemove, btnClear });
			Controls.Add(bar);

			// grid
			grid = new DataGridView
			{
				Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false,
				SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
			};
			grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Archivo en disco", Width = 420, ReadOnly = true });
			grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ruta CASC (ej: Interface/Custom/x.lua)", Width = 560 });
			Controls.Add(grid);

			// log
			txtLog = new TextBox { Dock = DockStyle.Bottom, Height = 170, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = System.Drawing.Color.Black, ForeColor = System.Drawing.Color.Gainsboro, Font = new System.Drawing.Font("Consolas", 8.5f) };
			Controls.Add(txtLog);

			// action buttons
			var actions = new Panel { Dock = DockStyle.Bottom, Height = 40 };
			btnExtract = MakeBtn("1. Extraer system files", 6, 200); btnExtract.Click += (s, e) => ExtractSystemFiles();
			btnBuild = MakeBtn("2. Construir / Inyectar", 214, 180); btnBuild.Click += (s, e) => Build();
			btnOpenOutput = MakeBtn("Abrir Output", 400, 110); btnOpenOutput.Click += (s, e) => OpenOutput();
			btnServe = MakeBtn("Servir CDN", 516, 120); btnServe.Click += (s, e) => ToggleServer();
			actions.Controls.AddRange(new Control[] { btnExtract, btnBuild, btnOpenOutput, btnServe });
			Controls.Add(actions);

			statusBar = new StatusStrip();
			status = new ToolStripStatusLabel("Listo.") { Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
			progress = new ToolStripProgressBar { Visible = false, Width = 160 };
			statusBar.Items.Add(status); statusBar.Items.Add(progress);
			Controls.Add(statusBar);

			// z-order so Fill grid sits between top settings/bar and bottom log/actions/status
			settings.BringToFront(); bar.BringToFront();
		}

		private Button MakeBtn(string text, int left, int width = 128)
			=> new Button { Text = text, Left = left, Top = 6, Width = width, Height = 27 };

		private void PickFolder(TextBox target)
		{
			using (var d = new FolderBrowserDialog())
			{
				if (Directory.Exists(target.Text)) d.SelectedPath = target.Text;
				if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
			}
		}
		#endregion

		#region File list
		private void AddFiles()
		{
			using (var d = new OpenFileDialog { Multiselect = true, Title = "Selecciona archivos a inyectar" })
			{
				if (d.ShowDialog(this) != DialogResult.OK) return;
				foreach (var f in d.FileNames)
					grid.Rows.Add(f, Path.GetFileName(f));
			}
		}

		private void AddFolder()
		{
			using (var d = new FolderBrowserDialog { Description = "La estructura bajo esta carpeta será la ruta CASC" })
			{
				if (d.ShowDialog(this) != DialogResult.OK) return;
				string root = d.SelectedPath;
				foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
				{
					string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
					grid.Rows.Add(f, rel);
				}
			}
		}

		private void RemoveSelected()
		{
			foreach (DataGridViewRow r in grid.SelectedRows) if (!r.IsNewRow) grid.Rows.Remove(r);
		}
		#endregion

		#region Actions
		private void Log(string s) { if (txtLog.IsHandleCreated) txtLog.BeginInvoke(new Action(() => { txtLog.AppendText(s + "\r\n"); })); }

		private void SetBusy(bool busy, string msg = null)
		{
			_busy = busy;
			foreach (var b in new[] { btnExtract, btnBuild, btnAddFiles, btnAddFolder, btnRemove, btnClear, btnClient, btnWorking })
				b.Enabled = !busy;
			progress.Visible = busy;
			progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
			if (msg != null) status.Text = msg;
			Cursor = busy ? Cursors.AppStarting : Cursors.Default;
		}

		private void ExtractSystemFiles()
		{
			if (_busy) return;
			string client = txtClient.Text.Trim(), working = txtWorking.Text.Trim();
			if (!File.Exists(Path.Combine(client, ".build.info"))) { Warn("El cliente no tiene .build.info."); return; }
			if (string.IsNullOrWhiteSpace(working)) { Warn("Indica una carpeta de trabajo."); return; }

			SetBusy(true, "Extrayendo system files...");
			Log("== Extrayendo system files ==");
			Task.Run(() =>
			{
				Directory.CreateDirectory(working);
				var settings = new CASSettings { BasePath = client, Basic = true, Logger = new GuiLogger(Log) };
				CASContainer.Open(settings);
				bool ok = CASContainer.ExtractSystemFiles(Path.Combine(working, "SystemFiles"));
				CASContainer.Close();
				return ok;
			}).ContinueWith(t => Done(t, "System files extraídos a SystemFiles\\.", "Error extrayendo system files"),
				TaskScheduler.FromCurrentSynchronizationContext());
		}

		private void Build()
		{
			if (_busy) return;
			string working = txtWorking.Text.Trim();
			string sysFiles = Path.Combine(working, "SystemFiles");
			if (!File.Exists(Path.Combine(sysFiles, ".build.info"))) { Warn("Extrae primero los system files (botón 1)."); return; }
			if (grid.Rows.Count == 0) { Warn("Añade al menos un archivo a inyectar."); return; }

			var files = grid.Rows.Cast<DataGridViewRow>()
				.Where(r => !r.IsNewRow && r.Cells[0].Value != null && r.Cells[1].Value != null)
				.Select(r => (disk: r.Cells[0].Value.ToString(), casc: r.Cells[1].Value.ToString().Trim()))
				.Where(x => File.Exists(x.disk) && x.casc.Length > 0).ToList();
			if (files.Count == 0) { Warn("No hay archivos válidos."); return; }

			var locale = Locales[cboLocale.SelectedIndex].flag;
			uint minId = (uint)numMinId.Value;
			bool isStatic = chkStatic.Checked, bnet = chkBnet.Checked;
			string patchUrl = txtPatchUrl.Text.Trim();
			string host = txtHost.Text.Trim();
			string version = ReadBuildInfoVersion(Path.Combine(sysFiles, ".build.info"));

			SetBusy(true, "Construyendo...");
			Log($"== Construir ({(isStatic ? "CDN/Static" : "local")}) - {files.Count} archivo(s), locale {locale} ==");

			Task.Run(() =>
			{
				var settings = new CASSettings
				{
					BasePath = working,
					SystemFilesPath = "SystemFiles",
					OutputPath = "Output",
					Locale = locale,
					StaticMode = isStatic,
					PatchUrl = patchUrl,
					Basic = string.IsNullOrWhiteSpace(patchUrl),
					Host = host,
					Logger = new GuiLogger(Log),
					Cache = new JsonCache(Path.Combine(working, "cache.json"), version),
					CDNs = new HashSet<string>(),
				};
				if (!string.IsNullOrWhiteSpace(host)) settings.CDNs.Add(host);

				CASContainer.Open(settings);
				Directory.CreateDirectory(settings.OutputPath);
				CASContainer.OpenCdnIndices(false);
				CASContainer.OpenEncoding();
				CASContainer.OpenRoot(locale, minId);
				if (bnet) { CASContainer.OpenDownload(); CASContainer.OpenInstall(); }

				foreach (var f in files)
				{
					CASContainer.RootHandler.AddFile(f.disk, f.casc);
					Log($"  + {f.casc}");
				}

				CASContainer.Save(); // writes Output, updates root/encoding/configs/.build.info
				return true;
			}).ContinueWith(t => Done(t, "Construcción completada. Revisa la carpeta Output\\.", "Error en la construcción"),
				TaskScheduler.FromCurrentSynchronizationContext());
		}

		private void Done(Task<bool> t, string okMsg, string errMsg)
		{
			SetBusy(false);
			if (t.IsFaulted)
			{
				var ex = t.Exception?.GetBaseException();
				Log("FALLO: " + ex?.Message);
				status.Text = errMsg;
				MessageBox.Show(this, ex?.Message ?? "Error", errMsg, MessageBoxButtons.OK, MessageBoxIcon.Error);
				try { CASContainer.Close(); } catch { }
				return;
			}
			Log(okMsg);
			status.Text = okMsg;
		}

		private void OpenOutput()
		{
			string outDir = Path.Combine(txtWorking.Text.Trim(), "Output");
			if (Directory.Exists(outDir))
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{outDir}\"") { UseShellExecute = true });
			else Warn("Aún no existe la carpeta Output (construye primero).");
		}

		private void ToggleServer()
		{
			if (_server != null) { try { _server.Stop(); _server.Close(); } catch { } _server = null; btnServe.Text = "Servir CDN"; Log("Servidor CDN detenido."); status.Text = "Servidor detenido."; return; }

			string outDir = Path.Combine(txtWorking.Text.Trim(), "Output");
			if (!Directory.Exists(outDir)) { Warn("No hay Output que servir (construye primero)."); return; }
			string hostPort = string.IsNullOrWhiteSpace(txtHost.Text) ? "localhost:8000" : txtHost.Text.Trim();
			int port = 8000;
			var parts = hostPort.Split(':'); if (parts.Length == 2) int.TryParse(parts[1], out port);

			try
			{
				_server = new HttpListener();
				_server.Prefixes.Add($"http://+:{port}/");
				_server.Start();
				btnServe.Text = "Detener CDN";
				Log($"Servidor CDN en http://localhost:{port}/  (raíz: Output\\)");
				status.Text = $"Sirviendo Output en :{port}";
				ServeLoop(outDir);
			}
			catch (Exception ex)
			{
				_server = null;
				Warn("No se pudo iniciar el servidor: " + ex.Message + "\n(ejecuta como administrador para http://+)");
			}
		}

		private async void ServeLoop(string root)
		{
			var srv = _server;
			while (srv != null && srv.IsListening)
			{
				HttpListenerContext ctx;
				try { ctx = await srv.GetContextAsync(); }
				catch { break; }
				try
				{
					string rel = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
					string file = Path.Combine(root, rel);
					if (File.Exists(file))
					{
						ctx.Response.ContentType = "application/octet-stream";
						using (var fs = File.OpenRead(file)) { ctx.Response.ContentLength64 = fs.Length; fs.CopyTo(ctx.Response.OutputStream); }
					}
					else { ctx.Response.StatusCode = 404; }
				}
				catch { try { ctx.Response.StatusCode = 500; } catch { } }
				finally { try { ctx.Response.OutputStream.Close(); } catch { } }
			}
		}

		private static string ReadBuildInfoVersion(string path)
		{
			try
			{
				var lines = File.ReadAllLines(path);
				if (lines.Length < 2) return "?";
				var header = lines[0].Split('|').Select(h => h.Split('!')[0]).ToArray();
				int vi = Array.IndexOf(header, "Version"), ai = Array.IndexOf(header, "Active");
				for (int i = 1; i < lines.Length; i++)
				{
					var c = lines[i].Split('|');
					if (ai < 0 || (ai < c.Length && c[ai] == "1")) return vi >= 0 && vi < c.Length ? c[vi] : "?";
				}
			}
			catch { }
			return "?";
		}

		private void Warn(string m) => MessageBox.Show(this, m, "CASCHost GUI", MessageBoxButtons.OK, MessageBoxIcon.Warning);

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			try { _server?.Stop(); _server?.Close(); } catch { }
			base.OnFormClosing(e);
		}
		#endregion
	}
}
