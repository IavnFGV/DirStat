namespace DiskSpaceMonitor;

/// <summary>
/// Treemap of the WSL filesystem built from the agent's wsl-tree.json.
/// Click selects, double-click enters a directory, right-click or Backspace goes up.
/// </summary>
internal sealed class DiskMapForm : Form
{
    // Colours are assigned to the biggest file types by share of space.
    private static readonly Color[] Palette =
    {
        Color.FromArgb(78, 121, 167), Color.FromArgb(242, 142, 43), Color.FromArgb(225, 87, 89),
        Color.FromArgb(118, 183, 178), Color.FromArgb(89, 161, 79), Color.FromArgb(237, 201, 72),
        Color.FromArgb(176, 122, 161), Color.FromArgb(255, 157, 167), Color.FromArgb(156, 117, 95),
        Color.FromArgb(107, 154, 196), Color.FromArgb(140, 209, 125), Color.FromArgb(211, 123, 149),
    };
    private static readonly Color OtherTypeColor = Color.FromArgb(150, 155, 165);
    private static readonly Color SmallItemsColor = Color.FromArgb(205, 205, 205);
    private static readonly Color UnaccountedColor = Color.FromArgb(90, 90, 90);

    private readonly string treePath;
    private readonly Func<string> requestScan;
    private readonly TreemapControl map = new() { Dock = DockStyle.Fill };
    private readonly DataGridView contents = Grid();
    private readonly DataGridView types = Grid();
    private readonly Button upButton = new() { Text = "↑ Вверх", AutoSize = true, Enabled = false };
    private readonly Button scanButton = new() { Text = "Сканировать заново", AutoSize = true };
    private readonly Label crumb = new() { AutoSize = true, Padding = new Padding(8, 7, 0, 0), Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold) };
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    private readonly Label hoverInfo = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), AutoEllipsis = true };
    private readonly SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    private readonly Dictionary<string, Color> extensionColors = new(StringComparer.Ordinal);
    private DiskTreeSnapshot? snapshot;
    private DiskTreeNode? current;
    private TreeScanStatus? scan;
    private DateTime lastTreeWrite;
    private long lastTreeLength = -1;
    private string? loadError;
    private bool scanRequested;
    private string? pendingRequest;
    private DateTimeOffset pendingSince;
    private bool fillingGrid;

    /// <param name="requestScan">Writes a scan request and returns its id; throws on failure.</param>
    public DiskMapForm(string treePath, Func<string> requestScan)
    {
        this.treePath = treePath;
        this.requestScan = requestScan;
        Text = "Disk Space Monitor — карта диска WSL";
        Width = 1280; Height = 800; MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4), WrapContents = false };
        toolbar.Controls.AddRange(new Control[] { upButton, scanButton, crumb });
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var contentsTab = new TabPage("Содержимое"); contentsTab.Controls.Add(contents);
        var typesTab = new TabPage("Типы файлов"); typesTab.Controls.Add(types);
        tabs.TabPages.Add(contentsTab); tabs.TabPages.Add(typesTab);
        split.Panel1.Controls.Add(map);
        split.Panel2.Controls.Add(tabs);
        Controls.Add(split); Controls.Add(hoverInfo); Controls.Add(status); Controls.Add(toolbar);

        contents.Columns.AddRange(Col("Имя", 50), Col("Размер", 18), Col("%", 12), Col("Файлов", 20));
        types.Columns.AddRange(Col("", 6), Col("Тип", 34), Col("Размер", 22), Col("%", 14), Col("Файлов", 24));

        map.ColorFor = ColorFor;
        map.PlaceholderText = "Нет данных.\nНажмите «Сканировать заново»: агент WSL обойдёт файловую систему.";
        map.HoverChanged += node => hoverInfo.Text = node is null ? "" : $"{node.FullPath}   {Units.Bytes(node.Size)}   {node.TypeName}";
        map.NodeClicked += Select;
        map.NodeDoubleClicked += EnterDirectory;
        map.UpRequested += Up;
        upButton.Click += (_, _) => Up();
        scanButton.Click += (_, _) => RequestScan();
        contents.SelectionChanged += (_, _) => { if (!fillingGrid && contents.CurrentRow?.Tag is DiskTreeNode node) map.Selected = node; };
        contents.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && contents.Rows[e.RowIndex].Tag is DiskTreeNode { IsDirectory: true } node) Navigate(node); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Back) { Up(); e.Handled = true; } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) => split.SplitterDistance = (int)(split.Width * 0.66);
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
    };

    private static DataGridViewTextBoxColumn Col(string name, float weight) =>
        new() { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable };

    private Color ColorFor(DiskTreeNode node) => node.Kind switch
    {
        "o" => SmallItemsColor,
        "u" => UnaccountedColor,
        _ => node.Extension is not null && extensionColors.TryGetValue(node.Extension, out var color) ? color : OtherTypeColor
    };

    /// <summary>Called on every monitor refresh while the window is visible.</summary>
    public void Tick(TreeScanStatus? scanStatus)
    {
        // Until the agent reports our request, its status still describes the previous scan.
        if (pendingRequest is not null && scanStatus?.RequestId != pendingRequest && DateTimeOffset.UtcNow - pendingSince < TimeSpan.FromMinutes(1))
            scan = new TreeScanStatus { State = "requested" };
        else
        {
            pendingRequest = null; // picked up by the agent, or it is not answering
            scan = scanStatus;
        }
        ReloadIfChanged();
        if (!scanRequested && snapshot is null && loadError is null && scan?.State is null or "idle") RequestScan();
        UpdateStatus();
    }

    private void ReloadIfChanged()
    {
        var file = new FileInfo(treePath);
        if (!file.Exists) return;
        if (file.LastWriteTimeUtc == lastTreeWrite && file.Length == lastTreeLength) return;
        try
        {
            var next = DiskTreeSnapshot.Parse(WslAgentManager.ReadShared(treePath));
            lastTreeWrite = file.LastWriteTimeUtc; lastTreeLength = file.Length;
            loadError = null;
            ApplySnapshot(next);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            loadError = ex.Message; // retried on the next tick; the agent may be mid-replace
        }
    }

    private void ApplySnapshot(DiskTreeSnapshot next)
    {
        snapshot = next;
        extensionColors.Clear();
        // "(без расширения)" competes for a colour like any type: model blobs and cache
        // entries often have no extension and are the biggest blocks on the map.
        var colored = next.Extensions.Where(e => e.Ext is not "(прочие типы)" and not "(не файл)").Take(Palette.Length).ToList();
        for (var i = 0; i < colored.Count; i++) extensionColors[colored[i].Ext] = Palette[i];
        FillTypes();
        Navigate(next.Tree!);
    }

    private void FillTypes()
    {
        types.Rows.Clear();
        if (snapshot is null) return;
        var total = Math.Max(1, snapshot.ScannedBytes);
        foreach (var stat in snapshot.Extensions)
        {
            var row = types.Rows[types.Rows.Add("", stat.Ext, Units.Bytes(stat.Size), (stat.Size * 100.0 / total).ToString("F1"), stat.Count.ToString("N0"))];
            row.Cells[0].Style.BackColor = row.Cells[0].Style.SelectionBackColor =
                extensionColors.TryGetValue(stat.Ext, out var color) ? color : OtherTypeColor;
        }
    }

    private void Navigate(DiskTreeNode node)
    {
        current = node;
        map.Root = node;
        map.Selected = null;
        upButton.Enabled = node.Parent is not null;
        crumb.Text = $"{node.FullPath}  —  {Units.Bytes(node.Size)}";
        fillingGrid = true;
        try
        {
            contents.Rows.Clear();
            var total = Math.Max(1, node.Size);
            foreach (var child in node.Children)
            {
                var files = child.IsDirectory || child.Kind == "o" ? child.FileCount.ToString("N0") : "";
                var row = contents.Rows[contents.Rows.Add(child.IsDirectory ? child.DisplayName + "/" : child.DisplayName,
                    Units.Bytes(child.Size), (child.Size * 100.0 / total).ToString("F1"), files)];
                row.Tag = child;
                if (child.Kind is "o" or "u") row.Cells[0].Style.ForeColor = Color.DimGray;
            }
            contents.ClearSelection();
        }
        finally { fillingGrid = false; }
    }

    private void Select(DiskTreeNode node)
    {
        map.Selected = node;
        if (current is null) return;
        // Highlight the row of the current directory's child that contains the clicked item.
        var top = node.PathFromRoot().FirstOrDefault(n => ReferenceEquals(n.Parent, current));
        fillingGrid = true;
        try
        {
            foreach (DataGridViewRow row in contents.Rows)
                if (ReferenceEquals(row.Tag, top)) { row.Selected = true; contents.CurrentCell = row.Cells[0]; break; }
        }
        finally { fillingGrid = false; }
    }

    private void EnterDirectory(DiskTreeNode node)
    {
        if (current is null) return;
        var top = node.PathFromRoot().FirstOrDefault(n => ReferenceEquals(n.Parent, current));
        if (top is { IsDirectory: true, Children.Count: > 0 }) Navigate(top);
    }

    private void Up()
    {
        if (current?.Parent is { } parent)
        {
            var from = current;
            Navigate(parent);
            Select(from);
        }
    }

    private void RequestScan()
    {
        scanRequested = true;
        try
        {
            pendingRequest = requestScan();
            pendingSince = DateTimeOffset.UtcNow;
            scan = new TreeScanStatus { State = "requested" };
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось запросить сканирование", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var running = scan?.State is "running" or "requested";
        scanButton.Enabled = !running;
        var parts = new List<string>();
        if (scan?.State == "requested") parts.Add("Запрос отправлен, ждём агента WSL…");
        else if (scan?.State == "running") parts.Add($"Сканирование… {scan.Files:N0} файлов, {Units.Bytes(scan.Bytes)}");
        else if (scan?.State == "error") parts.Add("Ошибка сканирования: " + scan.Error);
        if (snapshot is not null)
        {
            var when = snapshot.Timestamp?.LocalDateTime.ToString("G") ?? "—";
            var text = $"Скан {when}: {snapshot.ScannedFiles:N0} файлов, {Units.Bytes(snapshot.ScannedBytes)} за {snapshot.DurationSeconds:F1} с";
            if (snapshot.ErrorCount > 0) text += $"; нет доступа: {snapshot.ErrorCount:N0}";
            if (snapshot.UnaccountedBytes > 0) text += $"; не учтено {Units.Bytes(snapshot.UnaccountedBytes)}";
            parts.Add(text);
        }
        else if (loadError is not null) parts.Add("Не удалось прочитать дерево: " + loadError);
        status.Text = string.Join("   •   ", parts);
    }
}
