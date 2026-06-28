using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CASCEdit.Structs;

namespace CASCViewer
{
	public class MainForm : Form
	{
		private readonly CascReader _reader = new CascReader();
		private List<GameFile> _view = new List<GameFile>();   // currently shown (filtered)
		private string _folderFilter;                          // null=all, "@unknown", or dir prefix
		private uint _localeBit;                               // 0 = any locale
		private List<GameFile> _localeFiles;                   // locale-specific files (with that locale's CKey)

		private static readonly (string name, uint bit)[] Locales =
		{
			("Todas", 0u), ("enUS", 0x2), ("koKR", 0x4), ("frFR", 0x10), ("deDE", 0x20),
			("zhCN", 0x40), ("esES", 0x80), ("zhTW", 0x100), ("enGB", 0x200), ("esMX", 0x1000),
			("ruRU", 0x2000), ("ptBR", 0x4000), ("itIT", 0x8000), ("ptPT", 0x10000),
		};
		private bool _busy;
		private CancellationTokenSource _cts;

		// sorting
		private int _sortColumn = 0;
		private bool _sortAsc = true;

		// controls
		private ToolStrip _toolbar;
		private ToolStripButton _btnOpen, _btnListfile, _btnGenList, _btnExtractSel, _btnExtractAll, _btnCancel;
		private ToolStripTextBox _txtSearch;
		private ToolStripComboBox _localeFilter;
		private SplitContainer _split;
		private TreeView _tree;
		private ListView _list;
		private StatusStrip _statusBar;
		private ToolStripStatusLabel _status;
		private ToolStripProgressBar _progress;
		private System.Windows.Forms.Timer _searchTimer;

		private string _initialClientPath;

		public MainForm(string initialClientPath)
		{
			BuildUi();

			string def = initialClientPath;
			if (string.IsNullOrEmpty(def))
			{
				string guess = @"C:\Users\Administrator\Desktop\World of Warcraft 3.4.3.54261";
				if (File.Exists(Path.Combine(guess, ".build.info"))) def = guess;
			}
			if (!string.IsNullOrEmpty(def) && File.Exists(Path.Combine(def, ".build.info")))
				_initialClientPath = def;
		}

		#region UI construction
		private void BuildUi()
		{
			Text = "CASC Viewer - WoW Classic 3.4.3";
			Width = 1100;
			Height = 720;
			StartPosition = FormStartPosition.CenterScreen;

			_toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
			_btnOpen = new ToolStripButton("Abrir cliente...") { ToolTipText = "Selecciona la carpeta del cliente (con .build.info)" };
			_btnListfile = new ToolStripButton("Cargar listfile...") { ToolTipText = "CSV id;ruta para mostrar nombres reales" };
			_btnGenList = new ToolStripButton("Generar listfile") { Enabled = false, ToolTipText = "Recupera nombres desde el propio cliente (parcial: solo archivos referenciados por nombre)" };
			_btnExtractSel = new ToolStripButton("Extraer selección") { Enabled = false };
			_btnExtractAll = new ToolStripButton("Extraer lista filtrada") { Enabled = false };
			_btnCancel = new ToolStripButton("Cancelar") { Enabled = false, ForeColor = Color.Firebrick };
			_txtSearch = new ToolStripTextBox { ToolTipText = "Buscar por ruta o FileDataID", Width = 240 };
			_localeFilter = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, ToolTipText = "Filtrar por locale (esES, deDE, ...)" };
			foreach (var l in Locales) _localeFilter.Items.Add(l.name);

			_btnOpen.Click += (s, e) => PickAndLoadClient();
			_btnListfile.Click += (s, e) => PickAndLoadListfile();
			_btnGenList.Click += (s, e) => GenerateListfile();
			_btnExtractSel.Click += (s, e) => StartExtraction(false);
			_btnExtractAll.Click += (s, e) => StartExtraction(true);
			_btnCancel.Click += (s, e) => _cts?.Cancel();
			_txtSearch.TextChanged += (s, e) => _searchTimer.Stop(); // restart debounce
			_txtSearch.TextChanged += (s, e) => _searchTimer.Start();

			_toolbar.Items.AddRange(new ToolStripItem[]
			{
				_btnOpen, _btnListfile, _btnGenList, new ToolStripSeparator(),
				_btnExtractSel, _btnExtractAll, _btnCancel, new ToolStripSeparator(),
				new ToolStripLabel("Locale:"), _localeFilter,
				new ToolStripLabel("Buscar:"), _txtSearch
			});

			_searchTimer = new System.Windows.Forms.Timer { Interval = 250 };
			_searchTimer.Tick += (s, e) => { _searchTimer.Stop(); RebuildView(); };

			_split = new SplitContainer { Dock = DockStyle.Fill };

			_tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
			_tree.AfterSelect += (s, e) =>
			{
				_folderFilter = e.Node?.Tag as string;
				RebuildView();
			};
			_split.Panel1.Controls.Add(_tree);

			_list = new ListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				VirtualMode = true,
				FullRowSelect = true,
				MultiSelect = true,
				HideSelection = false,
				GridLines = true,
			};
			_list.Columns.Add("FileDataID", 90, HorizontalAlignment.Right);
			_list.Columns.Add("Nombre / Ruta", 540);
			_list.Columns.Add("Tamaño", 90, HorizontalAlignment.Right);
			_list.Columns.Add("Tipo", 60);
			_list.Columns.Add("Locale", 120);
			_list.Columns.Add("CKey", 250);
			_list.RetrieveVirtualItem += List_RetrieveVirtualItem;
			_list.ColumnClick += List_ColumnClick;
			_list.DoubleClick += (s, e) => { if (_list.SelectedIndices.Count > 0) StartExtraction(false); };
			_list.SelectedIndexChanged += (s, e) => UpdateSelectionStatus();
			_list.ContextMenuStrip = BuildContextMenu();
			_split.Panel2.Controls.Add(_list);

			_statusBar = new StatusStrip();
			_status = new ToolStripStatusLabel("Sin cliente cargado.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
			_progress = new ToolStripProgressBar { Visible = false, Width = 200 };
			_statusBar.Items.Add(_status);
			_statusBar.Items.Add(_progress);

			Controls.Add(_split);
			Controls.Add(_toolbar);
			Controls.Add(_statusBar);

			// Wire the locale filter now that _list exists (selecting fires RebuildView).
			_localeFilter.SelectedIndexChanged += (s, e) =>
			{
				_localeBit = _localeFilter.SelectedIndex >= 0 ? Locales[_localeFilter.SelectedIndex].bit : 0u;
				_localeFiles = null;
				if (_localeBit != 0 && _reader.IsLoaded)
				{
					// Use the locale-specific variants so the CKey/extraction reflect that language.
					try { _localeFiles = _reader.EnumerateLocaleSpecific((LocaleFlags)_localeBit); } catch { }
				}
				RebuildView();
			};
			_localeFilter.SelectedIndex = 0;
		}

		private ContextMenuStrip BuildContextMenu()
		{
			var menu = new ContextMenuStrip();
			menu.Items.Add("Extraer seleccionado(s)", null, (s, e) => StartExtraction(false));
			menu.Items.Add(new ToolStripSeparator());
			menu.Items.Add("Copiar FileDataID", null, (s, e) => CopyField(f => f.Id.ToString()));
			menu.Items.Add("Copiar ruta/nombre", null, (s, e) => CopyField(f => f.DisplayName));
			menu.Items.Add("Copiar CKey", null, (s, e) => CopyField(f => f.CKey.ToString()));
			return menu;
		}
		#endregion

		#region Virtual list
		private void List_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
		{
			if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count) { e.Item = new ListViewItem(); return; }
			var f = _view[e.ItemIndex];
			var item = new ListViewItem(f.Id.ToString());
			item.SubItems.Add(f.DisplayName);
			item.SubItems.Add(FormatSize(f.Size));
			item.SubItems.Add(string.IsNullOrEmpty(f.Extension) ? "" : f.Extension);
			item.SubItems.Add(f.Locale.ToString());
			item.SubItems.Add(f.CKey.ToString());
			item.Tag = f;
			e.Item = item;
		}

		private void List_ColumnClick(object sender, ColumnClickEventArgs e)
		{
			if (_busy) return;
			if (_sortColumn == e.Column) _sortAsc = !_sortAsc;
			else { _sortColumn = e.Column; _sortAsc = true; }
			SortView();
			_list.Invalidate();
		}

		private void SortView()
		{
			Comparison<GameFile> cmp;
			switch (_sortColumn)
			{
				case 1: cmp = (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase); break;
				case 2: cmp = (a, b) => a.Size.CompareTo(b.Size); break;
				case 3: cmp = (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase); break;
				default: cmp = (a, b) => a.Id.CompareTo(b.Id); break;
			}
			_view.Sort((a, b) => _sortAsc ? cmp(a, b) : -cmp(a, b));
		}
		#endregion

		#region Loading
		private void PickAndLoadClient()
		{
			using (var dlg = new FolderBrowserDialog { Description = "Selecciona la carpeta raíz del cliente WoW 3.4.3 (con .build.info)" })
			{
				if (dlg.ShowDialog(this) == DialogResult.OK)
					LoadClient(dlg.SelectedPath);
			}
		}

		private void LoadClient(string path)
		{
			if (_busy) return;
			SetBusy(true, "Cargando cliente...");
			_progress.Style = ProgressBarStyle.Marquee;
			_progress.Visible = true;

			Task.Run(() =>
			{
				_reader.Load(path, msg => { });
			}).ContinueWith(t =>
			{
				_progress.Visible = false;
				_progress.Style = ProgressBarStyle.Blocks;
				SetBusy(false, null);

				if (t.IsFaulted)
				{
					var ex = t.Exception?.GetBaseException();
					MessageBox.Show(this, ex?.Message ?? "Error desconocido.", "Error al cargar",
						MessageBoxButtons.OK, MessageBoxIcon.Error);
					_status.Text = "Error al cargar el cliente.";
					return;
				}

				_btnExtractSel.Enabled = true;
				_btnExtractAll.Enabled = true;
				_btnGenList.Enabled = true;
				_folderFilter = null;
				RebuildTree();
				RebuildView();
				_status.Text = $"Cliente {_reader.Version} - {_reader.Files.Count:N0} archivos" +
					(_reader.HasListfile ? $" ({_reader.NamedCount:N0} con nombre)" : " (sin listfile: solo FileDataID)");
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}

		private void PickAndLoadListfile()
		{
			if (!_reader.IsLoaded) { MessageBox.Show(this, "Carga primero un cliente.", "Listfile", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
			using (var dlg = new OpenFileDialog { Title = "Selecciona un listfile (id;ruta)", Filter = "Listfile (*.csv;*.txt)|*.csv;*.txt|Todos|*.*" })
			{
				if (dlg.ShowDialog(this) != DialogResult.OK) return;
				try
				{
					int n = _reader.LoadListfile(dlg.FileName);
					RebuildTree();
					RebuildView();
					_status.Text = $"Listfile aplicado: {n:N0} de {_reader.Files.Count:N0} archivos con nombre.";
				}
				catch (Exception ex)
				{
					MessageBox.Show(this, ex.Message, "Error al leer listfile", MessageBoxButtons.OK, MessageBoxIcon.Error);
				}
			}
		}
		#endregion

		#region Generate listfile (from the client)
		private void GenerateListfile()
		{
			if (_busy || !_reader.IsLoaded) return;

			var info = MessageBox.Show(this,
				"Recupera nombres de archivo hasheando cadenas encontradas en los binarios y en el " +
				"contenido de los archivos del juego.\n\nAVISO: el cliente moderno referencia casi todo por " +
				"FileDataID, no por nombre, así que esto recupera solo una fracción (normalmente <1%, sobre todo " +
				"interfaz/texturas). Para nombres completos usa un listfile de la comunidad.\n\nPuede tardar " +
				"varios minutos. ¿Continuar?",
				"Generar listfile", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
			if (info != DialogResult.OK) return;

			string outPath;
			using (var dlg = new SaveFileDialog
			{
				Title = "Guardar listfile generado",
				Filter = "Listfile (*.csv)|*.csv",
				FileName = "listfile_generado.csv"
			})
			{
				if (dlg.ShowDialog(this) != DialogResult.OK) return;
				outPath = dlg.FileName;
			}

			_cts = new CancellationTokenSource();
			var token = _cts.Token;
			SetBusy(true, "Generando listfile (binarios)...");
			_btnCancel.Enabled = true;
			_progress.Style = ProgressBarStyle.Blocks;
			_progress.Minimum = 0;
			_progress.Maximum = _reader.Files.Count;
			_progress.Value = 0;
			_progress.Visible = true;

			var progress = new Progress<(int done, int total, int found)>(p =>
			{
				_progress.Value = Math.Min(p.done, _progress.Maximum);
				_status.Text = $"Generando listfile: {p.done:N0} / {p.total:N0}  ({p.found:N0} nombres)";
			});

			Task.Run(() =>
			{
				var gen = new ListfileGenerator(_reader);
				gen.ScanBinaries(_reader.ClientPath, null);
				gen.ScanGameData(16L * 1024 * 1024,
					(d, t) => ((IProgress<(int, int, int)>)progress).Report((d, t, gen.FoundCount)),
					token);
				int n = gen.Save(outPath);
				return (n, gen.NameableCount);
			}, token).ContinueWith(t =>
			{
				_progress.Visible = false;
				_btnCancel.Enabled = false;
				SetBusy(false, null);
				_cts?.Dispose();
				_cts = null;

				if (t.IsFaulted)
				{
					MessageBox.Show(this, t.Exception?.GetBaseException().Message, "Error",
						MessageBoxButtons.OK, MessageBoxIcon.Error);
					return;
				}

				var (n, nameable) = t.Result;
				try { _reader.LoadListfile(outPath); } catch { }
				RebuildTree();
				RebuildView();
				double pct = nameable > 0 ? 100.0 * n / nameable : 0;
				MessageBox.Show(this,
					$"Listfile generado y aplicado.\n\nNombres recuperados: {n:N0} de {nameable:N0} ({pct:F1}%)\n" +
					$"Guardado en:\n{outPath}",
					"Generar listfile", MessageBoxButtons.OK, MessageBoxIcon.Information);
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}
		#endregion

		#region Tree + filtering
		private void RebuildTree()
		{
			_tree.BeginUpdate();
			_tree.Nodes.Clear();

			var all = new TreeNode($"Todos los archivos ({_reader.Files.Count:N0})") { Tag = null };
			_tree.Nodes.Add(all);

			if (_reader.HasListfile)
			{
				var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var f in _reader.Files)
				{
					if (string.IsNullOrEmpty(f.Path)) continue;
					int slash = f.Path.LastIndexOf('/');
					if (slash <= 0) continue;
					string dir = f.Path.Substring(0, slash);
					while (dir.Length > 0)
					{
						if (!dirs.Add(dir)) break; // ancestors already present
						int s = dir.LastIndexOf('/');
						dir = s < 0 ? "" : dir.Substring(0, s);
					}
				}

				var map = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
				foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
				{
					int s = dir.LastIndexOf('/');
					string parent = s < 0 ? "" : dir.Substring(0, s);
					string name = s < 0 ? dir : dir.Substring(s + 1);
					TreeNode parentNode = parent.Length == 0 ? all : (map.TryGetValue(parent, out var pn) ? pn : all);
					var node = new TreeNode(name) { Tag = dir };
					parentNode.Nodes.Add(node);
					map[dir] = node;
				}

				int unknown = _reader.Files.Count - _reader.NamedCount;
				if (unknown > 0)
					_tree.Nodes.Add(new TreeNode($"(sin nombre) ({unknown:N0})") { Tag = "@unknown" });
			}

			all.Expand();
			_tree.SelectedNode = all;
			_tree.EndUpdate();
		}

		private void RebuildView()
		{
			if (!_reader.IsLoaded) { _view = new List<GameFile>(); _list.VirtualListSize = 0; return; }

			string q = _txtSearch.Text?.Trim();
			bool hasQuery = !string.IsNullOrEmpty(q);
			bool numeric = hasQuery && q.All(char.IsDigit);

			// When a locale is selected, browse its locale-specific files (with that language's content).
			IEnumerable<GameFile> src = (_localeBit != 0 && _localeFiles != null) ? _localeFiles : _reader.Files;

			if (_folderFilter == "@unknown")
				src = src.Where(f => string.IsNullOrEmpty(f.Path));
			else if (!string.IsNullOrEmpty(_folderFilter))
				src = src.Where(f => f.Path != null && f.Path.StartsWith(_folderFilter + "/", StringComparison.OrdinalIgnoreCase));

			if (hasQuery)
			{
				if (numeric)
					src = src.Where(f => f.Id.ToString().Contains(q));
				else
					src = src.Where(f => f.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
			}

			_view = src.ToList();
			SortView();

			_list.VirtualListSize = _view.Count;
			_list.Invalidate();
			UpdateSelectionStatus();
		}

		private void UpdateSelectionStatus()
		{
			if (_busy) return;
			int sel = _list.SelectedIndices.Count;
			_status.Text = $"{_view.Count:N0} archivos mostrados" + (sel > 0 ? $" - {sel:N0} seleccionado(s)" : "")
				+ (_reader.IsLoaded ? $"  |  Cliente {_reader.Version}" : "");
		}
		#endregion

		#region Extraction
		private void StartExtraction(bool wholeFilteredList)
		{
			if (_busy || !_reader.IsLoaded) return;

			List<GameFile> targets;
			if (wholeFilteredList)
			{
				targets = _view.ToList();
			}
			else
			{
				targets = new List<GameFile>();
				foreach (int i in _list.SelectedIndices)
					if (i >= 0 && i < _view.Count) targets.Add(_view[i]);
			}

			if (targets.Count == 0)
			{
				MessageBox.Show(this, "No hay archivos para extraer.", "Extraer", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}

			string outDir;
			using (var dlg = new FolderBrowserDialog { Description = $"Carpeta de salida para {targets.Count:N0} archivo(s)" })
			{
				if (dlg.ShowDialog(this) != DialogResult.OK) return;
				outDir = dlg.SelectedPath;
			}

			_cts = new CancellationTokenSource();
			var token = _cts.Token;
			SetBusy(true, "Extrayendo...");
			_btnCancel.Enabled = true;
			_progress.Style = ProgressBarStyle.Blocks;
			_progress.Minimum = 0;
			_progress.Maximum = targets.Count;
			_progress.Value = 0;
			_progress.Visible = true;

			var progress = new Progress<int>(done =>
			{
				_progress.Value = Math.Min(done, _progress.Maximum);
				_status.Text = $"Extrayendo {done:N0} / {targets.Count:N0}...";
			});

			Task.Run(() =>
			{
				int ok = 0, fail = 0, done = 0;
				var errors = new List<string>();
				foreach (var f in targets)
				{
					if (token.IsCancellationRequested) break;
					try
					{
						byte[] data = _reader.Extract(f);
						string rel = !string.IsNullOrEmpty(f.Path)
							? f.Path
							: $"FileDataID/{f.Id}.{CascReader.SniffExtension(data)}";

						string full = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
						Directory.CreateDirectory(Path.GetDirectoryName(full));
						File.WriteAllBytes(full, data);
						ok++;
					}
					catch (Exception ex)
					{
						fail++;
						if (errors.Count < 25) errors.Add($"{f.DisplayName}: {ex.Message}");
					}
					done++;
					if (done % 16 == 0 || done == targets.Count)
						((IProgress<int>)progress).Report(done);
				}
				return (ok, fail, errors, token.IsCancellationRequested);
			}, token).ContinueWith(t =>
			{
				_progress.Visible = false;
				_btnCancel.Enabled = false;
				SetBusy(false, null);
				_cts?.Dispose();
				_cts = null;

				if (t.IsFaulted)
				{
					MessageBox.Show(this, t.Exception?.GetBaseException().Message, "Error de extracción",
						MessageBoxButtons.OK, MessageBoxIcon.Error);
					return;
				}

				var (ok, fail, errors, cancelled) = t.Result;
				_status.Text = $"Extracción {(cancelled ? "cancelada" : "completa")}: {ok:N0} OK, {fail:N0} con error.";
				string msg = $"{(cancelled ? "Cancelado.\n\n" : "")}Extraídos: {ok:N0}\nFallidos: {fail:N0}\nDestino:\n{outDir}";
				if (errors.Count > 0) msg += "\n\nPrimeros errores:\n" + string.Join("\n", errors);
				MessageBox.Show(this, msg, "Extracción", MessageBoxButtons.OK,
					fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}
		#endregion

		#region Helpers
		private void CopyField(Func<GameFile, string> selector)
		{
			var parts = new List<string>();
			foreach (int i in _list.SelectedIndices)
				if (i >= 0 && i < _view.Count) parts.Add(selector(_view[i]));
			if (parts.Count > 0)
			{
				try { Clipboard.SetText(string.Join(Environment.NewLine, parts)); } catch { }
			}
		}

		private void SetBusy(bool busy, string status)
		{
			_busy = busy;
			_btnOpen.Enabled = !busy;
			_btnListfile.Enabled = !busy;
			_btnGenList.Enabled = !busy && _reader.IsLoaded;
			_btnExtractSel.Enabled = !busy && _reader.IsLoaded;
			_btnExtractAll.Enabled = !busy && _reader.IsLoaded;
			_txtSearch.Enabled = !busy;
			_tree.Enabled = !busy;
			if (status != null) _status.Text = status;
			Cursor = busy ? Cursors.AppStarting : Cursors.Default;
		}

		private static string FormatSize(long bytes)
		{
			if (bytes <= 0) return "";
			string[] u = { "B", "KB", "MB", "GB" };
			double v = bytes; int i = 0;
			while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
			return (i == 0 ? bytes.ToString() : v.ToString("0.0")) + " " + u[i];
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);
			try { _split.SplitterDistance = 280; } catch { }
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			if (!string.IsNullOrEmpty(_initialClientPath))
			{
				var p = _initialClientPath;
				_initialClientPath = null;
				LoadClient(p);
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			_cts?.Cancel();
			_reader.Dispose();
			base.OnFormClosing(e);
		}
		#endregion
	}
}
