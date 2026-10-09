// フォルダジャンプ: アドレスバーの入力からフォルダ（とコマンド）の候補を探して、アドレスバーの下に出す。
// フォルダの索引と訪問の記録（FolderJumpService）・候補の一覧・検索の打ち切りは、ここが持つ。
// 候補を選んだ後どうするか（フォルダを開く・コマンドを実行する）は、イベントで MainForm に任せる
using ImageViewer.App.Chrome;
using ImageViewer.App.Commands;
using ImageViewer.Core.Jump;
using ImageViewer.Core.Navigation;

namespace ImageViewer.App.Jump;

internal sealed class JumpController : IDisposable
{
    private readonly Form _host;
    private readonly TextBox _address;
    private readonly AddressBox _addressBox;
    private readonly ISettingsAccess _settings;
    private readonly Func<string?> _currentFolder;
    private readonly Func<string, int, List<CommandCandidate>> _findCommands;
    private readonly JumpList _list = new();
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 120 };
    private EverythingClient? _everything;
    private CancellationTokenSource? _cts;
    private int _visitsSinceSave;

    /// <summary>フォルダの索引と訪問の記録</summary>
    public FolderJumpService Service { get; } = new(FolderJumpService.DefaultDataDir);

    /// <summary>フォルダの候補がクリックされた</summary>
    public event Action<string>? FolderPicked;

    /// <summary>コマンドの候補がクリックされた</summary>
    public event Action<CommandCandidate>? CommandPicked;

    /// <param name="findCommands">☰ メニューからコマンドの候補を探す（入力, 最大の件数）</param>
    public JumpController(Form host, TextBox address, AddressBox addressBox, ISettingsAccess settings,
        Func<string?> currentFolder, Func<string, int, List<CommandCandidate>> findCommands)
    {
        _host = host;
        _address = address;
        _addressBox = addressBox;
        _settings = settings;
        _currentFolder = currentFolder;
        _findCommands = findCommands;
    }

    /// <summary>候補の一覧をウィンドウに載せ、アドレスバーの入力につなぐ（ウィンドウのフォントを決めた後に呼ぶ）</summary>
    public void Attach()
    {
        _list.Font = _host.Font;
        _list.ItemHeight = _host.Font.Height * 2 + _host.LogicalToDeviceUnits(6);
        _host.Controls.Add(_list);
        _list.BringToFront();
        _list.Picked += (_, path) => FolderPicked?.Invoke(path);
        _list.CommandPicked += (_, command) => CommandPicked?.Invoke(command);
        // 候補をクリックすると一覧にフォーカスが移る。その間はアドレスバーの入力を続け、一覧からも離れたら閉じる
        _addressBox.KeepEditing = () => _list.Focused;
        _list.LostFocus += (_, _) => _host.BeginInvoke(() =>
        {
            if (!_list.Focused && !_address.Focused) Hide();
        });

        // 入力が止まってから検索する（1 文字ごとに走らせない）
        _address.TextChanged += (_, _) =>
        {
            if (!_address.Focused) return;
            _delay.Stop();
            _delay.Start();
        };
        _delay.Tick += async (_, _) =>
        {
            _delay.Stop();
            await UpdateListAsync();
        };
        // 候補をクリックしたときはアドレスバーからフォーカスが移るので、それ以外で外れたときだけ閉じる
        _address.LostFocus += (_, _) => _host.BeginInvoke(() =>
        {
            if (!_list.Focused && !_address.Focused) Hide();
        });

        _host.Shown += (_, _) => _ = Service.StartAsync(_settings.Settings.EffectiveJumpRoots);
    }

    /// <summary>候補の一覧が出ている</summary>
    public bool ListVisible => _list.Visible;

    /// <summary>一覧で選んでいるコマンド（一覧が出ていない・フォルダを選んでいるなら null）</summary>
    public CommandCandidate? SelectedCommand => _list.Visible ? _list.SelectedCommand : null;

    /// <summary>一覧で選んでいるフォルダ（一覧が出ていない・コマンドを選んでいるなら null）</summary>
    public string? SelectedPath => _list.Visible ? _list.SelectedPath : null;

    /// <summary>一覧が出ている間の ↑ ↓ PageUp PageDown で、選んでいる候補を動かす。動かしたら true</summary>
    public bool MoveSelection(Keys key)
    {
        if (!_list.Visible || key is not (Keys.Down or Keys.Up or Keys.PageDown or Keys.PageUp)) return false;
        _list.MoveSelection(key switch
        {
            Keys.Down => 1, Keys.Up => -1, Keys.PageDown => _list.MaxVisibleItems, _ => -_list.MaxVisibleItems,
        });
        return true;
    }

    public void ApplyTheme() => _list.ApplyTheme();

    /// <summary>
    /// アドレスバーの入力から候補を出す: ☰ メニューのコマンド（先に数件）とフォルダ。
    /// 「>」で始めるとコマンドだけ（「>」だけなら全部）。パスらしい入力・今のフォルダのままなら出さない
    /// </summary>
    private async Task UpdateListAsync()
    {
        string query = _address.Text.Trim();
        bool commandsOnly = query.StartsWith('>');
        string text = commandsOnly ? query[1..].Trim() : query;
        if (!_address.Focused || query.Length == 0
            || (!commandsOnly && (FolderListing.LooksLikePath(query) || string.Equals(query, _currentFolder(), StringComparison.OrdinalIgnoreCase))))
        {
            Hide();
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var commands = _findCommands(text, commandsOnly ? int.MaxValue : 5);
        IReadOnlyList<JumpResult> results = Array.Empty<JumpResult>();
        string? message = commandsOnly ? "コマンドが見つかりません" : null;
        if (!commandsOnly)
        {
            try
            {
                (results, message) = await SearchFoldersAsync(text, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // 次の入力で検索し直している
            }
        }
        if (cts.IsCancellationRequested || !_address.Focused) return;

        _list.SetResults(commands, results, message);
        var below = _host.PointToClient(_addressBox.Parent!.PointToScreen(new Point(_addressBox.Left, _addressBox.Bottom)));
        _list.SetBounds(below.X, below.Y + _host.LogicalToDeviceUnits(4), _addressBox.Width, _list.Height);
        _list.Visible = true;
        _list.BringToFront();
    }

    /// <summary>フォルダ名で検索（アドレスバーのフォルダジャンプと「フォルダーへ移動 / コピー」で共通）</summary>
    public async Task<(IReadOnlyList<JumpResult> Results, string? Message)> SearchFoldersAsync(string query, CancellationToken ct)
    {
        IReadOnlyList<string>? extra = null;
        if (_settings.Settings.UseEverything && EverythingClient.IsRunning)
        {
            _everything ??= new EverythingClient();
            extra = await _everything.QueryFoldersAsync(query, 300, TimeSpan.FromSeconds(1)); // 応答が無ければ null → 自前の索引
        }
        var results = await Task.Run(() => Service.Search(query, 30, extra, ct), ct);
        string? message = results.Count > 0 ? null
            : Service.Index == null && Service.IsBuilding ? "フォルダの索引を作成中です…（少し待ってから入力し直してください）"
            : "見つかりません";
        return (results, message);
    }

    /// <summary>候補の一覧を閉じる（進行中の検索は捨てる）</summary>
    public void Hide()
    {
        _cts?.Cancel();
        _list.Visible = false;
        if (!_address.Focused) _addressBox.EndEdit(); // 一覧をクリックしていた: 入力もやめる
    }

    /// <summary>フォルダを開いたのを記録する（よく開くフォルダを候補の上に出すため）。10 回ごとに保存する</summary>
    public void RecordVisit(string folder)
    {
        Service.RecordVisit(folder);
        if (++_visitsSinceSave < 10) return;
        _visitsSinceSave = 0;
        _ = Task.Run(Service.SaveVisits);
    }

    public void Dispose()
    {
        Service.SaveVisits();
        _everything?.Dispose();
        _delay.Dispose();
    }
}
