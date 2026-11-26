namespace DiskSpaceMonitor;

/// <summary>
/// Treemap of a WSL filesystem or a Windows drive.
/// Click selects (and pins the path bar), double-click enters a directory,
/// right-click or Backspace goes up, double-click on the path bar or Ctrl+C copies the path.
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
    private static readonly Color BarColor = Color.FromArgb(32, 32, 36);
    private const string CopyHintText = "двойной щелчок по пути — копировать";

    private readonly TreemapControl map = new() { Dock = DockStyle.Fill };
    private readonly DataGridView contents = Grid();
    private readonly DataGridView types = Grid();
    private readonly ComboBox sourceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(3, 5, 3, 3) };
    private readonly Button upButton = new() { Text = "↑ Вверх", AutoSize = true, Enabled = false };
    private readonly Button scanButton = new() { Text = "Сканировать", AutoSize = true };
    private readonly Button adminButton = new() { Text = "Сканировать с правами администратора", AutoSize = true, Visible = false };
    private readonly Label crumb = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0), Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold) };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Right, Width = 160, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label notice = new() { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = Color.FromArgb(255, 243, 205), ForeColor = Color.FromArgb(102, 77, 3), Visible = false, AutoEllipsis = true };
    private readonly TextBox nameBox = new() { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = BarColor, ForeColor = Color.White, Dock = DockStyle.Top, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false };
    private readonly TextBox pathBox = new() { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = BarColor, ForeColor = Color.FromArgb(190, 200, 215), Dock = DockStyle.Bottom, Font = new Font("Segoe UI", 9.5f), Cursor = Cursors.Hand, TabStop = false };
    private readonly Label sizeLabel = new() { Dock = DockStyle.Right, AutoSize = false, Width = 230, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.Gainsboro, BackColor = BarColor, Font = new Font("Segoe UI", 10f) };
    private readonly Label copyHint = new() { Dock = DockStyle.Right, Width = 250, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Silver, BackColor = BarColor, Text = CopyHintText };
    private readonly Button copyButton = new() { Text = "Копировать путь", Dock = DockStyle.Right, Width = 130, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(0, 102, 204) };
    private readonly SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer hintReset = new() { Interval = 2500 };
    private readonly Dictionary<string, Color> extensionColors = new(StringComparer.Ordinal);
    private readonly HashSet<MapSource> autoScanned = new();
    private MapSource? source;
    private DiskTreeSnapshot? snapshot;
    private DiskTreeNode? current;
    private DiskTreeNode? hovered;
    private DateTime lastTreeWrite;
    private long lastTreeLength = -1;
    private string? loadError;
    private bool fillingGrid;

    public DiskMapForm(IReadOnlyList<MapSource> sources)
    {
        Text = "Disk Space Monitor — карта диска · " + AppInfo.Version;
        Width = 1320; Height = 860; MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(4), WrapContents = false };
        toolbar.Controls.AddRange(new Control[] { sourceBox, upButton, scanButton, adminButton, crumb });
        var statusRow = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 2, 8, 2) };
        statusRow.Controls.Add(status); statusRow.Controls.Add(progressBar);

        var pathBar = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = BarColor, Padding = new Padding(10, 0, 0, 0) };
        var pathHolder = new Panel { Dock = DockStyle.Fill, BackColor = BarColor, Padding = new Padding(0, 6, 8, 6) };
        pathHolder.Controls.Add(nameBox); pathHolder.Controls.Add(pathBox);
        var copyMenu = new ContextMenuStrip();
        copyMenu.Items.Add("Копировать путь", null, (_, _) => CopyPath());
        copyMenu.Items.Add("Копировать имя", null, (_, _) => CopyPath(nameOnly: true));
        nameBox.ContextMenuStrip = pathBox.ContextMenuStrip = copyMenu;
        pathBar.Controls.Add(pathHolder); pathBar.Controls.Add(sizeLabel); pathBar.Controls.Add(copyHint); pathBar.Controls.Add(copyButton);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var contentsTab = new TabPage("Содержимое"); contentsTab.Controls.Add(contents);
        var typesTab = new TabPage("Типы файлов"); typesTab.Controls.Add(types);
        tabs.TabPages.Add(contentsTab); tabs.TabPages.Add(typesTab);
        split.Panel1.Controls.Add(map);
        split.Panel2.Controls.Add(tabs);
        Controls.Add(split); Controls.Add(pathBar); Controls.Add(notice); Controls.Add(statusRow); Controls.Add(toolbar);

        contents.Columns.AddRange(Col("Имя", 50), Col("Размер", 18), Col("%", 12), Col("Файлов", 20));
        types.Columns.AddRange(Col("", 6), Col("Тип", 34), Col("Размер", 22), Col("%", 14), Col("Файлов", 24));

        map.ColorFor = ColorFor;
        map.HoverChanged += node => { hovered = node; ShowPath(); };
        map.NodeClicked += Select;
        map.NodeDoubleClicked += EnterDirectory;
        map.UpRequested += Up;
        upButton.Click += (_, _) => Up();
        scanButton.Click += (_, _) => StartScan(tryAdmin: true);
        adminButton.Click += (_, _) => StartScan(tryAdmin: true);
        copyButton.Click += (_, _) => CopyPath();
        pathBox.DoubleClick += (_, _) => CopyPath();
        nameBox.DoubleClick += (_, _) => CopyPath();
        sizeLabel.DoubleClick += (_, _) => CopyPath();
        hintReset.Tick += (_, _) => { hintReset.Stop(); copyHint.ForeColor = Color.Silver; copyHint.Text = CopyHintText; };
        contents.SelectionChanged += (_, _) => { if (!fillingGrid && contents.CurrentRow?.Tag is DiskTreeNode node) { map.Selected = node; ShowPath(); } };
        contents.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && contents.Rows[e.RowIndex].Tag is DiskTreeNode { IsDirectory: true } node) Navigate(node); };
        KeyDown += OnKeyDown;
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) => split.SplitterDistance = (int)(split.Width * 0.66);
        VisibleChanged += (_, _) => { poll.Enabled = Visible; if (Visible) Tick(); };
        poll.Tick += (_, _) => Tick();

        foreach (var item in sources) sourceBox.Items.Add(item);
        sourceBox.SelectedIndexChanged += (_, _) => SwitchTo(sourceBox.SelectedItem as MapSource);
        if (sourceBox.Items.Count > 0) sourceBox.SelectedIndex = 0;
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Back && !pathBox.Focused && !nameBox.Focused) { Up(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.C && pathBox.SelectionLength == 0 && nameBox.SelectionLength == 0) { CopyPath(); e.Handled = true; }
    }

    private void SwitchTo(MapSource? next)
    {
        source = next;
        snapshot = null; current = null; hovered = null; loadError = null;
        lastTreeWrite = default; lastTreeLength = -1;
        map.Root = null;
        contents.Rows.Clear(); types.Rows.Clear();
        crumb.Text = "";
        Tick();
    }

    /// <summary>Called every second while visible, and on each monitor refresh.</summary>
    public void Tick()
    {
        if (source is null) return;
        ReloadIfChanged();
        if (snapshot is null && loadError is null && source.AutoScanWhenEmpty && !source.Running && autoScanned.Add(source)) StartScan(tryAdmin: false);
        UpdateStatus();
    }

    private void StartScan(bool tryAdmin)
    {
        if (source is null || source.Running) return;
        try { source.StartScan(tryAdmin); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось запустить сканирование", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        UpdateStatus();
    }

    private void ReloadIfChanged()
    {
        if (source is null) return;
        var file = new FileInfo(source.CachePath);
        if (!file.Exists) return;
        if (file.LastWriteTimeUtc == lastTreeWrite && file.Length == lastTreeLength) return;
        try
        {
            var next = DiskTreeSnapshot.Parse(WslAgentManager.ReadShared(source.CachePath));
            lastTreeWrite = file.LastWriteTimeUtc; lastTreeLength = file.Length;
            loadError = null;
            ApplySnapshot(next);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            loadError = ex.Message; // retried on the next tick; the writer may be mid-replace
        }
    }

    private void ApplySnapshot(DiskTreeSnapshot next)
    {
        snapshot = next;
        extensionColors.Clear();
        // "(без расширения)" competes for a colour like any type: model blobs and cache
        // entries often have no extension and are the biggest blocks on the map.
        var colored = next.Extensions.Where(e => e.Ext is not FileTypes.OtherTypes and not FileTypes.NotFile).Take(Palette.Length).ToList();
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
        ShowPath();
    }

    private void Select(DiskTreeNode node)
    {
        map.Selected = node;
        ShowPath();
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

    /// <summary>The path bar follows the mouse; when the mouse leaves the map it shows the selection.</summary>
    private DiskTreeNode? PathNode => hovered ?? map.Selected ?? current;

    private void ShowPath()
    {
        var node = PathNode;
        nameBox.Text = node is null ? "" : node.Parent is null ? node.FullPath : node.DisplayName;
        pathBox.Text = node?.FullPath ?? "";
        // Long paths: keep the end (the file and its folders) in view rather than the drive prefix.
        pathBox.SelectionStart = pathBox.TextLength;
        pathBox.ScrollToCaret();
        sizeLabel.Text = node is null ? "" : $"{Units.Bytes(node.Size)}   {node.TypeName}";
        copyButton.Enabled = node is not null;
    }

    private void CopyPath(bool nameOnly = false)
    {
        // Pinned selection wins over hover: the mouse is on the bar or button when copying.
        var node = map.Selected ?? hovered ?? current;
        if (node is null) return;
        // Folded and unaccounted blocks are not real paths: copy their directory instead.
        var real = node.Kind is "o" or "u" ? node.Parent ?? node : node;
        var text = nameOnly ? real.Parent is null ? real.FullPath : real.Name : real.FullPath;
        try
        {
            Clipboard.SetText(text);
            copyHint.ForeColor = Color.LightGreen;
            copyHint.Text = nameOnly ? "✓ имя скопировано" : "✓ путь скопирован";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            copyHint.ForeColor = Color.LightCoral;
            copyHint.Text = "буфер обмена занят, повторите";
        }
        hintReset.Stop(); hintReset.Start();
    }

    private void UpdateStatus()
    {
        if (source is null) return;
        var running = source.Running;
        scanButton.Enabled = !running;
        scanButton.Text = snapshot is null ? "Сканировать" : "Сканировать заново";
        progressBar.Visible = running;
        var lowRights = snapshot?.Elevated == false;
        adminButton.Visible = source.CanElevate && lowRights;
        adminButton.Enabled = !running;
        map.PlaceholderText = running ? "Идёт сканирование…" : $"Нет данных для {source.Title.Trim()}.\nНажмите «Сканировать».";
        if (snapshot is null) map.Invalidate();

        var parts = new List<string>();
        if (source.ProgressText is { } progress) parts.Add(progress);
        if (source.LastError is { } error) parts.Add(error);
        if (snapshot is not null)
        {
            var when = snapshot.Timestamp?.LocalDateTime.ToString("g") ?? "—";
            var text = $"Скан {when}: {snapshot.ScannedFiles:N0} файлов, {Units.Bytes(snapshot.ScannedBytes)} за {snapshot.DurationSeconds:F1} с";
            if (snapshot.Elevated == true) text += snapshot.BackupPrivilege == true ? "; администратор (backup)" : "; администратор";
            if (snapshot.ErrorCount > 0) text += $"; нет доступа: {snapshot.ErrorCount:N0}";
            if (snapshot.UnaccountedBytes > 0) text += $"; не учтено {Units.Bytes(snapshot.UnaccountedBytes)}";
            if (snapshot.HardLinkDuplicateBytes > 0) text += $"; жёсткие ссылки (не повторно): {Units.Bytes(snapshot.HardLinkDuplicateBytes)}";
            if (snapshot.CloudFiles > 0) text += $"; в облаке: {snapshot.CloudFiles:N0} файлов, {Units.Bytes(snapshot.CloudBytes)}";
            parts.Add(text);
        }
        else if (loadError is not null) parts.Add("Не удалось прочитать результат: " + loadError);
        status.Text = string.Join("   •   ", parts);

        var noticeText = source.Notice;
        if (noticeText is null && lowRights && !running)
            noticeText = $"⚠ Скан выполнен без прав администратора: {snapshot!.ErrorCount:N0} папок недоступно" +
                (snapshot.UnaccountedBytes > 0 ? $", {Units.Bytes(snapshot.UnaccountedBytes)} не учтено (серый блок)" : "") +
                ". Нажмите «Сканировать с правами администратора», чтобы увидеть всё.";
        notice.Text = noticeText ?? "";
        notice.Visible = noticeText is not null;
    }
}
